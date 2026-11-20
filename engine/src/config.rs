use std::path::{Component, Path, PathBuf};

use serde::Deserialize;

use crate::library::System;

#[derive(Debug, Default, Deserialize)]
pub struct FileConfig {
    pub roms: Option<PathBuf>,
    #[serde(default)]
    pub roms_unc: Option<PathBuf>,
    pub cores: Option<PathBuf>,
    pub data: Option<PathBuf>,
    pub bind: Option<String>,
    pub advertise_ip: Option<String>,
    pub stun: Option<String>,
    #[serde(default)]
    pub webrtc_port_min: Option<u16>,
    #[serde(default)]
    pub webrtc_port_max: Option<u16>,
}

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
    /// Start PixelPipe.Web.exe when it sits beside the engine binary.
    pub spawn_ui: bool,
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
        for name in system.core_filenames() {
            let path = self.cores.join(name);
            if path.is_file() {
                return path;
            }
        }
        let names = system.core_filenames();
        let preferred = if cfg!(windows) { names[0] } else { names[1] };
        self.cores.join(preferred)
    }

    pub fn ensure_dirs(&self) -> std::io::Result<()> {
        std::fs::create_dir_all(self.system_dir())?;
        std::fs::create_dir_all(self.save_dir())?;
        Ok(())
    }
}

/// Directories that may hold `config.json`. The installer writes
/// `%ProgramData%\PixelPipe\config.json`, which can point at a UNC share.
pub fn config_file_candidates() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if let Ok(explicit) = std::env::var("PIXELPIPE_CONFIG") {
        if !explicit.is_empty() {
            paths.push(PathBuf::from(explicit));
        }
    }
    if let Ok(program_data) = std::env::var("PROGRAMDATA") {
        paths.push(
            PathBuf::from(program_data)
                .join("PixelPipe")
                .join("config.json"),
        );
    }
    paths.push(PathBuf::from("pixelpipe.json"));
    paths
}

pub fn load_file_config() -> FileConfig {
    for path in config_file_candidates() {
        let Ok(text) = std::fs::read_to_string(&path) else {
            continue;
        };
        match serde_json::from_str::<FileConfig>(&text) {
            Ok(config) => {
                tracing::info!(path = %path.display(), "loaded config file");
                return config;
            }
            Err(err) => tracing::warn!(path = %path.display(), %err, "ignoring config file"),
        }
    }
    FileConfig::default()
}

/// Resolve a library-relative ROM path and reject anything that escapes `root`.
///
/// `canonicalize` is used when the OS can resolve the path (it defeats symlink
/// escapes). UNC shares that cannot be canonicalized are still accepted when
/// the joined path is an existing file and has no `..` component.
pub fn resolve_rom(root: &Path, relative: &str) -> anyhow::Result<PathBuf> {
    if relative.contains('\0') {
        anyhow::bail!("invalid rom path");
    }
    let relative = relative.trim_start_matches(['/', '\\']);
    if relative.is_empty() {
        anyhow::bail!("empty rom path");
    }
    let rel = Path::new(relative);
    if rel
        .components()
        .any(|component| matches!(component, Component::ParentDir))
    {
        anyhow::bail!("invalid rom path");
    }
    let candidate = root.join(rel);
    if let (Ok(root_c), Ok(full_c)) = (loose_canonical(root), loose_canonical(&candidate)) {
        if !full_c.starts_with(&root_c) {
            anyhow::bail!("rom path escapes the library");
        }
        if !full_c.is_file() {
            anyhow::bail!("rom path is not a file");
        }
        return Ok(full_c);
    }
    if candidate.is_file() {
        return Ok(candidate);
    }
    anyhow::bail!("rom not found");
}

fn loose_canonical(path: &Path) -> std::io::Result<PathBuf> {
    Ok(strip_verbatim(&path.canonicalize()?))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_a_parent_directory_component() {
        let err = resolve_rom(Path::new("roms"), "homebrew/../../etc/passwd").unwrap_err();
        assert!(err.to_string().contains("invalid"), "{err}");
    }

    #[test]
    fn resolves_the_bundled_test_rom() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../roms");
        let path = resolve_rom(&root, "homebrew/pixelpipe-test.nes").expect("rom");
        assert!(path.is_file());
    }
}

fn strip_verbatim(path: &Path) -> PathBuf {
    let text = path.to_string_lossy();
    if let Some(rest) = text.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{rest}"));
    }
    if let Some(rest) = text.strip_prefix(r"\\?\") {
        return PathBuf::from(rest.to_string());
    }
    path.to_path_buf()
}
