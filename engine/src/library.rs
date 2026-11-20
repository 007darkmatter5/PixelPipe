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

    /// Name shown when no file has been found yet.
    pub fn core_file(self) -> &'static str {
        let names = self.core_filenames();
        if cfg!(windows) {
            names[0]
        } else {
            names[1]
        }
    }

    /// Native name first (`dll` on Windows, `so` on Unix), then the other.
    pub fn core_filenames(self) -> [&'static str; 2] {
        match self {
            System::Nes => ["fceumm_libretro.dll", "fceumm_libretro.so"],
            System::Snes => ["snes9x_libretro.dll", "snes9x_libretro.so"],
            System::Genesis => [
                "genesis_plus_gx_libretro.dll",
                "genesis_plus_gx_libretro.so",
            ],
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

    /// Folder names used when the extension is `.bin` or otherwise ambiguous.
    pub fn from_folder_name(name: &str) -> Option<Self> {
        let compact: String = name
            .chars()
            .filter(|ch| ch.is_ascii_alphanumeric())
            .flat_map(|ch| ch.to_lowercase())
            .collect();
        match compact.as_str() {
            "nes" | "famicom" => Some(System::Nes),
            "snes" | "supernintendo" | "superfamicom" => Some(System::Snes),
            "genesis" | "megadrive" | "segagenesis" => Some(System::Genesis),
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
    bundled: Option<PathBuf>,
    cache: std::sync::Mutex<Option<(SystemTime, Vec<Game>)>>,
}

impl Library {
    pub fn new(root: PathBuf) -> Self {
        let bundled = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(|dir| dir.join("demo-roms")))
            .filter(|dir| dir.is_dir());
        Self {
            root,
            bundled,
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
        let mut games = if self.root.is_dir() {
            scan(&self.root)?
        } else {
            Vec::new()
        };
        if let Some(bundled) = &self.bundled {
            if bundled.is_dir() {
                let mut extra = scan(bundled)?;
                for game in &mut extra {
                    game.file = format!("bundled/{}", game.file);
                }
                games.extend(extra);
                games.sort_by(|a, b| {
                    a.system
                        .label()
                        .cmp(b.system.label())
                        .then(a.title.cmp(&b.title))
                });
            }
        }
        *guard = Some((SystemTime::now(), games.clone()));
        Ok(games)
    }

    /// Resolve a library id, including `bundled/...` and `archive.zip#entry`.
    pub fn materialize(&self, file: &str, cache: &Path) -> anyhow::Result<PathBuf> {
        let (outer, member) = file
            .split_once('#')
            .map(|(outer, member)| (outer, Some(member)))
            .unwrap_or((file, None));
        let path = if let Some(rest) = outer.strip_prefix("bundled/") {
            let bundled = self
                .bundled
                .as_ref()
                .context("bundled demo ROMs are not installed")?;
            crate::config::resolve_rom(bundled, rest)?
        } else {
            crate::config::resolve_rom(&self.root, outer)?
        };
        match member {
            Some(member) => extract_zip_member(&path, member, cache),
            None => Ok(path),
        }
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
        // `.md` is both Markdown and Mega Drive. A text file is documentation.
        if ext.eq_ignore_ascii_case("md") && looks_like_text(&path) {
            continue;
        }
        let Some((system, member)) = classify(root, &path) else {
            continue;
        };
        let (crc, rom_crc, bytes) = hash_rom(&path, member.as_deref(), system)
            .with_context(|| format!("hash {}", path.display()))?;
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
        let file = match &member {
            Some(entry) => format!("{rel}#{entry}"),
            None => rel,
        };
        games.push(Game {
            file,
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
        } else if is_rom_candidate(&path) {
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

fn is_rom_candidate(path: &Path) -> bool {
    let ext = path
        .extension()
        .and_then(|ext| ext.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    matches!(ext.as_str(), "zip" | "bin") || System::from_extension(&ext).is_some()
}

fn classify(root: &Path, path: &Path) -> Option<(System, Option<String>)> {
    let ext = path
        .extension()
        .and_then(|ext| ext.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    if ext == "zip" {
        return classify_zip(root, path);
    }
    let system = System::from_extension(&ext).or_else(|| system_from_parents(root, path));
    system.map(|system| (system, None))
}

fn classify_zip(root: &Path, path: &Path) -> Option<(System, Option<String>)> {
    let file = File::open(path).ok()?;
    let mut archive = zip::ZipArchive::new(file).ok()?;
    let mut best: Option<(u64, String, System)> = None;
    for index in 0..archive.len() {
        let entry = archive.by_index(index).ok()?;
        let name = entry.name().replace('\\', "/");
        if name.is_empty() || name.ends_with('/') {
            continue;
        }
        if name.split('/').any(|part| part == "..") || name.starts_with('/') {
            continue;
        }
        let entry_ext = Path::new(&name)
            .extension()
            .and_then(|ext| ext.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        let system = System::from_extension(&entry_ext).or_else(|| {
            system_from_name_parts(name.split('/').rev().skip(1))
                .or_else(|| system_from_parents(root, path))
        });
        let Some(system) = system else {
            continue;
        };
        let size = entry.size();
        let replace = best.as_ref().map(|best| size > best.0).unwrap_or(true);
        if replace {
            best = Some((size, name, system));
        }
    }
    best.map(|(_, name, system)| (system, Some(name)))
}

fn system_from_parents(root: &Path, path: &Path) -> Option<System> {
    let mut current = path.parent();
    while let Some(dir) = current {
        if let Some(name) = dir.file_name().and_then(|name| name.to_str()) {
            if let Some(system) = System::from_folder_name(name) {
                return Some(system);
            }
        }
        if dir == root {
            break;
        }
        current = dir.parent();
    }
    None
}

fn system_from_name_parts<'a>(parts: impl Iterator<Item = &'a str>) -> Option<System> {
    for part in parts {
        if let Some(system) = System::from_folder_name(part) {
            return Some(system);
        }
    }
    None
}

fn hash_rom(
    path: &Path,
    member: Option<&str>,
    system: System,
) -> anyhow::Result<(u32, Option<u32>, u64)> {
    if let Some(member) = member {
        let file = File::open(path)?;
        let mut archive = zip::ZipArchive::new(file)?;
        let mut entry = archive
            .by_name(member)
            .with_context(|| format!("zip entry {member}"))?;
        return hash_reader(&mut entry, system);
    }
    let mut file = File::open(path)?;
    hash_reader(&mut file, system)
}

fn hash_reader(reader: &mut impl Read, system: System) -> anyhow::Result<(u32, Option<u32>, u64)> {
    let mut full = Hasher::new();
    let mut body = Hasher::new();
    let mut header = [0u8; 16];
    let n = reader.read(&mut header)?;
    full.update(&header[..n]);
    let ines = system == System::Nes && n == 16 && &header[..4] == b"NES\x1a";
    let mut buf = [0u8; 64 * 1024];
    let mut total = n as u64;
    loop {
        let k = reader.read(&mut buf)?;
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

pub fn extract_zip_member(zip_path: &Path, member: &str, cache: &Path) -> anyhow::Result<PathBuf> {
    if member.contains('\0') || member.split(['/', '\\']).any(|part| part == "..") {
        anyhow::bail!("invalid zip entry");
    }
    fs::create_dir_all(cache)?;
    let file = File::open(zip_path)?;
    let mut archive = zip::ZipArchive::new(file)?;
    let mut entry = archive
        .by_name(&member.replace('\\', "/"))
        .with_context(|| format!("zip entry {member}"))?;
    let ext = Path::new(member)
        .extension()
        .and_then(|ext| ext.to_str())
        .unwrap_or("rom");
    let mut hasher = Hasher::new();
    hasher.update(member.as_bytes());
    let out = cache.join(format!("{:08x}.{ext}", hasher.finalize()));
    let mut dest = File::create(&out)?;
    std::io::copy(&mut entry, &mut dest)?;
    Ok(out)
}

#[cfg(test)]
mod tests {
    use std::io::Write;

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
        let snes = games
            .iter()
            .find(|g| g.file.ends_with("pixelpipe-snes-test.sfc"))
            .expect("snes");
        assert_eq!(snes.system, System::Snes);
        assert_eq!(snes.title, "PixelPipe SNES Test");
        let genesis = games
            .iter()
            .find(|g| g.file.ends_with("pixelpipe-genesis-test.gen"))
            .expect("genesis");
        assert_eq!(genesis.system, System::Genesis);
        assert_eq!(genesis.title, "PixelPipe Genesis Test");
        assert!(games.iter().all(|g| !g.file.ends_with("README.md")));
        assert!(game.rom_crc32.is_some());
        assert_ne!(game.crc32, game.rom_crc32.clone().unwrap());
    }

    #[test]
    fn nested_folders_bin_and_zip_identify_the_system() {
        let root = std::env::temp_dir().join(format!("pixelpipe-scan-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("consoles/SNES/usa")).unwrap();
        fs::create_dir_all(root.join("Mega Drive")).unwrap();
        fs::write(root.join("consoles/SNES/usa/demo.bin"), b"snes-bin").unwrap();
        fs::write(root.join("Mega Drive/sonic.bin"), b"md-bin").unwrap();
        let zip_path = root.join("packs/favorites.zip");
        fs::create_dir_all(zip_path.parent().unwrap()).unwrap();
        {
            let file = File::create(&zip_path).unwrap();
            let mut writer = zip::ZipWriter::new(file);
            let options = zip::write::SimpleFileOptions::default()
                .compression_method(zip::CompressionMethod::Stored);
            writer.start_file("readme.txt", options).unwrap();
            writer.write_all(b"notes").unwrap();
            writer.start_file("tiny.sfc", options).unwrap();
            writer.write_all(&[1u8, 2, 3, 4]).unwrap();
            writer.start_file("bigger.nes", options).unwrap();
            writer.write_all(&[9u8; 32]).unwrap();
            writer.finish().unwrap();
        }
        let games = scan(&root).unwrap();
        let snes = games
            .iter()
            .find(|game| game.file.ends_with("demo.bin"))
            .unwrap();
        assert_eq!(snes.system, System::Snes);
        let genesis = games
            .iter()
            .find(|game| game.file.ends_with("sonic.bin"))
            .unwrap();
        assert_eq!(genesis.system, System::Genesis);
        let zipped = games
            .iter()
            .find(|game| game.file.contains("favorites.zip"))
            .unwrap();
        assert_eq!(zipped.system, System::Nes);
        assert!(zipped.file.ends_with("#bigger.nes"));
        let _ = fs::remove_dir_all(&root);
    }
}
