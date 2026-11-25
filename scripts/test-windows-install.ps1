# Install the built setup silently, check the layout and that the UI answers, then uninstall.
# Requires an elevated Windows session. The ROM path is passed with /ROMDIR so the
# post-install script still escapes backslashes when the wizard page is not shown.
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SetupItem = Get-ChildItem (Join-Path $Root 'dist') -Filter 'PixelPipe-Setup-*.exe' |
    Where-Object { $_.Extension -eq '.exe' } |
    Select-Object -First 1
if (-not $SetupItem) {
    throw 'Installer not found under dist (expected PixelPipe-Setup-<version>.exe)'
}
$Setup = $SetupItem.FullName
$Log = Join-Path $Root 'dist\install.log'
$Roms = 'C:\PixelPipeRoms'
$InstallDir = 'C:\Program Files\PixelPipe'
$ConfigPath = 'C:\ProgramData\PixelPipe\config.json'

function Wait-SetupProcess($Process, [int]$Seconds, [string]$Label) {
    # Poll HasExited. Process.WaitForExit(timeout) can sit forever in CI once the
    # installer has started the tray, because the timeout overload then waits on
    # inherited console handles.
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try {
            $Process.Refresh()
            if ($Process.HasExited) {
                return $Process.ExitCode
            }
        }
        catch {
            return 0
        }
        if ((Get-Date) -gt $deadline) {
            & taskkill.exe /F /T /PID $Process.Id | Out-Host
            throw "$Label did not exit within $Seconds seconds"
        }
        Start-Sleep -Seconds 2
    }
}

function Stop-PixelPipe {
    # Stop the tray first so it does not restart the children it supervises.
    foreach ($name in @('PixelPipe.Tray', 'PixelPipe.Web', 'pixelpipe-engine')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

function Wait-PixelPipeUi {
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    try {
        for ($i = 0; $i -lt 40; $i++) {
            try {
                $response = $client.GetAsync('http://127.0.0.1:8080').GetAwaiter().GetResult()
                if ([int]$response.StatusCode -eq 200) {
                    return
                }
            }
            catch {
                Write-Host "waiting for http://127.0.0.1:8080 ($($i + 1))"
            }
            Start-Sleep -Seconds 3
        }
    }
    finally {
        $client.Dispose()
    }
    $engineLog = 'C:\ProgramData\PixelPipe\pixelpipe.log'
    if (Test-Path $engineLog) {
        Write-Host '--- pixelpipe.log (tail) ---'
        Get-Content $engineLog -Tail 80
    }
    Show-InstallLog
    throw 'PixelPipe did not answer on http://127.0.0.1:8080'
}

function Get-PixelPipeUninstallKeys {
    # PowerShell's registry provider treats {GUID} as a wildcard, so Get-ChildItem
    # skips Inno's uninstall key. The .NET registry API reads the name literally.
    $found = @()
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
        if (-not $uninstall) { continue }
        foreach ($name in $uninstall.GetSubKeyNames()) {
            $sub = $uninstall.OpenSubKey($name)
            if (-not $sub) { continue }
            $display = [string]$sub.GetValue('DisplayName')
            $remove = [string]$sub.GetValue('UninstallString')
            if ($display -eq 'PixelPipe' -or $remove -match '\\PixelPipe\\unins\d+\.exe') {
                $found += "$view\$name"
            }
        }
    }
    return $found
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
$installCode = Wait-SetupProcess $install 300 'installer'
if ($installCode -ne 0) {
    Show-InstallLog
    throw "installer exit code $installCode"
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

Wait-PixelPipeUi
Write-Host 'UI responded on http://127.0.0.1:8080'

$installed = @(Get-PixelPipeUninstallKeys)
if ($installed.Count -ne 1) {
    throw "expected one Add/Remove Programs entry after install, found $($installed.Count): $($installed -join ', ')"
}

$saveDir = 'C:\ProgramData\PixelPipe\saves'
New-Item -ItemType Directory -Force -Path $saveDir | Out-Null
$sentinel = Join-Path $saveDir 'upgrade-keep.txt'
Set-Content -Path $sentinel -Value 'keep' -Encoding ascii

$running = @(Get-Process -Name 'PixelPipe.Tray','PixelPipe.Web','pixelpipe-engine' -ErrorAction SilentlyContinue)
if ($running.Count -lt 1) { throw 'PixelPipe was not running before the in-place upgrade' }
Write-Host "upgrade while running: $($running.Name -join ', ')"

$upgradeLog = Join-Path $Root 'dist\install-upgrade.log'
if (Test-Path $upgradeLog) { Remove-Item -Force $upgradeLog }
Write-Host "upgrade in place $Setup"
# No /ROMDIR: the second install must keep config.json. Processes are still running
# so the installer has to close them and the silent startup task starts them again.
$upgrade = Start-Process -FilePath $Setup -ArgumentList @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    '/FORCECLOSEAPPLICATIONS',
    '/TASKS=startup,firewall',
    "/LOG=$upgradeLog"
) -PassThru
$upgradeCode = Wait-SetupProcess $upgrade 300 'upgrade installer'
if ($upgradeCode -ne 0) {
    if (Test-Path $upgradeLog) { Get-Content $upgradeLog -Tail 120 }
    throw "upgrade installer exit code $upgradeCode"
}
if (Test-Path $upgradeLog) {
    $upgradeText = Get-Content $upgradeLog -Raw
    if ($upgradeText -match 'DeleteFile failed|Access is denied') {
        Get-Content $upgradeLog -Tail 80
        throw 'upgrade could not replace a file that was still in use'
    }
}

$cfg2 = Get-Content $ConfigPath -Raw | ConvertFrom-Json
if ($cfg2.roms -ne $Roms) { throw "upgrade changed roms to '$($cfg2.roms)'" }
if ((Get-Content $sentinel -Raw).Trim() -ne 'keep') { throw 'save sentinel was removed by the upgrade' }
$after = @(Get-PixelPipeUninstallKeys)
if ($after.Count -ne 1) {
    throw "upgrade left $($after.Count) Add/Remove Programs entries: $($after -join ', ')"
}
Write-Host "upgrade kept roms=$($cfg2.roms) and one uninstall entry"
Wait-PixelPipeUi
Write-Host 'UI responded again after the in-place upgrade'

Stop-PixelPipe
$unins = Join-Path $InstallDir 'unins000.exe'
if (-not (Test-Path $unins)) { throw "missing $unins" }
Write-Host "silent uninstall $unins"
$remove = Start-Process -FilePath $unins -ArgumentList @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART'
) -PassThru
$removeCode = Wait-SetupProcess $remove 180 'uninstaller'
if ($removeCode -ne 0) {
    throw "uninstaller exit code $removeCode"
}
if (Test-Path $engine) { throw 'engine executable still present after uninstall' }
Write-Host 'silent install test passed'
