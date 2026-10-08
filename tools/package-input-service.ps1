<# Publishes the optional service separately from the portable launcher. Does not install it. #>
[CmdletBinding()]
param([switch]$NoSign, [switch]$NoRestore, [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$Version) {
    $project = [xml](Get-Content -LiteralPath (Join-Path $repo 'Loungepad\Loungepad.csproj'))
    $Version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'A three-part release version is required.' }
$work = Join-Path $repo ('artifacts\input-service\' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $work 'package'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$restoreArgs = @()
if ($NoRestore) { $restoreArgs += '--no-restore' }
foreach ($name in 'Loungepad.Service', 'Loungepad.InputAgent') {
    $output = Join-Path $work $name
    dotnet publish (Join-Path $repo "$name\$name.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none "-p:Version=$Version" -o $output --nologo @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "Publishing $name failed" }
    foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse) {
        $relative = $file.FullName.Substring($output.Length + 1)
        # Desktop and service runtime assemblies can have the same names but different
        # builds. Isolate the WPF agent's self-contained runtime rather than merging them.
        $destination = if ($name -eq 'Loungepad.InputAgent') { Join-Path (Join-Path $package 'Agent') $relative } else { Join-Path $package $relative }
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Conflicting shared dependency: $relative"
            }
        } else {
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}
if (!$NoSign) {
    $config = Join-Path $PSScriptRoot 'signing.json'
    if (!(Test-Path -LiteralPath $config)) { throw 'Configure tools\signing.json (see signing.example.json), or use -NoSign for build inspection only.' }
    if (!(Get-Module -ListAvailable ArtifactSigning)) { throw 'Install the ArtifactSigning PowerShell module before packaging.' }
    $cfg = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    $files = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' } | Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid' } | ForEach-Object FullName)
    if ($files.Count) {
        Invoke-ArtifactSigning -Endpoint $cfg.Endpoint -CodeSigningAccountName $cfg.CodeSigningAccountName -CertificateProfileName $cfg.CertificateProfileName -Files ($files -join ',') -FileDigest SHA256 -TimestampRfc3161 'http://timestamp.acs.microsoft.com' -TimestampDigest SHA256 -Description 'Loungepad input service' -DescriptionUrl 'https://loungepad.app'
    }
    foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' }) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid') { throw "Signature verification failed: $($file.Name)" }
        if (!$signature.TimeStamperCertificate) { throw "Missing signing timestamp: $($file.Name)" }
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-input-service.ps1'), (Join-Path $PSScriptRoot 'uninstall-input-service.ps1') -Destination $package
Copy-Item -LiteralPath (Join-Path $repo 'docs\SECURE-INPUT.md'), (Join-Path $repo 'LICENSE'), (Join-Path $repo 'NOTICE') -Destination $package
$catalog = Join-Path $work 'Loungepad.Input.cat'
New-FileCatalog -Path $package -CatalogFilePath $catalog -CatalogVersion 2.0 | Out-Null
if (!$NoSign) {
    Invoke-ArtifactSigning -Endpoint $cfg.Endpoint -CodeSigningAccountName $cfg.CodeSigningAccountName -CertificateProfileName $cfg.CertificateProfileName -Files $catalog -FileDigest SHA256 -TimestampRfc3161 'http://timestamp.acs.microsoft.com' -TimestampDigest SHA256 -Description 'Loungepad input package' -DescriptionUrl 'https://loungepad.app'
    $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalog
    if ($catalogSignature.Status -ne 'Valid' -or !$catalogSignature.TimeStamperCertificate) { throw 'Package catalog signature is invalid or missing a timestamp.' }
}
Copy-Item -LiteralPath $catalog -Destination $package
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
# Older launcher updaters pick the first asset ending in -win-x64.zip. Keep the
# optional service out of that namespace so they select the launcher compatibility zip.
$zip = Join-Path $dist "Loungepad.InputService-v$Version-x64.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -Force
Write-Host "Package: $zip"
Write-Host "Folder: $package"
if ($NoSign) { Write-Warning 'Unsigned inspection build. The installer will reject it.' }
