# ROM library

Mount your library here (locally, or a share such as a UniFi UNAS Pro volume). PixelPipe only ships one ROM: `homebrew/pixelpipe-test.nes`, an original NES program written for this project and released CC0. Do not commit commercial ROMs.

Recognized extensions:

- NES: `.nes`, `.unf`, `.unif` → FCEUmm
- SNES: `.sfc`, `.smc` → Snes9x
- Genesis: `.md`, `.gen`, `.smd` → Genesis Plus GX

`library.json` can override the title, developer, year, and description, keyed by the path relative to this folder. Box art is a PNG with the same file name sitting next to the ROM, or `boxart/<name>.png`, or `boxart/<crc32>.png`.

Regenerate the test ROM and poster with `python3 scripts/build-test-rom.py`.
