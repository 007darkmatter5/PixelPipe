# Install the built setup silently, check the layout and that the UI answers, then uninstall.
# Requires an elevated Windows session. The ROM path is passed with /ROMDIR so the
# post-install script still escapes backslashes when the wizard page is not shown.
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Setup = Join-Path $Root 'dist\PixelPipe-Setup.exe'
$Log = Join-Path $Root 'dist\install.log'
$Roms = 'C:\PixelPipeRoms'
$InstallDir = 'C:\Program Files\PixelPipe'
$ConfigPath = 'C:\ProgramData\PixelPipe\config.json'

function Stop-PixelPipe {
    # Stop the tray first so it does not restart the children it supervises.
    foreach ($name in @('PixelPipe.Tray', 'PixelPipe.Web', 'pixelpipe-engine')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

function Show-InstallLog {
    if (Test-Path $Log) {
        Write-Host '--- install.log (tail) ---'
        Get-Content $Log -Tail 120
    }
}

if (-not (Test-Path $Setup)) {
    throw "Installer not found: $Setup"
}

New-Item -ItemType Directory -Force -Path $Roms | Out-Null
if (Test-Path $Log) { Remove-Item -Force $Log }
Stop-PixelPipe

Write-Host "silent install $Setup"
# Do not use Start-Process -Wait. That waits for the whole process tree, including
# the engine the installer launches, so the test would never reach the HTTP check.
$install = Start-Process -FilePath $Setup -ArgumentList @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    '/TASKS=startup,firewall',
    "/LOG=$Log",
    "/ROMDIR=$Roms"
) -PassThru
if (-not $install.WaitForExit(300000)) {
    Show-InstallLog
    Stop-Process -Id $install.Id -Force -ErrorAction SilentlyContinue
    throw 'installer did not exit within 5 minutes'
}
if ($install.ExitCode -ne 0) {
    Show-InstallLog
    throw "installer exit code $($install.ExitCode)"
}
if (Test-Path $Log) {
    $logText = Get-Content $Log -Raw
    if ($logText -match 'Runtime error|Type mismatch') {
        Show-InstallLog
        throw 'installer log contains a Pascal runtime error'
    }
}

$engine = Join-Path $InstallDir 'pixelpipe-engine.exe'
$ui = Join-Path $InstallDir 'PixelPipe.Web.exe'
$tray = Join-Path $InstallDir 'PixelPipe.Tray.exe'
if (-not (Test-Path $engine)) { Show-InstallLog; throw "missing $engine" }
if (-not (Test-Path $ui)) { throw "missing $ui" }
if (-not (Test-Path $tray)) { throw "missing $tray" }
if (-not (Test-Path (Join-Path $InstallDir 'cores\fceumm_libretro.dll'))) { throw 'NES core was not installed' }
if (-not (Test-Path $ConfigPath)) { throw "missing $ConfigPath" }

$cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
if ($cfg.roms -ne $Roms) { throw "config roms is '$($cfg.roms)'" }
if ($cfg.cores -ne (Join-Path $InstallDir 'cores')) { throw "config cores is '$($cfg.cores)'" }
if ($cfg.data -ne 'C:\ProgramData\PixelPipe') { throw "config data is '$($cfg.data)'" }
Write-Host "config.json roms=$($cfg.roms)"

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080' -UseBasicParsing -TimeoutSec 5
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    }
    catch {
        Write-Host "waiting for http://127.0.0.1:8080 ($($i + 1))"
    }
    Start-Sleep -Seconds 3
}
if (-not $ready) {
    $engineLog = 'C:\ProgramData\PixelPipe\pixelpipe.log'
    if (Test-Path $engineLog) {
        Write-Host '--- pixelpipe.log (tail) ---'
        Get-Content $engineLog -Tail 80
    }
    Show-InstallLog
    throw 'PixelPipe did not answer on http://127.0.0.1:8080'
}
Write-Host 'UI responded on http://127.0.0.1:8080'

Stop-PixelPipe
$unins = Join-Path $InstallDir 'unins000.exe'
if (-not (Test-Path $unins)) { throw "missing $unins" }
Write-Host "silent uninstall $unins"
$remove = Start-Process -FilePath $unins -ArgumentList @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART'
) -PassThru
if (-not $remove.WaitForExit(180000)) {
    Stop-Process -Id $remove.Id -Force -ErrorAction SilentlyContinue
    throw 'uninstaller did not exit within 3 minutes'
}
if ($remove.ExitCode -ne 0) {
    throw "uninstaller exit code $($remove.ExitCode)"
}
if (Test-Path $engine) { throw 'engine executable still present after uninstall' }
Write-Host 'silent install test passed'
