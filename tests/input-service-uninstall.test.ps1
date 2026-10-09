$ErrorActionPreference = 'Stop'
# Load only the cleanup function; never invoke the actual service/registry uninstaller.
$script = Join-Path $PSScriptRoot '..\tools\uninstall-input-service.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
$function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Remove-XInputUwpFix' }, $false)
. ([scriptblock]::Create($function.Extent.Text))
function Assert($condition, $message) { if (!$condition) { throw $message } }
function FakeKey($values) {
    $key = [pscustomobject]@{ Values = $values }
    $key | Add-Member ScriptMethod GetValue { param($name) $this.Values[$name] }
    $key | Add-Member ScriptMethod DeleteValue { param($name, $ignore) $this.Values.Remove($name) }
    $key | Add-Member ScriptMethod Dispose { }
    return $key
}
function FakeHive($run, $mapping) {
    $hive = [pscustomobject]@{ Run = $run; Mapping = $mapping; Paths = [Collections.Generic.List[string]]::new() }
    $hive | Add-Member ScriptMethod OpenSubKey {
        param($path, $writable)
        $this.Paths.Add($path)
        if ($path.EndsWith('\Run')) { return $this.Run }
        if ($path.EndsWith('\ControllerToVKMapping')) { return $this.Mapping }
        throw "Unexpected registry target $path"
    }
    return $hive
}
$testRoot = Join-Path $PSScriptRoot ('..\artifacts\uninstall-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$testRoot = (Resolve-Path -LiteralPath $testRoot).Path
try {
    $UserSid = 'S-1-5-21-1-2-3-1001'
    $exe = Join-Path $testRoot 'XInputUWPFix.exe'
    Set-Content -LiteralPath $exe -Value 'fixture; never execute'
    Set-Content -LiteralPath (Join-Path $testRoot 'install.bat') -Value 'reg add Run /v XInputUWPFix'
    Set-Content -LiteralPath (Join-Path $testRoot 'uninstall.bat') -Value 'unrelated script must remain'
    Set-Content -LiteralPath (Join-Path $testRoot 'unrelated.txt') -Value 'keep'
    $run = FakeKey @{ XInputUWPFix = "`"$exe`""; OtherApp = 'keep' }
    $mapping = FakeKey @{ Enabled = 0; OtherSetting = 42 }
    $users = FakeHive $run $null
    $machine = FakeHive $null $mapping
    $script:stopped = $false
    $process = [pscustomobject]@{ Path = $exe }
    $process | Add-Member ScriptMethod Kill { $script:stopped = $true }
    $process | Add-Member ScriptMethod WaitForExit { param($timeout) return $true }
    $process | Add-Member ScriptMethod Dispose { }
    function Get-Process { param($Name, $ErrorAction) Assert ($Name -eq 'XInputUWPFix') 'Unexpected process target'; return $process }
    Remove-XInputUwpFix -UserRegistry $users -MachineRegistry $machine
    Assert $script:stopped 'Helper process was not stopped'
    Assert (!$run.Values.ContainsKey('XInputUWPFix') -and $run.Values.OtherApp -eq 'keep') 'Startup cleanup changed other entries'
    Assert ($users.Paths[0].StartsWith($UserSid + '\')) 'Cleanup did not target the originating user'
    Assert (!(Test-Path -LiteralPath $exe)) 'Helper executable was not removed'
    Assert (!(Test-Path -LiteralPath (Join-Path $testRoot 'install.bat'))) 'Identifiable helper script was not removed'
    Assert (Test-Path -LiteralPath (Join-Path $testRoot 'uninstall.bat')) 'Unrelated script was removed'
    Assert (Test-Path -LiteralPath (Join-Path $testRoot 'unrelated.txt')) 'Unrelated file was removed'
    Assert (!$mapping.Values.ContainsKey('Enabled') -and $mapping.Values.OtherSetting -eq 42) 'Navigation default was not restored narrowly'
    function Get-Process { param($Name, $ErrorAction) }
    # Repeat safely with no installation and an explicit navigation enable value.
    $mapping.Values.Enabled = 1
    Remove-XInputUwpFix -UserRegistry $users -MachineRegistry $machine
    Assert ($mapping.Values.Enabled -eq 1) 'An existing navigation enable value was changed'
    Write-Host 'PASS: uninstall stops helper, removes exact startup/files, restores navigation and preserves unrelated data'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts')).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped artifacts' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
