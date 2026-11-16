use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use anyhow::Context;
use crc32fast::Hasher;
use serde::Deserialize;
use serde::Serialize;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum System {
    Nes,
    Snes,
    Genesis,
}

impl System {
    pub fn label(self) -> &'static str {
        match self {
            System::Nes => "NES",
            System::Snes => "SNES",
            System::Genesis => "Genesis",
        }
    }

    pub fn id(self) -> &'static str {
        match self {
            System::Nes => "nes",
            System::Snes => "snes",
            System::Genesis => "genesis",
        }
    }

    pub fn core_file(self) -> &'static str {
        match self {
            System::Nes => "fceumm_libretro.so",
            System::Snes => "snes9x_libretro.so",
            System::Genesis => "genesis_plus_gx_libretro.so",
        }
    }

    pub fn from_extension(ext: &str) -> Option<Self> {
        match ext.to_ascii_lowercase().as_str() {
            "nes" | "unf" | "unif" => Some(System::Nes),
            "sfc" | "smc" => Some(System::Snes),
            "md" | "gen" | "smd" => Some(System::Genesis),
            _ => None,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct Game {
    pub file: String,
    pub title: String,
    pub system: System,
    pub crc32: String,
    pub rom_crc32: Option<String>,
    pub year: Option<i32>,
    pub developer: Option<String>,
    pub description: Option<String>,
    pub bytes: u64,
}

#[derive(Debug, Deserialize)]
struct LibraryFile {
    #[serde(default)]
    games: Vec<LibraryMeta>,
}

#[derive(Debug, Deserialize)]
struct LibraryMeta {
    file: String,
    title: Option<String>,
    developer: Option<String>,
    year: Option<i32>,
    description: Option<String>,
}

pub struct Library {
    root: PathBuf,
    cache: std::sync::Mutex<Option<(SystemTime, Vec<Game>)>>,
}

impl Library {
    pub fn new(root: PathBuf) -> Self {
        Self {
            root,
            cache: std::sync::Mutex::new(None),
        }
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn invalidate(&self) {
        if let Ok(mut guard) = self.cache.lock() {
            *guard = None;
        }
    }

    pub fn games(&self) -> anyhow::Result<Vec<Game>> {
        let mut guard = self.cache.lock().expect("library cache");
        let fresh = match guard.as_ref() {
            Some((when, games)) => when
                .elapsed()
                .map(|d| d.as_secs() < 5)
                .unwrap_or(false)
                .then(|| games.clone()),
            None => None,
        };
        if let Some(games) = fresh {
            return Ok(games);
        }
        let games = scan(&self.root)?;
        *guard = Some((SystemTime::now(), games.clone()));
        Ok(games)
    }
}

pub fn scan(root: &Path) -> anyhow::Result<Vec<Game>> {
    if !root.is_dir() {
        anyhow::bail!("ROM library {} is not a directory", root.display());
    }
    let meta = load_meta(&root.join("library.json"));
    let mut files = Vec::new();
    walk(root, 0, &mut files);
    files.sort();
    let mut games = Vec::with_capacity(files.len());
    for path in files {
        let rel = path
            .strip_prefix(root)
            .unwrap_or(&path)
            .to_string_lossy()
            .replace('\\', "/");
        let ext = path.extension().and_then(|e| e.to_str()).unwrap_or("");
        let Some(system) = System::from_extension(ext) else {
            continue;
        };
        // `.md` is both Markdown and Mega Drive. A text file is documentation.
        if ext.eq_ignore_ascii_case("md") && looks_like_text(&path) {
            continue;
        }
        let (crc, rom_crc, bytes) =
            hash_rom(&path, system).with_context(|| format!("hash {}", path.display()))?;
        let stem = path.file_stem().and_then(|s| s.to_str()).unwrap_or("game");
        let mut title = prettify(stem);
        let mut year = None;
        let mut developer = None;
        let mut description = None;
        if let Some(entry) = meta.iter().find(|m| m.file.replace('\\', "/") == rel) {
            if let Some(t) = &entry.title {
                title = t.clone();
            }
            year = entry.year;
            developer = entry.developer.clone();
            description = entry.description.clone();
        }
        games.push(Game {
            file: rel,
            title,
            system,
            crc32: format!("{crc:08x}"),
            rom_crc32: rom_crc.map(|c| format!("{c:08x}")),
            year,
            developer,
            description,
            bytes,
        });
    }
    games.sort_by(|a, b| {
        a.system
            .label()
            .cmp(b.system.label())
            .then(a.title.cmp(&b.title))
    });
    Ok(games)
}

fn load_meta(path: &Path) -> Vec<LibraryMeta> {
    let Ok(text) = fs::read_to_string(path) else {
        return Vec::new();
    };
    serde_json::from_str::<LibraryFile>(&text)
        .map(|f| f.games)
        .unwrap_or_default()
}

fn walk(dir: &Path, depth: usize, out: &mut Vec<PathBuf>) {
    if depth > 12 {
        return;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            walk(&path, depth + 1, out);
        } else if path
            .extension()
            .and_then(|e| e.to_str())
            .and_then(System::from_extension)
            .is_some()
        {
            out.push(path);
        }
    }
}

fn looks_like_text(path: &Path) -> bool {
    let mut file = match File::open(path) {
        Ok(file) => file,
        Err(_) => return false,
    };
    let mut buf = [0u8; 256];
    let n = file.read(&mut buf).unwrap_or(0);
    n > 0
        && buf[..n]
            .iter()
            .all(|byte| byte.is_ascii() && (byte.is_ascii_graphic() || byte.is_ascii_whitespace()))
}

fn prettify(stem: &str) -> String {
    stem.replace('_', " ")
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

fn hash_rom(path: &Path, system: System) -> anyhow::Result<(u32, Option<u32>, u64)> {
    let mut file = File::open(path)?;
    let mut full = Hasher::new();
    let mut body = Hasher::new();
    let mut header = [0u8; 16];
    let n = file.read(&mut header)?;
    full.update(&header[..n]);
    let ines = system == System::Nes && n == 16 && &header[..4] == b"NES\x1a";
    let mut buf = [0u8; 64 * 1024];
    let mut total = n as u64;
    loop {
        let k = file.read(&mut buf)?;
        if k == 0 {
            break;
        }
        full.update(&buf[..k]);
        if ines {
            body.update(&buf[..k]);
        }
        total += k as u64;
    }
    let rom_crc = if ines { Some(body.finalize()) } else { None };
    Ok((full.finalize(), rom_crc, total))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_rom_is_valid_ines_and_scannable() {
        let rom = Path::new(env!("CARGO_MANIFEST_DIR")).join("../roms/homebrew/pixelpipe-test.nes");
        let bytes = fs::read(&rom).expect("test rom");
        assert_eq!(&bytes[..4], b"NES\x1a");
        assert_eq!(bytes[4], 1);
        assert_eq!(bytes[5], 1);
        assert_eq!(bytes.len(), 16 + 16384 + 8192);
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../roms");
        let games = scan(&root).expect("scan");
        let game = games
            .iter()
            .find(|g| g.file.ends_with("pixelpipe-test.nes"))
            .expect("listed");
        assert_eq!(game.system, System::Nes);
        assert_eq!(game.title, "PixelPipe Test");
        assert!(games.iter().all(|g| !g.file.ends_with("README.md")));
        assert!(game.rom_crc32.is_some());
        assert_ne!(game.crc32, game.rom_crc32.clone().unwrap());
    }
}
