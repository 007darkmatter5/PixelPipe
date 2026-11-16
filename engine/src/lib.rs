//! PixelPipe engine: load a libretro core, encode its frames, and stream them.

pub mod audio;
pub mod config;
pub mod encoder;
pub mod input;
pub mod library;
pub mod libretro;
pub mod selftest;
pub mod server;
pub mod session;

pub use config::Config;
