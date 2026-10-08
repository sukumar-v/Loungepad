<#
.SYNOPSIS
    Builds what gets attached to a GitHub release: Loungepad.exe, and a zip for old updaters.

.DESCRIPTION
    Publishes Loungepad self-contained for win-x64 as a single file, so the download is one exe
    and nothing has to be installed or unpacked first -- no .NET runtime, no folder of two hundred
    DLLs, and no ui folder: the page, the bundled themes and the Vortex extension are embedded
    in the exe. The WebView2 runtime is the one thing that cannot be bundled; Windows 11 ships
    it, and the app says so plainly if it is missing.

    Output, both to attach to the release under exactly these names:
      dist\v<version>\Loungepad.exe                       what people download, and what later builds update from
      dist\v<version>\Loungepad-v<version>-win-x64.zip    only for copies up to 1.6.3, whose updater
                                                          reads nothing but a zip with ui\index.html in it

.PARAMETER Version
    Overrides the version baked into the exe and the zip name. Defaults to <Version> in the
    csproj. Pass the tag you are about to push, without the "v". The updater refuses an exe whose
    built version is not the release's, so the two have to agree.

.PARAMETER NoSign
    Skips signing even when tools\signing.json exists, for a package that is only for testing.

.EXAMPLE
    .\tools\package.ps1
    .\tools\package.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [switch]$NoSign,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'Loungepad\Loungepad.csproj'
$artifacts = Join-Path $repo "artifacts\$Runtime"
$staging = Join-Path $artifacts 'publish'
$zipStaging = Join-Path $artifacts 'zip'
# The name the updater looks for (UpdateService.AssetName): no version in it, so
# releases/latest/download/Loungepad.exe is always the newest.
$assetName = if ($Runtime -eq 'win-x64') { 'Loungepad.exe' } else { "Loungepad-$($Runtime -replace '^win-', '').exe" }

if (-not $Version) {
    $csproj = [xml](Get-Content $project)
    $Version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'No <Version> in the csproj and none passed with -Version.' }
$dist = Join-Path $repo "dist\v$Version"

# A stale staging folder would ship files that are no longer part of the build.
foreach ($dir in $artifacts, $dist) {
    $resolved = [IO.Path]::GetFullPath($dir)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath($repo).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Packaging path escaped the repository.' }
    if (Test-Path -LiteralPath $resolved) {
        foreach ($item in @((Get-Item -LiteralPath $resolved)) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing redirected packaging path: $($item.FullName)" }
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host "Publishing Loungepad $Version ($Runtime, self-contained)..." -ForegroundColor Cyan
$restoreArgs = @()
if ($NoRestore) { $restoreArgs += '--no-restore' }
dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $staging `
    --nologo @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Nothing here is any use to someone running the app.
Get-ChildItem $staging -Include *.pdb, *.xml -Recurse | Remove-Item -Force

# The release is this one file, so anything else in the publish output is something the exe
# would go without: a native DLL that stopped being bundled, or a folder the csproj copies again.
$extra = Get-ChildItem $staging -Recurse -File | Where-Object { $_.Name -ne 'Loungepad.exe' }
if ($extra) { throw "The publish output has files besides Loungepad.exe, which the release would not carry:`n  $($extra.FullName -join "`n  ")" }

# The page, the themes and the Vortex extension are embedded resources (ShippedFiles). The exe
# cannot be inspected from here -- the bundle is compressed -- so the assembly it was made from
# is: every file under those folders has to be in it by name, or the release opens to a black
# screen (no ui), lists no themes, or cannot connect Vortex.
$assembly = Get-ChildItem (Join-Path $repo 'Loungepad\obj\Release') -Recurse -Filter Loungepad.dll |
    Where-Object { $_.Directory.Name -eq $Runtime } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $assembly) { throw "Could not find the Release assembly for $Runtime under Loungepad\obj to check its resources." }
$heap = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($assembly.FullName))
$source = Join-Path $repo 'Loungepad'
$shipped = Get-ChildItem (Join-Path $source 'ui'), (Join-Path $source 'themes'), (Join-Path $source 'vortex-bridge') -Recurse -File
foreach ($file in $shipped) {
    $name = 'shipped\' + $file.FullName.Substring($source.Length + 1)
    # Names in the metadata end in a NUL, so app.js cannot be found inside app.json.
    if (-not $heap.Contains("$name$([char]0)")) { throw "Not embedded in the exe: $name" }
}
foreach ($setup in 'install-input-service.ps1', 'uninstall-input-service.ps1') {
    if (-not $heap.Contains("shipped\setup\$setup$([char]0)")) { throw "Input service setup was not embedded: $setup" }
}
Write-Host "  $($shipped.Count) shipped files embedded" -ForegroundColor Green

# Azure Artifact Signing. tools\signing.json names the account (see signing.example.json) and is
# kept out of the repository, so a contributor's build is simply unsigned; where it exists, a
# failure to sign fails the build rather than shipping an unsigned exe. Loungepad.exe is the only
# executable there is: the native DLLs are bundled inside it. The certificate lasts three days,
# so the timestamp is what keeps the signature valid after that, and it is checked for below.
$signingConfig = Join-Path $PSScriptRoot 'signing.json'
if (-not $NoSign -and (Test-Path $signingConfig)) {
    if (-not (Get-Module -ListAvailable ArtifactSigning)) {
        throw 'Signing needs the ArtifactSigning module: Install-Module ArtifactSigning -Scope CurrentUser'
    }
    $cfg = Get-Content $signingConfig -Raw | ConvertFrom-Json
    $exe = Join-Path $staging 'Loungepad.exe'
    Write-Host "Signing Loungepad.exe ($($cfg.CodeSigningAccountName) / $($cfg.CertificateProfileName))..." -ForegroundColor Cyan
    Invoke-ArtifactSigning `
        -Endpoint $cfg.Endpoint `
        -CodeSigningAccountName $cfg.CodeSigningAccountName `
        -CertificateProfileName $cfg.CertificateProfileName `
        -Files $exe `
        -FileDigest SHA256 `
        -TimestampRfc3161 'http://timestamp.acs.microsoft.com' `
        -TimestampDigest SHA256 `
        -Description 'Loungepad' `
        -DescriptionUrl 'https://loungepad.app'

    $sig = Get-AuthenticodeSignature $exe
    if ($sig.Status -ne 'Valid') { throw "Loungepad.exe is not validly signed: $($sig.Status). $($sig.StatusMessage)" }
    if (-not $sig.TimeStamperCertificate) { throw 'Loungepad.exe was signed without a timestamp; it would stop validating in three days.' }
    Write-Host "  Signed: $($sig.SignerCertificate.Subject)" -ForegroundColor Green
} elseif (-not $NoSign) {
    Write-Host 'No tools\signing.json: Loungepad.exe is NOT signed.' -ForegroundColor Yellow
}

$exeOut = Join-Path $dist $assetName
Copy-Item (Join-Path $staging 'Loungepad.exe') $exeOut

# The zip is only for copies up to 1.6.3. Their updater takes the asset ending -win-x64.zip,
# refuses one without Loungepad.exe and ui\index.html in it, and copies everything it holds over
# the install -- so this one holds the same signed exe, the page's index.html to pass that check,
# and the license and notices (Apache-2.0 section 4). The new exe never reads ui\, and removes
# the old folders from beside itself on its first start (UpdateService.RemoveZipLeftovers).
# Drop it once nobody can still be on 1.6.3 or older.
New-Item -ItemType Directory -Path (Join-Path $zipStaging 'ui') -Force | Out-Null
Copy-Item $exeOut (Join-Path $zipStaging 'Loungepad.exe')
Copy-Item (Join-Path $source 'ui\index.html') (Join-Path $zipStaging 'ui\index.html')
foreach ($doc in 'README.md', 'LICENSE', 'NOTICE') { Copy-Item (Join-Path $repo $doc) $zipStaging }
$zip = Join-Path $dist "Loungepad-v$Version-$Runtime.zip"
Compress-Archive -Path (Join-Path $zipStaging '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host ''
foreach ($out in $exeOut, $zip) {
    $size = '{0:N1} MB' -f ((Get-Item $out).Length / 1MB)
    Write-Host "  $out  ($size)" -ForegroundColor Green
    Write-Host "    SHA-256: $((Get-FileHash $out -Algorithm SHA256).Hash)"
}
Write-Host ''
Write-Host "Attach both to the v$Version release under exactly these names." -ForegroundColor Cyan
