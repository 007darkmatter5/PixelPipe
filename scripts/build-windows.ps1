# Build the native Windows x64 engine, the self-contained Blazor UI, and the installer.
# Requires: Rust (MSVC), .NET 8 SDK, CMake, LLVM (libclang), and optionally Inno Setup 6.
# FFmpeg 6.1.1 shared binaries are downloaded on first run (ffmpeg-next cannot parse FFmpeg 7+).
$ErrorActionPreference = 'Stop'
# PowerShell does not fail the script when cargo or cmake returns non-zero.
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $true
}
# Opus ships a CMakeLists that asks for CMake < 3.5. Current GitHub runners
# ship CMake 4, which refuses that unless this floor is set.
$env:CMAKE_POLICY_VERSION_MINIMUM = '3.5'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Cache = Join-Path $Root '.cache'
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

function Find-Libclang {
    if ($env:LIBCLANG_PATH -and (Test-Path (Join-Path $env:LIBCLANG_PATH 'libclang.dll'))) {
        return $env:LIBCLANG_PATH
    }
    foreach ($dir in @('C:\Program Files\LLVM\bin', 'C:\Program Files (x86)\LLVM\bin')) {
        if (Test-Path (Join-Path $dir 'libclang.dll')) {
            return $dir
        }
    }
    throw 'LLVM libclang.dll was not found. Install LLVM and set LIBCLANG_PATH to its bin directory.'
}

$ffmpegName = 'ffmpeg-6.1.1-full_build-shared'
$zip = Join-Path $Cache "$ffmpegName.zip"
if (-not (Test-Path $zip)) {
    $url = "https://github.com/GyanD/codexffmpeg/releases/download/6.1.1/$ffmpegName.zip"
    Write-Host "download $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
}
$ffmpeg = Join-Path $Cache $ffmpegName
if (-not (Test-Path (Join-Path $ffmpeg 'include\libavcodec\avfft.h'))) {
    Expand-Archive -Path $zip -DestinationPath $Cache -Force
}
if (-not (Test-Path (Join-Path $ffmpeg 'lib\avcodec.lib'))) {
    throw "FFmpeg import libraries were not found in $ffmpeg"
}

$env:LIBCLANG_PATH = Find-Libclang
$env:FFMPEG_DIR = $ffmpeg
$env:PATH = "$(Join-Path $ffmpeg 'bin');$env:PATH"
Write-Host "FFMPEG_DIR=$ffmpeg"
Write-Host "LIBCLANG_PATH=$env:LIBCLANG_PATH"

Push-Location $Root
try {
    cargo build --release --locked --manifest-path engine/Cargo.toml
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed ($LASTEXITCODE)" }
    & (Join-Path $Root 'scripts\fetch-cores.ps1')

    $stage = Join-Path $Root 'dist\windows'
    if (Test-Path $stage) {
        Remove-Item -Recurse -Force $stage
    }
    dotnet publish ui/PixelPipe.Web/PixelPipe.Web.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    $trayOut = Join-Path $Root 'dist\tray-publish'
    dotnet publish ui/PixelPipe.Tray/PixelPipe.Tray.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o $trayOut
    if ($LASTEXITCODE -ne 0) { throw "tray publish failed ($LASTEXITCODE)" }
    Copy-Item (Join-Path $trayOut 'PixelPipe.Tray.exe') $stage
    $updaterOut = Join-Path $Root 'dist\updater-publish'
    dotnet publish ui/PixelPipe.Updater/PixelPipe.Updater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=false -o $updaterOut
    if ($LASTEXITCODE -ne 0) { throw "updater publish failed ($LASTEXITCODE)" }
    Copy-Item (Join-Path $updaterOut 'PixelPipe.Updater.exe') $stage
    Copy-Item (Join-Path $Root 'engine\target\release\pixelpipe-engine.exe') $stage
    Copy-Item (Join-Path $ffmpeg 'bin\*.dll') $stage
    $coresDest = Join-Path $stage 'cores'
    New-Item -ItemType Directory -Force -Path $coresDest | Out-Null
    Copy-Item (Join-Path $Root 'cores\*_libretro.dll') $coresDest
    $demo = Join-Path $stage 'demo-roms'
    New-Item -ItemType Directory -Force -Path (Join-Path $demo 'homebrew') | Out-Null
    Copy-Item (Join-Path $Root 'roms\homebrew\*') (Join-Path $demo 'homebrew')
    Copy-Item (Join-Path $Root 'roms\library.json') $demo
    Set-Content -Path (Join-Path $stage 'PixelPipe.url') -Value "[InternetShortcut]`r`nURL=http://127.0.0.1:8080`r`n" -Encoding ascii

    $engine = Join-Path $Root 'engine\target\release\pixelpipe-engine.exe'
    $data = Join-Path $env:TEMP 'pixelpipe-selftest'
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
    $cases = @(
        @('fceumm_libretro.dll', 'roms\homebrew\pixelpipe-test.nes'),
        @('snes9x_libretro.dll', 'roms\homebrew\pixelpipe-snes-test.sfc'),
        @('genesis_plus_gx_libretro.dll', 'roms\homebrew\pixelpipe-genesis-test.gen')
    )
    foreach ($case in $cases) {
        & $engine --self-test --no-ui --data $data --core (Join-Path $Root "cores\$($case[0])") --rom (Join-Path $Root $case[1])
        if ($LASTEXITCODE -ne 0) {
            $log = Join-Path $env:ProgramData 'PixelPipe\pixelpipe.log'
            if (Test-Path $log) { Get-Content $log -Tail 80 }
            throw "self-test failed for $($case[1])"
        }
    }

    $iscc = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc (Join-Path $Root 'installer\pixelpipe.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
        $built = Get-ChildItem (Join-Path $Root 'dist') -Filter 'PixelPipe-Setup-*.exe' | Select-Object -First 1
        if ($built) { Write-Host "installer: $($built.FullName)" }
    }
    elseif ($env:GITHUB_ACTIONS -eq 'true') {
        throw 'ISCC.exe is required on the CI runner'
    }
    else {
        Write-Warning 'Inno Setup was not found. Staged binaries are in dist\windows.'
    }
}
finally {
    Pop-Location
}
