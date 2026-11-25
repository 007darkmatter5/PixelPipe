//! PixelPipe engine: load a libretro core, encode its frames, and stream them.

pub mod audio;
pub mod auth;
pub mod config;
pub mod encoder;
pub mod input;
pub mod library;
pub mod libretro;
pub mod play_control;
pub mod selftest;
pub mod server;
#[cfg(windows)]
pub mod service;
pub mod session;

pub use config::Config;
