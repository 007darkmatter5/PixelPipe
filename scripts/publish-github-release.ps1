# Attach dist\PixelPipe-Setup-<version>.exe and its SHA256 file to a GitHub pre-release.
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
$exeItem = Get-ChildItem (Join-Path $root 'dist') -Filter 'PixelPipe-Setup-*.exe' |
    Where-Object { $_.Extension -eq '.exe' } |
    Select-Object -First 1
if (-not $exeItem) {
    throw 'Installer was not built (expected dist\PixelPipe-Setup-<version>.exe)'
}
$exe = $exeItem.FullName
$sum = "$exe.sha256"
$hash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash.ToLowerInvariant()
Set-Content -Path $sum -Value "$hash  $($exeItem.Name)" -Encoding ascii
Write-Host "sha256 $hash $($exeItem.Name)"

if ($tag -eq 'v0.2.0-preview') {
    $title = 'PixelPipe 0.2.0 preview (Windows)'
    $notes = @"
NES, SNES, and Genesis play end to end, including save states. NVENC is selected automatically when the NVIDIA encoder opens, and the fallback chain remains in place. The installer accepts a UNC ROM folder such as \\unas-pro\roms and can start PixelPipe when you sign in.

NVENC on a real NVIDIA GPU, and this installer on a real Windows 11 desktop, still need verification.

SHA256 is in PixelPipe-Setup.exe.sha256.
"@
}
elseif ($tag -eq 'v0.3.0-preview') {
    $title = 'PixelPipe 0.3.0 preview (Windows)'
    $notes = @"
Sign in is required. The first visit creates an admin account. Admins manage players. Save states and battery RAM are stored per person and per game, so two players do not share progress.

The browser talks only to port 8080. Signaling is proxied to the engine. Remote play through a Cloudflare Tunnel uses TURN, including Cloudflare Realtime TURN. On the LAN a direct connection still wins.

The download is PixelPipe-Setup-0.3.0.exe. Installing it over an existing copy keeps config.json and saves. See docs/remote-access-cloudflare.md.

SHA256 is in PixelPipe-Setup-0.3.0.exe.sha256.
"@
}
elseif ($tag -eq 'v0.2.3-preview') {
    $title = 'PixelPipe 0.2.3 preview (Windows)'
    $notes = @"
The installer accepts a typed mapped-drive path such as M:\Games\ROMs even though Browse, which runs elevated, may not list that drive. Settings has a folder browser that runs as the signed-in user, saves the path, and stores the UNC form when Windows can resolve it.

The download is PixelPipe-Setup-0.2.3.exe. Installing it over an existing PixelPipe keeps the same Add/Remove Programs entry, config.json, and saves, and restarts the tray.

SHA256 is in PixelPipe-Setup-0.2.3.exe.sha256.
"@
}
elseif ($tag -eq 'v0.2.2-preview') {
    $title = 'PixelPipe 0.2.2 preview (Windows)'
    $notes = @"
Sign-in is the default start mode. The ROM page has Browse and still accepts a pasted UNC path such as \\unas-pro\roms. An upgrade keeps the existing library config and saves.

PixelPipe runs from the notification area with no console window. The bundled NES, SNES, and Genesis test ROMs stay in the library when the ROM folder is empty. Scanning walks subfolders and accepts zip archives and system folder names such as nes, snes, sfc, genesis, mega drive, and md.

Settings and the tray can check GitHub releases, including previews, verify PixelPipe-Setup.exe.sha256, and install the update. A private repo needs a token saved in Settings.

SHA256 is in PixelPipe-Setup.exe.sha256.
"@
}
elseif ($tag -eq 'v0.2.1-preview') {
    $title = 'PixelPipe 0.2.1 preview (Windows)'
    $notes = @"
Fixes the installer crash (Inno Setup runtime error Type mismatch) that appeared as soon as the wizard opened.

NES, SNES, and Genesis play end to end, including save states. NVENC is selected automatically when the NVIDIA encoder opens, and the fallback chain remains in place. The installer accepts a UNC ROM folder such as \\unas-pro\roms and can start PixelPipe when you sign in.

The silent install is exercised on the GitHub Windows runner. NVENC on a real NVIDIA GPU still needs verification.

SHA256 is in PixelPipe-Setup.exe.sha256.
"@
}
else {
    $title = "PixelPipe $tag"
    $notes = @"
Windows installer for $tag.

SHA256 is in $($exeItem.Name).sha256. This is a pre-release.
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
