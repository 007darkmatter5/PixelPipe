//! Libretro joypad bit positions. The browser sends the same mask.

pub const JOYPAD_B: u32 = 0;
pub const JOYPAD_Y: u32 = 1;
pub const JOYPAD_SELECT: u32 = 2;
pub const JOYPAD_START: u32 = 3;
pub const JOYPAD_UP: u32 = 4;
pub const JOYPAD_DOWN: u32 = 5;
pub const JOYPAD_LEFT: u32 = 6;
pub const JOYPAD_RIGHT: u32 = 7;
pub const JOYPAD_A: u32 = 8;
pub const JOYPAD_X: u32 = 9;
pub const JOYPAD_L: u32 = 10;
pub const JOYPAD_R: u32 = 11;
pub const JOYPAD_MASK_ID: u32 = 256;

pub fn pressed(mask: u32, id: u32) -> bool {
    if id == JOYPAD_MASK_ID {
        return false;
    }
    id < 16 && (mask & (1 << id)) != 0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn right_is_bit_seven() {
        let mask = 1 << JOYPAD_RIGHT;
        assert!(pressed(mask, JOYPAD_RIGHT));
        assert!(!pressed(mask, JOYPAD_LEFT));
        assert!(!pressed(mask, JOYPAD_A));
    }
}
