#!/usr/bin/env python3
"""Build the original PixelPipe NES test ROM and its box art.

The ROM is written for this project (CC0). It is NROM, 16KB PRG + 8KB CHR:
a pulsing blue field and a 16x16 yellow cursor that moves 3px per frame with
the D-pad. A recolors the cursor and flashes the field. Direction changes the
square-wave pitch so the audio callback is audible.

No third-party ROM data is embedded.
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROM_PATH = ROOT / "roms" / "homebrew" / "pixelpipe-test.nes"
ART_PATH = ROOT / "roms" / "boxart" / "pixelpipe-test.png"


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
        for v in vals:
            if not 0 <= v <= 255:
                raise SystemExit(f"byte out of range {v}")
        self.buf.extend(vals)
        self.pos += len(vals)

    def _rel(self, opcode: int, name: str) -> None:
        self.emit(opcode, 0)
        self.fixups.append((len(self.buf) - 1, name, "rel"))

    def _abs_ref(self, opcode: int, name: str) -> None:
        self.emit(opcode, 0, 0)
        self.fixups.append((len(self.buf) - 2, name, "abs"))

    def lda_imm(self, v: int) -> None:
        self.emit(0xA9, v)

    def ldx_imm(self, v: int) -> None:
        self.emit(0xA2, v)

    def ldy_imm(self, v: int) -> None:
        self.emit(0xA0, v)

    def sta_zp(self, a: int) -> None:
        self.emit(0x85, a)

    def lda_zp(self, a: int) -> None:
        self.emit(0xA5, a)

    def sta_abs(self, a: int) -> None:
        self.emit(0x8D, a & 0xFF, (a >> 8) & 0xFF)

    def lda_abs(self, a: int) -> None:
        self.emit(0xAD, a & 0xFF, (a >> 8) & 0xFF)

    def bit_abs(self, a: int) -> None:
        self.emit(0x2C, a & 0xFF, (a >> 8) & 0xFF)

    def sta_abs_x(self, a: int) -> None:
        self.emit(0x9D, a & 0xFF, (a >> 8) & 0xFF)

    def lda_abs_x(self, name: str) -> None:
        self._abs_ref(0xBD, name)

    def jmp(self, name: str) -> None:
        self._abs_ref(0x4C, name)

    def bne(self, name: str) -> None:
        self._rel(0xD0, name)

    def beq(self, name: str) -> None:
        self._rel(0xF0, name)

    def bpl(self, name: str) -> None:
        self._rel(0x10, name)

    def cmp_zp(self, a: int) -> None:
        self.emit(0xC5, a)

    def word(self, name: str) -> None:
        self.emit(0x00, 0x00)
        self.fixups.append((len(self.buf) - 2, name, "abs"))

    def db(self, *vals: int) -> None:
        self.emit(*vals)

    def pad_to(self, addr: int, fill: int = 0x00) -> None:
        if addr < self.pos:
            raise SystemExit(f"pad target {addr:#x} is behind {self.pos:#x}")
        self.emit(*([fill] * (addr - self.pos)))

    def finish(self, end: int) -> bytes:
        for index, name, kind in self.fixups:
            if name not in self.labels:
                raise SystemExit(f"missing label {name}")
            target = self.labels[name]
            if kind == "abs":
                self.buf[index] = target & 0xFF
                self.buf[index + 1] = (target >> 8) & 0xFF
            elif kind == "rel":
                next_addr = self.org + index + 1
                rel = target - next_addr
                if not -128 <= rel <= 127:
                    raise SystemExit(f"branch to {name} out of range ({rel})")
                self.buf[index] = rel & 0xFF
            else:
                raise SystemExit(kind)
        if self.pos != end:
            raise SystemExit(f"prg ended at {self.pos:#x}, expected {end:#x}")
        return bytes(self.buf)


def build_prg() -> bytes:
    a = Asm(0xC000)

    a.label("reset")
    a.emit(0x78)  # SEI
    a.emit(0xD8)  # CLD
    a.ldx_imm(0xFF)
    a.emit(0x9A)  # TXS
    a.lda_imm(0x00)
    a.sta_abs(0x2000)
    a.sta_abs(0x2001)

    a.bit_abs(0x2002)
    a.label("wait1")
    a.bit_abs(0x2002)
    a.bpl("wait1")
    a.label("wait2")
    a.bit_abs(0x2002)
    a.bpl("wait2")

    a.lda_imm(0x00)
    a.ldx_imm(0x00)
    a.label("clram")
    a.sta_abs_x(0x0000)
    a.sta_abs_x(0x0100)
    a.sta_abs_x(0x0200)
    a.sta_abs_x(0x0300)
    a.sta_abs_x(0x0400)
    a.sta_abs_x(0x0500)
    a.sta_abs_x(0x0600)
    a.sta_abs_x(0x0700)
    a.emit(0xE8)  # INX
    a.bne("clram")

    a.lda_imm(0xFF)
    a.ldx_imm(0x00)
    a.label("hidespr")
    a.sta_abs_x(0x0200)
    a.emit(0xE8, 0xE8, 0xE8, 0xE8)  # INX x4
    a.bne("hidespr")

    a.lda_imm(0x3F)
    a.sta_abs(0x2006)
    a.lda_imm(0x00)
    a.sta_abs(0x2006)
    a.ldx_imm(0x00)
    a.label("palloop")
    a.lda_abs_x("palette")
    a.sta_abs(0x2007)
    a.emit(0xE8)  # INX
    a.emit(0xE0, 0x20)  # CPX #$20
    a.bne("palloop")

    a.lda_imm(0x20)
    a.sta_abs(0x2006)
    a.lda_imm(0x00)
    a.sta_abs(0x2006)
    a.ldy_imm(0x03)
    a.ldx_imm(0x00)
    a.lda_imm(0x01)
    a.label("nt1")
    a.sta_abs(0x2007)
    a.emit(0xE8)
    a.bne("nt1")
    a.emit(0x88)  # DEY
    a.bne("nt1")
    a.ldx_imm(0xC0)
    a.label("nt2")
    a.sta_abs(0x2007)
    a.emit(0xCA)  # DEX
    a.bne("nt2")

    a.lda_imm(0x23)
    a.sta_abs(0x2006)
    a.lda_imm(0xC0)
    a.sta_abs(0x2006)
    a.ldx_imm(0x40)
    a.lda_imm(0x00)
    a.label("atloop")
    a.sta_abs(0x2007)
    a.emit(0xCA)
    a.bne("atloop")

    a.lda_imm(120)
    a.sta_zp(0x01)  # sprite x
    a.lda_imm(96)
    a.sta_zp(0x02)  # sprite y

    a.lda_imm(0x80)
    a.sta_abs(0x2000)
    a.lda_imm(0x1E)
    a.sta_abs(0x2001)

    a.lda_imm(0x01)
    a.sta_abs(0x4015)
    a.lda_imm(0xBF)
    a.sta_abs(0x4000)
    a.lda_imm(0x00)
    a.sta_abs(0x4001)
    a.lda_imm(0xFD)
    a.sta_abs(0x4002)
    a.sta_zp(0x19)
    a.lda_imm(0x00)
    a.sta_abs(0x4003)

    a.label("forever")
    a.jmp("forever")

    a.label("nmi")
    a.emit(0x48)  # PHA
    a.emit(0x8A)  # TXA
    a.emit(0x48)  # PHA
    a.emit(0x98)  # TYA
    a.emit(0x48)  # PHA

    a.lda_imm(0x00)
    a.sta_abs(0x2003)
    a.lda_imm(0x02)
    a.sta_abs(0x4014)

    a.lda_imm(0x01)
    a.sta_abs(0x4016)
    a.lda_imm(0x00)
    a.sta_abs(0x4016)
    for zp, _name in (
        (0x10, "A"),
        (0x11, "B"),
        (0x12, "Select"),
        (0x13, "Start"),
        (0x14, "Up"),
        (0x15, "Down"),
        (0x16, "Left"),
        (0x17, "Right"),
    ):
        a.lda_abs(0x4016)
        a.emit(0x29, 0x01)  # AND #$01
        a.sta_zp(zp)

    a.lda_zp(0x14)
    a.beq("no_up")
    a.lda_zp(0x02)
    a.emit(0x38)  # SEC
    a.emit(0xE9, 0x03)  # SBC #$03
    a.sta_zp(0x02)
    a.label("no_up")

    a.lda_zp(0x15)
    a.beq("no_down")
    a.lda_zp(0x02)
    a.emit(0x18)  # CLC
    a.emit(0x69, 0x03)  # ADC #$03
    a.sta_zp(0x02)
    a.label("no_down")

    a.lda_zp(0x16)
    a.beq("no_left")
    a.lda_zp(0x01)
    a.emit(0x38)
    a.emit(0xE9, 0x03)
    a.sta_zp(0x01)
    a.label("no_left")

    a.lda_zp(0x17)
    a.beq("no_right")
    a.lda_zp(0x01)
    a.emit(0x18)
    a.emit(0x69, 0x03)
    a.sta_zp(0x01)
    a.label("no_right")

    a.lda_zp(0x10)
    a.beq("not_a")
    a.lda_imm(0x01)
    a.jmp("have_attr")
    a.label("not_a")
    a.lda_imm(0x00)
    a.label("have_attr")
    a.sta_zp(0x18)

    # Four 8x8 sprites make a 16x16 cursor.
    def sprite(index: int, y_off: int, x_off: int) -> None:
        base = 0x0200 + index * 4
        if y_off == 0:
            a.lda_zp(0x02)
        else:
            a.lda_zp(0x02)
            a.emit(0x18)
            a.emit(0x69, y_off)
        a.sta_abs(base)
        a.lda_imm(0x01)
        a.sta_abs(base + 1)
        a.lda_zp(0x18)
        a.sta_abs(base + 2)
        if x_off == 0:
            a.lda_zp(0x01)
        else:
            a.lda_zp(0x01)
            a.emit(0x18)
            a.emit(0x69, x_off)
        a.sta_abs(base + 3)

    sprite(0, 0, 0)
    sprite(1, 0, 8)
    sprite(2, 8, 0)
    sprite(3, 8, 8)

    a.emit(0xE6, 0x00)  # INC frame counter
    a.lda_imm(0x3F)
    a.sta_abs(0x2006)
    a.lda_imm(0x01)
    a.sta_abs(0x2006)
    a.lda_zp(0x10)
    a.beq("no_flash")
    a.lda_imm(0x16)  # red while A is held
    a.jmp("store_color")
    a.label("no_flash")
    a.lda_zp(0x00)
    a.emit(0x29, 0x10)  # AND #$10
    a.beq("col_dim")
    a.lda_imm(0x22)
    a.jmp("store_color")
    a.label("col_dim")
    a.lda_imm(0x12)
    a.label("store_color")
    a.sta_abs(0x2007)

    a.lda_imm(0x00)
    a.sta_abs(0x2005)
    a.sta_abs(0x2005)
    a.lda_imm(0x80)
    a.sta_abs(0x2000)

    a.lda_zp(0x14)
    a.emit(0x05, 0x15)  # ORA $15
    a.emit(0x05, 0x16)  # ORA $16
    a.emit(0x05, 0x17)  # ORA $17
    a.beq("pitch_low")
    a.lda_imm(0x50)
    a.jmp("pitch_set")
    a.label("pitch_low")
    a.lda_imm(0xFD)
    a.label("pitch_set")
    a.cmp_zp(0x19)
    a.beq("pitch_same")
    a.sta_zp(0x19)
    a.sta_abs(0x4002)
    a.lda_imm(0x00)
    a.sta_abs(0x4003)
    a.label("pitch_same")

    a.emit(0x68)  # PLA
    a.emit(0xA8)  # TAY
    a.emit(0x68)  # PLA
    a.emit(0xAA)  # TAX
    a.emit(0x68)  # PLA
    a.emit(0x40)  # RTI

    a.label("irq")
    a.emit(0x40)  # RTI

    a.label("palette")
    a.db(
        0x02, 0x12, 0x22, 0x30,
        0x02, 0x12, 0x22, 0x30,
        0x02, 0x12, 0x22, 0x30,
        0x02, 0x12, 0x22, 0x30,
        0x00, 0x28, 0x16, 0x30,
        0x00, 0x16, 0x28, 0x30,
        0x00, 0x28, 0x16, 0x30,
        0x00, 0x28, 0x16, 0x30,
    )

    a.pad_to(0xFFFA)
    a.word("nmi")
    a.word("reset")
    a.word("irq")
    return a.finish(0x10000)


def build_chr() -> bytes:
    chr_rom = bytearray(8192)
    # Tile 1: solid palette index 1 (plane 0 set, plane 1 clear).
    base = 16
    for i in range(8):
        chr_rom[base + i] = 0xFF
        chr_rom[base + 8 + i] = 0x00
    return bytes(chr_rom)


def build_rom() -> bytes:
    header = bytearray(16)
    header[0:4] = b"NES\x1a"
    header[4] = 1
    header[5] = 1
    prg = build_prg()
    chr_rom = build_chr()
    if len(prg) != 16384 or len(chr_rom) != 8192:
        raise SystemExit("unexpected rom size")
    return bytes(header) + prg + chr_rom


# 5x7 glyphs, public-domain bitmaps written for this poster.
FONT = {
    " ": ["00000"] * 7,
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "B": ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
    "C": ["01111", "10000", "10000", "10000", "10000", "10000", "01111"],
    "D": ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "F": ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
    "G": ["01111", "10000", "10000", "10111", "10001", "10001", "01110"],
    "H": ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    "I": ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
    "L": ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    "M": ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
    "N": ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
    "O": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "S": ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "U": ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    "W": ["10001", "10001", "10001", "10101", "10101", "11011", "10001"],
    "X": ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
    "Y": ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
    "0": ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
    "2": ["01110", "10001", "00001", "00110", "01000", "10000", "11111"],
    "6": ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
}


def text_width(text: str, scale: int) -> int:
    return len(text) * (6 * scale)


def blit_text(buf: bytearray, w: int, text: str, x: int, y: int, scale: int, color: tuple[int, int, int, int]) -> None:
    cursor = x
    for ch in text:
        glyph = FONT[ch]
        for gy, row in enumerate(glyph):
            for gx, bit in enumerate(row):
                if bit != "1":
                    continue
                for sy in range(scale):
                    for sx in range(scale):
                        px = cursor + gx * scale + sx
                        py = y + gy * scale + sy
                        if 0 <= px < w and 0 <= py < (len(buf) // (w * 4)):
                            i = (py * w + px) * 4
                            buf[i : i + 4] = bytes(color)
        cursor += 6 * scale


def build_boxart() -> bytes:
    w, h = 300, 420
    buf = bytearray(w * h * 4)
    for y in range(h):
        for x in range(w):
            i = (y * w + x) * 4
            # Deep indigo field with a faint vertical vignette.
            shade = 18 + (x * 10) // w
            buf[i : i + 4] = bytes((shade, shade, 32 + (y * 12) // h, 255))
            if y % 3 == 2:
                buf[i] = max(0, buf[i] - 6)
                buf[i + 1] = max(0, buf[i + 1] - 6)
                buf[i + 2] = max(0, buf[i + 2] - 4)
    for y in range(0, 78):
        for x in range(w):
            i = (y * w + x) * 4
            buf[i : i + 4] = bytes((92, 64, 210, 255))
    # Yellow cursor, matching the ROM sprite, centered.
    for y in range(150, 230):
        for x in range(110, 190):
            i = (y * w + x) * 4
            buf[i : i + 4] = bytes((20, 180, 248, 255))
    title = "PIXELPIPE"
    blit_text(buf, w, title, (w - text_width(title, 3)) // 2, 22, 3, (255, 255, 255, 255))
    sub = "TEST ROM"
    blit_text(buf, w, sub, (w - text_width(sub, 3)) // 2, 280, 3, (255, 220, 80, 255))
    nes = "NES"
    blit_text(buf, w, nes, (w - text_width(nes, 4)) // 2, 340, 4, (255, 255, 255, 255))
    year = "2026"
    blit_text(buf, w, year, (w - text_width(year, 2)) // 2, 384, 2, (180, 180, 210, 255))

    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw.extend(buf[y * w * 4 : (y + 1) * w * 4])

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def main() -> None:
    rom = build_rom()
    ROM_PATH.parent.mkdir(parents=True, exist_ok=True)
    ART_PATH.parent.mkdir(parents=True, exist_ok=True)
    ROM_PATH.write_bytes(rom)
    ART_PATH.write_bytes(build_boxart())
    print(f"wrote {ROM_PATH} ({len(rom)} bytes) crc32={zlib.crc32(rom) & 0xFFFFFFFF:08x}")
    print(f"wrote {ART_PATH}")


if __name__ == "__main__":
    main()
