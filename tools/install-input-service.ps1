#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$SourcePath = $PSScriptRoot, [string]$ExpectedPublisher, [string]$EnableProfile)
$ErrorActionPreference = 'Stop'
$serviceName = 'Loungepad.Service'
$source = (Resolve-Path -LiteralPath $SourcePath).Path
$root = Join-Path $env:ProgramFiles 'Loungepad'
$target = [IO.Path]::GetFullPath((Join-Path $root 'Input'))
if (!$target.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Install target must be inside Program Files.' }
foreach ($path in $root, $target) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing a redirected installation directory: $path" }
}
if ($source -eq $target -or $source.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Extract the package outside its installation directory.' }
if ($source -eq $root -or $root.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The package must not contain the installation directory.' }
$code = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' })
foreach ($required in 'Loungepad.Service.exe', 'Agent\Loungepad.InputAgent.exe', 'Loungepad.Input.dll', 'Agent\Loungepad.Input.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $source $required))) { throw "Incomplete package: $required" }
}
if (Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Package must not contain redirected paths.' }
foreach ($file in $code) {
    if ((Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid') { throw "A trusted Authenticode signature is required: $($file.Name)" }
}
$publisher = (Get-AuthenticodeSignature -LiteralPath (Join-Path $source 'Loungepad.Service.exe')).SignerCertificate.Subject
if ($ExpectedPublisher -and $publisher -ne $ExpectedPublisher) { throw 'The input service publisher does not match Loungepad.' }
$catalog = Join-Path $source 'Loungepad.Input.cat'
$catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalog
if ($catalogSignature.Status -ne 'Valid' -or $catalogSignature.SignerCertificate.Subject -ne $publisher) { throw 'A trusted package catalog from the same publisher is required.' }
if ((Test-FileCatalog -Path $source -CatalogFilePath $catalog -FilesToSkip 'Loungepad.Input.cat') -ne 'Valid') { throw 'Package files do not match the signed catalog.' }
foreach ($own in 'Agent\Loungepad.InputAgent.exe', 'Loungepad.Input.dll', 'Agent\Loungepad.Input.dll') {
    if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $source $own)).SignerCertificate.Subject -ne $publisher) { throw "Publisher mismatch: $own" }
}
$stage = [IO.Path]::GetFullPath((Join-Path $root ('Input.stage-' + [Guid]::NewGuid().ToString('N'))))
$backup = [IO.Path]::GetFullPath((Join-Path $root ('Input.backup-' + [Guid]::NewGuid().ToString('N'))))
foreach ($path in $stage, $backup, $target) {
    if (!$path.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer path escaped the protected root.' }
}
# Secure both the child and its parent: a writable parent could otherwise rename the child.
foreach ($directory in $root, $stage) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $acl.SetOwner($admins)
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
        $sid = [Security.Principal.SecurityIdentifier]::new($entry[0])
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, $entry[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $directory -AclObject $acl
}
if (Test-Path -LiteralPath $target) {
    foreach ($item in Get-ChildItem -LiteralPath $target -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing redirected installed file: $($item.FullName)" }
    }
}
Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -Force
# Verify after copying into an admin-only tree as well, including runtime/dependency JSON.
$stagedSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $stage 'Loungepad.Input.cat')
if ($stagedSignature.Status -ne 'Valid' -or $stagedSignature.SignerCertificate.Subject -ne $publisher) { throw 'Staged catalog signature changed.' }
if ((Test-FileCatalog -Path $stage -CatalogFilePath (Join-Path $stage 'Loungepad.Input.cat') -FilesToSkip 'Loungepad.Input.cat') -ne 'Valid') { throw 'Installed staging files failed catalog verification.' }
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) { Stop-Service -Name $serviceName; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }
Move-Item -LiteralPath $stage -Destination $target
# Explicit native quoting also works under Windows PowerShell 5.1.
$registration = if ($existing) { 'config' } else { 'create' }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $env:SystemRoot 'System32\sc.exe'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.Arguments = $registration + ' ' + $serviceName + ' binPath= "\"' + (Join-Path $target 'Loungepad.Service.exe') + '\"" start= auto obj= LocalSystem'
if (!$existing) { $start.Arguments += ' DisplayName= "Loungepad Controller Input"' }
$registrationProcess = [Diagnostics.Process]::Start($start)
try { $registrationProcess.WaitForExit(); if ($registrationProcess.ExitCode -ne 0) { throw 'Service registration failed' } }
finally { $registrationProcess.Dispose() }
& sc.exe description $serviceName 'Opt-in controller and selected keyboard support on the console, UAC and Windows sign-in desktops.'
if ($LASTEXITCODE -ne 0) { throw 'Service description failed' }
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) { throw 'Service recovery configuration failed' }
$key = 'HKLM:\SOFTWARE\Loungepad\Input'
# New-Item -Force replaces an existing registry key and clears its values.
# CreateSubKey opens it without resetting Enabled, Profile, or other settings.
$settingsKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SOFTWARE\Loungepad\Input')
$settingsKey.Dispose()
if ($null -eq (Get-ItemProperty -LiteralPath $key -Name Enabled -ErrorAction SilentlyContinue)) {
    New-ItemProperty -LiteralPath $key -Name Enabled -Value 0 -PropertyType DWord | Out-Null
}
Start-Service -Name $serviceName
if ($EnableProfile) {
    & (Join-Path $target 'Loungepad.Service.exe') --enable $EnableProfile
    if ($LASTEXITCODE -ne 0) { throw 'Service installed, but enabling input failed.' }
}
Write-Host 'Installed. Configure Controller on UAC and sign-in screens in Loungepad Settings > General.'
