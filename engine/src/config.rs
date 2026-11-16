use std::path::{Path, PathBuf};

use crate::library::System;

#[derive(Clone, Debug)]
pub struct Config {
    pub roms: PathBuf,
    pub cores: PathBuf,
    pub data: PathBuf,
    pub bind: String,
    pub advertise_ip: Option<String>,
    pub stun: Option<String>,
    pub webrtc_port_min: u16,
    pub webrtc_port_max: u16,
    pub core_nes: Option<PathBuf>,
    pub core_snes: Option<PathBuf>,
    pub core_genesis: Option<PathBuf>,
}

impl Config {
    pub fn system_dir(&self) -> PathBuf {
        self.data.join("system")
    }

    pub fn save_dir(&self) -> PathBuf {
        self.data.join("saves")
    }

    pub fn core_path(&self, system: System) -> PathBuf {
        if let Some(path) = match system {
            System::Nes => self.core_nes.clone(),
            System::Snes => self.core_snes.clone(),
            System::Genesis => self.core_genesis.clone(),
        } {
            return path;
        }
        self.cores.join(system.core_file())
    }

    pub fn ensure_dirs(&self) -> std::io::Result<()> {
        std::fs::create_dir_all(self.system_dir())?;
        std::fs::create_dir_all(self.save_dir())?;
        Ok(())
    }
}

/// Resolve a library-relative ROM path and reject anything that escapes `root`.
pub fn resolve_rom(root: &Path, relative: &str) -> anyhow::Result<PathBuf> {
    if relative.contains('\0') || relative.contains("..") {
        anyhow::bail!("invalid rom path");
    }
    let relative = relative.trim_start_matches(['/', '\\']);
    if relative.is_empty() {
        anyhow::bail!("empty rom path");
    }
    let root = root
        .canonicalize()
        .map_err(|_| anyhow::anyhow!("rom library is not accessible"))?;
    let candidate = root.join(relative);
    let full = candidate
        .canonicalize()
        .map_err(|_| anyhow::anyhow!("rom not found"))?;
    if !full.starts_with(&root) {
        anyhow::bail!("rom path escapes the library");
    }
    if !full.is_file() {
        anyhow::bail!("rom path is not a file");
    }
    Ok(full)
}
