#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
$ErrorActionPreference = 'Stop'
if ($UserSid -notmatch '^S-1-5-21-(\d+-){3}\d+$' -and $UserSid -notmatch '^S-1-12-1-(\d+-){3}\d+$') { throw 'Invalid originating user SID.' }

function Remove-XInputUwpFix {
    param($UserRegistry = [Microsoft.Win32.Registry]::Users, $MachineRegistry = [Microsoft.Win32.Registry]::LocalMachine)
    # The upstream installer uses the user's Run entry. Use the originating SID,
    # since an administrator approving UAC may be a different account.
    $executables = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($location in @(@{ Hive = $UserRegistry; Path = "$UserSid\Software\Microsoft\Windows\CurrentVersion\Run" },
            @{ Hive = $MachineRegistry; Path = 'Software\Microsoft\Windows\CurrentVersion\Run' })) {
        $run = $location.Hive.OpenSubKey($location.Path, $true)
        if ($run) {
            try {
                $command = $run.GetValue('XInputUWPFix')
                if ($command -is [string]) {
                    # A startup value is data; never execute a downloaded uninstaller or command.
                    if ($command -match '^\s*"(?<exe>[^"]+\\XInputUWPFix\.exe)"\s*$' -or
                        $command -match '^\s*(?<exe>[A-Za-z]:\\.+\\XInputUWPFix\.exe)\s*$') {
                        [void]$executables.Add($matches.exe)
                    }
                    $run.DeleteValue('XInputUWPFix', $false)
                }
            } finally { $run.Dispose() }
        }
    }
    foreach ($process in @(Get-Process -Name XInputUWPFix -ErrorAction SilentlyContinue)) {
        try {
            $exe = $process.Path
            if ([IO.Path]::GetFileName($exe) -ieq 'XInputUWPFix.exe') {
                [void]$executables.Add($exe)
                $process.Kill()
                if (!$process.WaitForExit(5000)) { throw 'XInputUWPFix did not stop.' }
            }
        } finally { $process.Dispose() }
    }
    foreach ($exe in $executables) {
        # Only the exact helper and its identifiable companion scripts; no recursive
        # deletion of the user's download/project folder, and no redirected paths.
        if ($exe -notmatch '^[A-Za-z]:\\' -or [IO.Path]::GetFileName($exe) -ine 'XInputUWPFix.exe') { continue }
        $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($exe))
        $parent = $directory
        while ($parent) {
            if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing redirected XInputUWPFix cleanup path.' }
            $parent = [IO.Path]::GetDirectoryName($parent)
        }
        foreach ($filename in 'XInputUWPFix.exe', 'install.bat', 'uninstall.bat') {
            $file = Join-Path $directory $filename
            if (!(Test-Path -LiteralPath $file -PathType Leaf)) { continue }
            $item = Get-Item -LiteralPath $file
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected XInputUWPFix file.' }
            if ($filename -ne 'XInputUWPFix.exe') {
                if ($item.Length -gt 16384 -or (Get-Content -LiteralPath $file -Raw) -notmatch '(?i)XInputUWPFix') { continue }
            }
            Remove-Item -LiteralPath $file -Force
        }
    }
    # Return Windows to its default controller-navigation behavior if the legacy
    # disable workaround is present. Other input settings are left intact.
    $mapping = $MachineRegistry.OpenSubKey('SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\ControllerToVKMapping', $true)
    if ($mapping) {
        try { if ($mapping.GetValue('Enabled') -eq 0) { $mapping.DeleteValue('Enabled', $false) } }
        finally { $mapping.Dispose() }
    }
}
$name = 'Loungepad.Service'
$root = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Loungepad'))
$target = [IO.Path]::GetFullPath((Join-Path $root 'Input'))
if (!$target.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstall target must stay inside Program Files.' }
if ((Test-Path -LiteralPath $root) -and ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing redirected uninstall root.' }
# Only this component and GUID-named backups/stages made by its installer.
$targets = @($target)
if (Test-Path -LiteralPath $root) {
    $targets += @(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object { $_.Name -match '^Input\.(backup|stage)-[0-9a-f]{32}$' } | ForEach-Object FullName)
}
foreach ($path in $targets) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (!$resolved.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstall path escaped the protected root.' }
    if (Test-Path -LiteralPath $resolved) {
        foreach ($item in @((Get-Item -LiteralPath $resolved)) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing redirected uninstall path: $($item.FullName)" }
        }
    }
}
$key = 'HKLM:\SOFTWARE\Loungepad\Input'
if (Test-Path -LiteralPath $key) { Set-ItemProperty -LiteralPath $key -Name Enabled -Value 0 }
$service = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($service) {
    try { Stop-Service -Name $name; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
    finally { $service.Dispose(); $service = $null }
    & (Join-Path $env:SystemRoot 'System32\sc.exe') delete $name
    if ($LASTEXITCODE -ne 0) { throw 'Service removal failed' }
}
Remove-XInputUwpFix
foreach ($path in $targets) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }; break }
        catch { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 250 }
    }
}
if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
Write-Host 'Input service and XInputUWPFix startup/helper files removed. Default Windows controller navigation restored. The launcher and its user settings are unchanged.'
