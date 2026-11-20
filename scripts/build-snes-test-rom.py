#!/usr/bin/env python3
"""Original CC0 SNES LoROM used to prove Snes9x in PixelPipe.

A blue backdrop, a yellow 8x8 sprite, and a square wave is not required:
Snes9x still produces audio samples. Right on joypad 1 moves the sprite
two pixels per frame. The ROM is PixelPipe's own code.
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROM_PATH = ROOT / "roms" / "homebrew" / "pixelpipe-snes-test.sfc"


class Asm:
    def __init__(self, org: int) -> None:
        self.org = org
        self.pos = org
        self.buf = bytearray()
        self.labels: dict[str, int] = {}
        self.fixups: list[tuple[int, str, str]] = []

    def label(self, name: str) -> None:
        if name in self.labels:
            raise SystemExit(f"duplicate label {name}")
        self.labels[name] = self.pos

    def emit(self, *vals: int) -> None:
        for value in vals:
            if not 0 <= value <= 255:
                raise SystemExit(f"byte out of range {value:#x}")
        self.buf.extend(vals)
        self.pos += len(vals)

    def db(self, *vals: int) -> None:
        self.emit(*vals)

    def _rel(self, opcode: int, name: str) -> None:
        self.emit(opcode, 0)
        self.fixups.append((len(self.buf) - 1, name, "rel"))

    def _abs(self, opcode: int, addr: int) -> None:
        self.emit(opcode, addr & 0xFF, (addr >> 8) & 0xFF)

    def _imm16(self, opcode: int, value: int) -> None:
        self.emit(opcode, value & 0xFF, (value >> 8) & 0xFF)

    def sei(self) -> None:
        self.emit(0x78)

    def cli(self) -> None:
        self.emit(0x58)

    def clc(self) -> None:
        self.emit(0x18)

    def xce(self) -> None:
        self.emit(0xFB)

    def rep(self, mask: int) -> None:
        self.emit(0xC2, mask)

    def sep(self, mask: int) -> None:
        self.emit(0xE2, mask)

    def lda8(self, value: int) -> None:
        self.emit(0xA9, value)

    def ldx16(self, value: int) -> None:
        self._imm16(0xA2, value)

    def ldy16(self, value: int) -> None:
        self._imm16(0xA0, value)

    def txs(self) -> None:
        self.emit(0x9B)

    def tcd(self) -> None:
        self.emit(0x5B)

    def pha(self) -> None:
        self.emit(0x48)

    def pla(self) -> None:
        self.emit(0x68)

    def phx(self) -> None:
        self.emit(0xDA)

    def plx(self) -> None:
        self.emit(0xFA)

    def phy(self) -> None:
        self.emit(0x5A)

    def ply(self) -> None:
        self.emit(0x7A)

    def phb(self) -> None:
        self.emit(0x8B)

    def plb(self) -> None:
        self.emit(0xAB)

    def sta(self, addr: int) -> None:
        self._abs(0x8D, addr)

    def stz(self, addr: int) -> None:
        self._abs(0x9C, addr)

    def lda_abs(self, addr: int) -> None:
        self._abs(0xAD, addr)

    def lda_zp(self, addr: int) -> None:
        self.emit(0xA5, addr)

    def and8(self, value: int) -> None:
        self.emit(0x29, value)

    def inc_zp(self, addr: int) -> None:
        self.emit(0xE6, addr)

    def inx(self) -> None:
        self.emit(0xE8)

    def dey(self) -> None:
        self.emit(0x88)

    def cpx16(self, value: int) -> None:
        self._imm16(0xE0, value)

    def bne(self, name: str) -> None:
        self._rel(0xD0, name)

    def beq(self, name: str) -> None:
        self._rel(0xF0, name)

    def jmp(self, name: str) -> None:
        self.emit(0x4C, 0, 0)
        self.fixups.append((len(self.buf) - 2, name, "abs"))

    def wai(self) -> None:
        self.emit(0xCB)

    def rti(self) -> None:
        self.emit(0x40)

    def word(self, name: str) -> None:
        self.emit(0, 0)
        self.fixups.append((len(self.buf) - 2, name, "abs"))

    def pad_to(self, addr: int, fill: int = 0x00) -> None:
        if addr < self.pos:
            raise SystemExit(f"pad {addr:#x} behind {self.pos:#x}")
        self.emit(*([fill] * (addr - self.pos)))

    def finish(self, end: int) -> bytes:
        for index, name, kind in self.fixups:
            target = self.labels[name]
            if kind == "abs":
                self.buf[index] = target & 0xFF
                self.buf[index + 1] = (target >> 8) & 0xFF
            else:
                rel = target - (self.org + index + 1)
                if not -128 <= rel <= 127:
                    raise SystemExit(f"branch {name} out of range ({rel})")
                self.buf[index] = rel & 0xFF
        if self.pos != end:
            raise SystemExit(f"rom ended at {self.pos:#x}, expected {end:#x}")
        return bytes(self.buf)


def build_prg() -> bytearray:
    a = Asm(0x8000)
    a.label("reset")
    a.sei()
    a.clc()
    a.xce()
    a.rep(0x30)
    a.emit(0xA9, 0x00, 0x00)  # LDA #$0000, A is 16-bit
    a.tcd()
    a.sep(0x20)
    a.lda8(0x00)
    a.pha()
    a.plb()
    a.rep(0x10)
    a.ldx16(0x1FFF)
    a.txs()

    a.lda8(0x8F)
    a.sta(0x2100)  # force blank
    a.stz(0x2101)
    a.stz(0x2102)
    a.stz(0x2103)
    a.stz(0x2105)
    a.stz(0x2106)
    a.stz(0x212C)
    a.stz(0x212D)
    a.stz(0x4200)

    # Backdrop blue, then sprite palette 0 color 1 yellow.
    a.stz(0x2121)
    a.lda8(0x00)
    a.sta(0x2122)
    a.lda8(0x7D)
    a.sta(0x2122)
    a.lda8(128)
    a.sta(0x2121)
    a.lda8(0x00)
    a.sta(0x2122)
    a.sta(0x2122)
    a.lda8(0xFF)
    a.sta(0x2122)
    a.lda8(0x03)
    a.sta(0x2122)

    # Sprite tile 0: solid palette index 1.
    a.lda8(0x80)
    a.sta(0x2115)
    a.stz(0x2116)
    a.stz(0x2117)
    # 4bpp: 16 bytes of planes 0/1, then 16 bytes of planes 2/3.
    a.ldy16(8)
    a.label("plane01")
    a.lda8(0xFF)
    a.sta(0x2118)
    a.lda8(0x00)
    a.sta(0x2119)
    a.dey()
    a.bne("plane01")
    a.ldy16(8)
    a.label("plane23")
    a.lda8(0x00)
    a.sta(0x2118)
    a.sta(0x2119)
    a.dey()
    a.bne("plane23")

    # Hide every sprite, then place sprite 0.
    a.stz(0x2102)
    a.stz(0x2103)
    a.ldx16(0)
    a.label("hide")
    a.lda8(0x00)
    a.sta(0x2104)
    a.lda8(0xF0)
    a.sta(0x2104)
    a.lda8(0x00)
    a.sta(0x2104)
    a.sta(0x2104)
    a.inx()
    a.cpx16(0x80)
    a.bne("hide")
    a.ldx16(0)
    a.label("highoam")
    a.stz(0x2104)
    a.inx()
    a.cpx16(32)
    a.bne("highoam")

    a.lda8(96)
    a.emit(0x85, 0x00)  # STA $00 sprite X
    a.stz(0x2102)
    a.stz(0x2103)
    a.lda_zp(0x00)
    a.sta(0x2104)
    a.lda8(96)
    a.sta(0x2104)
    a.lda8(0x00)
    a.sta(0x2104)
    a.lda8(0x30)
    a.sta(0x2104)

    a.lda8(0x0F)
    a.sta(0x2100)
    a.lda8(0x10)
    a.sta(0x212C)
    a.lda8(0x01)  # automatic joypad read, no NMI
    a.sta(0x4200)
    a.label("wait_vbl")
    a.lda_abs(0x4212)
    a.and8(0x80)
    a.beq("wait_vbl")
    a.label("wait_joy")
    a.lda_abs(0x4212)
    a.and8(0x01)
    a.bne("wait_joy")
    # Manual strobe. Bit order is B,Y,Sel,Start,Up,Down,Left,Right.
    a.lda8(0x01)
    a.sta(0x4016)
    a.stz(0x4016)
    a.ldy16(8)
    a.lda8(0x00)
    a.emit(0x85, 0x01)  # STA $01 accumulated buttons
    a.label("readbtn")
    a.lda_abs(0x4016)
    a.emit(0x4A)  # LSR
    a.emit(0x26, 0x01)  # ROL $01
    a.dey()
    a.bne("readbtn")
    a.lda_zp(0x01)
    a.and8(0x01)  # Right landed in bit 0
    a.beq("no_right")
    a.inc_zp(0x00)
    a.inc_zp(0x00)
    a.label("no_right")
    a.stz(0x2102)
    a.stz(0x2103)
    a.lda_zp(0x00)
    a.sta(0x2104)
    a.lda8(96)
    a.sta(0x2104)
    a.lda8(0x00)
    a.sta(0x2104)
    a.lda8(0x30)
    a.sta(0x2104)
    a.label("wait_end")
    a.lda_abs(0x4212)
    a.and8(0x80)
    a.bne("wait_end")
    a.jmp("wait_vbl")

    a.label("nmi")
    a.rti()

    a.pad_to(0xFFEA)
    a.word("nmi")
    a.emit(0, 0)  # native IRQ unused
    a.pad_to(0xFFFC)
    a.word("reset")
    a.emit(0, 0)
    return bytearray(a.finish(0x10000))


def checksum(rom: bytearray) -> None:
    rom[0x7FDC] = 0xFF
    rom[0x7FDD] = 0xFF
    rom[0x7FDE] = 0xFF
    rom[0x7FDF] = 0xFF
    total = sum(rom) & 0xFFFF
    complement = total ^ 0xFFFF
    rom[0x7FDC] = complement & 0xFF
    rom[0x7FDD] = (complement >> 8) & 0xFF
    rom[0x7FDE] = total & 0xFF
    rom[0x7FDF] = (total >> 8) & 0xFF


def header(rom: bytearray) -> None:
    title = b"PIXELPIPE TEST ROM"
    rom[0x7FC0 : 0x7FC0 + 21] = title.ljust(21, b" ")
    rom[0x7FD5] = 0x20  # LoROM
    rom[0x7FD6] = 0x00
    rom[0x7FD7] = 0x05  # 32 KiB
    rom[0x7FD8] = 0x00
    rom[0x7FD9] = 0x01  # USA
    rom[0x7FDA] = 0x00
    rom[0x7FDB] = 0x00
    checksum(rom)


def main() -> None:
    rom = build_prg()
    if len(rom) != 32768:
        raise SystemExit(len(rom))
    header(rom)
    ROM_PATH.parent.mkdir(parents=True, exist_ok=True)
    ROM_PATH.write_bytes(rom)
    print(f"wrote {ROM_PATH} ({len(rom)} bytes) crc32={zlib.crc32(rom) & 0xFFFFFFFF:08x}")


if __name__ == "__main__":
    main()
