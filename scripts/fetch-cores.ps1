# Download Windows x64 libretro cores into .\cores.
# Usage: scripts\fetch-cores.ps1 [core_name ...]
# Default cores: fceumm snes9x genesis_plus_gx
$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Dest = Join-Path $Root 'cores'
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$names = if ($args.Count -gt 0) { $args } else { @('fceumm', 'snes9x', 'genesis_plus_gx') }
foreach ($core in $names) {
    $url = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/${core}_libretro.dll.zip"
    $tmp = Join-Path $env:TEMP "$core-libretro.zip"
    Write-Host "fetch $url"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    Expand-Archive -Path $tmp -DestinationPath $Dest -Force
    Remove-Item $tmp
    Get-ChildItem $Dest -Recurse -Filter "${core}_libretro.dll" | ForEach-Object {
        $target = Join-Path $Dest $_.Name
        if ($_.FullName -ne $target) {
            Move-Item -Force $_.FullName $target
        }
    }
}
Get-ChildItem (Join-Path $Dest '*_libretro.dll') | Select-Object Name, Length
