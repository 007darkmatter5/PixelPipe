# ROM library

Mount your library here (locally, or a share such as a UniFi UNAS Pro volume, including a Windows UNC path). PixelPipe ships three original CC0 ROMs and no commercial games:

- `homebrew/pixelpipe-test.nes` (NES, `scripts/build-test-rom.py`)
- `homebrew/pixelpipe-snes-test.sfc` (SNES, `scripts/build-snes-test-rom.py`)
- `homebrew/pixelpipe-genesis-test.gen` (Genesis, `scripts/build-genesis-test-rom.py`)

Recognized extensions:

- NES: `.nes`, `.unf`, `.unif` → FCEUmm
- SNES: `.sfc`, `.smc` → Snes9x
- Genesis: `.md`, `.gen`, `.smd` → Genesis Plus GX

`library.json` can override the title, developer, year, and description, keyed by the path relative to this folder. Box art is a PNG with the same file name sitting next to the ROM, or `boxart/<name>.png`, or `boxart/<crc32>.png`.

Regenerate a test ROM with the matching `scripts/build-*-test-rom.py` script.
