# Libretro cores

PixelPipe does not vendor emulator binaries. `scripts/fetch-cores.sh` downloads the current Linux libretro buildbot builds (or `PIXELPIPE_CORE_ARCH=x86_64|arm64`). `scripts/fetch-cores.ps1` downloads the Windows x64 DLLs. The engine loads whichever of `.so` / `.dll` is present.

| System | Linux | Windows | Upstream | License |
| --- | --- | --- | --- | --- |
| NES | `fceumm_libretro.so` | `fceumm_libretro.dll` | [libretro/libretro-fceumm](https://github.com/libretro/libretro-fceumm) | GNU GPL v2 |
| SNES | `snes9x_libretro.so` | `snes9x_libretro.dll` | [libretro/snes9x](https://github.com/libretro/snes9x) | Non-commercial / personal use |
| Genesis | `genesis_plus_gx_libretro.so` | `genesis_plus_gx_libretro.dll` | [libretro/Genesis-Plus-GX](https://github.com/libretro/Genesis-Plus-GX) | Non-commercial |

The Docker image downloads the same files at build time. See the repository README for what that means if you redistribute PixelPipe together with a core.
