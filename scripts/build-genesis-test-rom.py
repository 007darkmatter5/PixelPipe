#!/usr/bin/env python3
"""Original CC0 Genesis ROM used to prove Genesis Plus GX in PixelPipe.

Blue backdrop, one yellow 8x8 sprite. Joypad right moves it three pixels
per frame. A PSG tone keeps the audio callback busy.
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROM_PATH = ROOT / "roms" / "homebrew" / "pixelpipe-genesis-test.gen"
SIZE = 64 * 1024


class Asm:
    def __init__(self, org: int) -> None:
        self.org = org
        self.pos = org
        self.buf = bytearray()
        self.labels: dict[str, int] = {}
        self.fixups: list[tuple[int, str]] = []

    def label(self, name: str) -> None:
        self.labels[name] = self.pos

    def emit(self, *words: int) -> None:
        for word in words:
            if not 0 <= word <= 0xFFFF:
                raise SystemExit(f"word out of range {word:#x}")
            self.buf.extend(struct.pack(">H", word))
            self.pos += 2

    def long(self, value: int) -> None:
        self.emit((value >> 16) & 0xFFFF, value & 0xFFFF)

    def branch(self, opcode: int, name: str) -> None:
        # opcode is the 16-bit word branch (BNE.W = 0x6600). Displacement is patched.
        self.emit(opcode, 0)
        self.fixups.append((len(self.buf) - 2, name))

    def bne(self, name: str) -> None:
        self.branch(0x6600, name)

    def beq(self, name: str) -> None:
        self.branch(0x6700, name)

    def bra(self, name: str) -> None:
        self.branch(0x6000, name)

    def finish(self) -> bytes:
        for index, name in self.fixups:
            target = self.labels[name]
            disp = target - (self.org + index)
            if not -32768 <= disp <= 32767:
                raise SystemExit(f"branch {name} out of range")
            self.buf[index] = (disp >> 8) & 0xFF
            self.buf[index + 1] = disp & 0xFF
        return bytes(self.buf)


def vram_write(addr: int) -> int:
    return 0x40000000 | ((addr & 0x3FFF) << 16) | ((addr >> 14) & 0x3)


def build_code() -> bytes:
    a = Asm(0x200)
    a.label("reset")
    a.emit(0x46FC, 0x2700)  # MOVE.W #$2700, SR
    # TMSS: if the hardware version is nonzero, unlock the VDP.
    a.emit(0x1039, 0x00A1, 0x0001)  # MOVE.B ($A10001).l, D0
    a.emit(0x0200, 0x000F)  # ANDI.B #$0F, D0
    a.beq("no_tmss")
    a.emit(0x23FC)  # MOVE.L #'SEGA', ($A14000).l
    a.long(0x53454741)
    a.long(0x00A14000)
    a.label("no_tmss")

    regs = [
        0x8004,
        0x8104,  # display off, mode 5
        0x8230,
        0x833C,
        0x8407,
        0x8578,  # sprites at $F000
        0x8600,
        0x8700,
        0x8800,
        0x8900,
        0x8A00,
        0x8B00,
        0x8C00,  # H32
        0x8D3F,
        0x8E00,
        0x8F02,
        0x9001,
        0x9100,
        0x9200,
    ]
    for reg in regs:
        a.emit(0x33FC, reg)  # MOVE.W #reg, ($C00004).l
        a.long(0x00C00004)

    # CRAM: color 0 blue, color 1 yellow.
    a.emit(0x23FC)
    a.long(0xC0000000)
    a.long(0x00C00004)
    a.emit(0x33FC, 0x0E00)
    a.long(0x00C00000)
    a.emit(0x33FC, 0x00EE)
    a.long(0x00C00000)

    # Tile 1, solid color index 1.
    a.emit(0x23FC)
    a.long(vram_write(0x20))
    a.long(0x00C00004)
    for _ in range(8):
        a.emit(0x23FC)
        a.long(0x11111111)
        a.long(0x00C00000)

    # Sprite 0 at (128, 100). X lives in RAM $FF0000.
    a.emit(0x33FC, 128)  # MOVE.W #128, ($FF0000).l
    a.long(0x00FF0000)
    a.label("draw_sprite")
    a.emit(0x23FC)
    a.long(vram_write(0xF000))
    a.long(0x00C00004)
    a.emit(0x33FC, 128 + 100)
    a.long(0x00C00000)
    a.emit(0x33FC, 0x0000)
    a.long(0x00C00000)
    a.emit(0x33FC, 0x8001)  # priority, tile 1
    a.long(0x00C00000)
    a.emit(0x33F9, 0x00FF, 0x0000)  # MOVE.W ($FF0000).l, ($C00000).l
    a.long(0x00C00000)

    # Display on.
    a.emit(0x33FC, 0x8144)
    a.long(0x00C00004)

    # PSG tone, channel 0, loud.
    a.emit(0x13FC, 0x0080)
    a.long(0x00C00011)
    a.emit(0x13FC, 0x0010)
    a.long(0x00C00011)
    a.emit(0x13FC, 0x0090)
    a.long(0x00C00011)

    # Port 1 TH is an output.
    a.emit(0x13FC, 0x0040)
    a.long(0x00A10009)

    a.label("frame")
    a.label("wait")
    a.emit(0x3039, 0x00C0, 0x0004)  # MOVE.W ($C00004).l, D0
    a.emit(0x0800, 0x0003)  # BTST #3, D0
    a.beq("wait")
    # TH high: bit 3 clear means Right (active low).
    a.emit(0x13FC, 0x0040)
    a.long(0x00A10003)
    a.emit(0x1039, 0x00A1, 0x0003)  # MOVE.B ($A10003).l, D0
    a.emit(0x0800, 0x0003)  # BTST #3, D0
    a.bne("draw_only")
    a.emit(0x5479)  # ADDQ.W #3, ($FF0000).l
    a.long(0x00FF0000)
    a.label("draw_only")
    a.bra("draw_sprite")
    return a.finish()


def build() -> bytes:
    rom = bytearray(SIZE)
    # Vectors. Everything except reset goes to RTE at a tiny stub.
    rom[0:4] = struct.pack(">I", 0x00FFFFF0)
    rom[4:8] = struct.pack(">I", 0x00000200)
    code = build_code()
    rom[0x200 : 0x200 + len(code)] = code
    # RTE stub at 0x100 before the header text? Header occupies 0x100. Put RTE at 0x80.
    rom[0x80:0x82] = struct.pack(">H", 0x4E73)
    for offset in range(0x08, 0x100, 4):
        rom[offset : offset + 4] = struct.pack(">I", 0x00000080)

    rom[0x100:0x110] = b"SEGA GENESIS    "
    rom[0x110:0x120] = b"(C)PIXELPIPE2026"
    rom[0x120:0x150] = b"PIXELPIPE TEST ROM".ljust(48, b" ")
    rom[0x150:0x180] = b"PIXELPIPE TEST ROM".ljust(48, b" ")
    rom[0x180:0x18E] = b"GM 00000000-00"
    rom[0x190:0x1A0] = b"J               "
    rom[0x1A0:0x1A4] = struct.pack(">I", 0)
    rom[0x1A4:0x1A8] = struct.pack(">I", SIZE - 1)
    rom[0x1A8:0x1AC] = struct.pack(">I", 0x00FF0000)
    rom[0x1AC:0x1B0] = struct.pack(">I", 0x00FFFFFF)
    checksum = 0
    for index in range(0x200, SIZE, 2):
        checksum = (checksum + int.from_bytes(rom[index : index + 2], "big")) & 0xFFFF
    rom[0x18E:0x190] = struct.pack(">H", checksum)
    return bytes(rom)


def main() -> None:
    rom = build()
    ROM_PATH.parent.mkdir(parents=True, exist_ok=True)
    ROM_PATH.write_bytes(rom)
    print(f"wrote {ROM_PATH} ({len(rom)} bytes) crc32={zlib.crc32(rom) & 0xFFFFFFFF:08x}")


if __name__ == "__main__":
    main()
