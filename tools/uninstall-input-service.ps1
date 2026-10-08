#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
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
foreach ($path in $targets) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }; break }
        catch { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 250 }
    }
}
if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
Write-Host 'Input service, protected files and machine input settings removed. The launcher and its user settings are unchanged.'
