# Attach dist\PixelPipe-Setup.exe and its SHA256 file to a GitHub pre-release.
# Called from .github/workflows/release-windows.yml with GH_TOKEN and RELEASE_TAG.
$ErrorActionPreference = 'Stop'
$tag = $env:RELEASE_TAG
if ([string]::IsNullOrWhiteSpace($tag)) {
    throw 'RELEASE_TAG is empty'
}
$repo = $env:GITHUB_REPOSITORY
if ([string]::IsNullOrWhiteSpace($repo)) {
    throw 'GITHUB_REPOSITORY is empty'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $root 'dist\PixelPipe-Setup.exe'
$sum = Join-Path $root 'dist\PixelPipe-Setup.exe.sha256'
if (-not (Test-Path $exe)) {
    throw "Installer was not built: $exe"
}

$hash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash.ToLowerInvariant()
Set-Content -Path $sum -Value "$hash  PixelPipe-Setup.exe" -Encoding ascii
Write-Host "sha256 $hash"

if ($tag -eq 'v0.2.0-preview') {
    $title = 'PixelPipe 0.2.0 preview (Windows)'
    $notes = @"
NES, SNES, and Genesis play end to end, including save states. NVENC is selected automatically when the NVIDIA encoder opens, and the fallback chain remains in place. The installer accepts a UNC ROM folder such as \\unas-pro\roms and can start PixelPipe when you sign in.

NVENC on a real NVIDIA GPU, and this installer on a real Windows 11 desktop, still need verification.

SHA256 is in PixelPipe-Setup.exe.sha256.
"@
}
else {
    $title = "PixelPipe $tag"
    $notes = @"
Windows installer for $tag.

SHA256 is in PixelPipe-Setup.exe.sha256. This is a pre-release.
"@
}

$notesFile = Join-Path $env:TEMP 'pixelpipe-release-notes.md'
Set-Content -Path $notesFile -Value $notes -Encoding utf8

gh release view $tag --repo $repo *> $null
$exists = $LASTEXITCODE -eq 0
if ($exists) {
    gh release upload $tag $exe $sum --repo $repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)" }
}
else {
    gh release create $tag $exe $sum --repo $repo --prerelease --title $title --notes-file $notesFile --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
}

gh release view $tag --repo $repo
if ($LASTEXITCODE -ne 0) { throw "release $tag is not visible after publish" }
