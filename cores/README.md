# Libretro cores

PixelPipe does not vendor emulator binaries. `scripts/fetch-cores.sh` downloads the current libretro buildbot builds for this machine (or for `PIXELPIPE_CORE_ARCH=x86_64|arm64`):

| System | File | Upstream | License |
| --- | --- | --- | --- |
| NES | `fceumm_libretro.so` | [libretro/libretro-fceumm](https://github.com/libretro/libretro-fceumm) | GNU GPL v2 |
| SNES | `snes9x_libretro.so` | [libretro/snes9x](https://github.com/libretro/snes9x) | Non-commercial / personal use |
| Genesis | `genesis_plus_gx_libretro.so` | [libretro/Genesis-Plus-GX](https://github.com/libretro/Genesis-Plus-GX) | Non-commercial |

The Docker image downloads the same files at build time. See the repository README for what that means if you redistribute PixelPipe together with a core.
