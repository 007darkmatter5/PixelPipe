//! Headless proof that a core runs the test ROM and libx264 can carry a frame.

use std::fs;
use std::io::Write;
use std::path::Path;
use std::time::Instant;

use anyhow::Context;

use crate::encoder::{decode_annex_b, H264Encoder};
use crate::input::JOYPAD_RIGHT;
use crate::libretro::{Core, VideoFrame};

pub struct SelfTestOptions<'a> {
    pub core: &'a Path,
    pub rom: &'a Path,
    pub system_dir: &'a Path,
    pub save_dir: &'a Path,
    pub dump_dir: &'a Path,
}

pub fn run(opts: &SelfTestOptions<'_>) -> anyhow::Result<()> {
    fs::create_dir_all(opts.system_dir)?;
    fs::create_dir_all(opts.save_dir)?;
    fs::create_dir_all(opts.dump_dir)?;
    let mut core = Core::load(opts.core, opts.system_dir, opts.save_dir)?;
    let av = core.load_rom(opts.rom)?;
    println!(
        "core {} {}  {}x{}  {:.3} fps  {:.0} Hz",
        core.name(),
        core.version(),
        av.width,
        av.height,
        av.fps,
        av.sample_rate
    );

    let mut audio_samples = 0usize;
    let mut idle: Option<VideoFrame> = None;
    for _ in 0..50 {
        let packet = core.run_frame();
        audio_samples += packet.audio.len();
        if let Some(frame) = packet.frame {
            idle = Some(frame);
        }
    }
    let idle = idle.context("core produced no video frames")?;
    let idle_yellow = yellow_centroid(&idle.bgra, idle.width, idle.height);
    write_ppm(&opts.dump_dir.join("selftest-idle.ppm"), &idle)?;

    core.buttons()
        .store(1 << JOYPAD_RIGHT, std::sync::atomic::Ordering::Relaxed);
    let mut moved = idle.clone();
    for _ in 0..40 {
        let packet = core.run_frame();
        audio_samples += packet.audio.len();
        if let Some(frame) = packet.frame {
            moved = frame;
        }
    }
    write_ppm(&opts.dump_dir.join("selftest-moved.ppm"), &moved)?;
    let moved_yellow = yellow_centroid(&moved.bgra, moved.width, moved.height);
    let nonblack = nonblack_ratio(&moved.bgra);

    println!(
        "frame {}x{}  nonblack {:.1}%  audio_samples {audio_samples}",
        moved.width,
        moved.height,
        nonblack * 100.0
    );
    println!("idle centroid {idle_yellow:?}");
    println!("moved centroid {moved_yellow:?}");
    if nonblack < 0.5 {
        anyhow::bail!("frame stayed mostly black; core did not render the ROM");
    }
    if audio_samples < 1000 {
        anyhow::bail!("core produced too little audio ({audio_samples} samples)");
    }
    let (x0, _, _) = idle_yellow.context(format!(
        "no yellow cursor before input; sample {}",
        sample_pixels(&idle.bgra, idle.width, idle.height)
    ))?;
    let (x1, _, _) = moved_yellow.context("no yellow cursor after holding Right")?;
    let dx = x1 - x0;
    println!("cursor dx {dx:.1} px");
    if dx < 30.0 {
        anyhow::bail!("cursor did not move right (dx {dx:.1}); input path failed");
    }

    let mut encoder = H264Encoder::open_best(moved.width, moved.height, av.fps.max(1.0))?;
    let mut units = Vec::new();
    let mut times = Vec::new();
    let bgra = if moved.width % 2 == 0 && moved.height % 2 == 0 {
        moved.bgra.clone()
    } else {
        moved.bgra.clone()
    };
    // The encoder opens at even dimensions. The test ROM is even.
    if encoder.width() != moved.width || encoder.height() != moved.height {
        anyhow::bail!(
            "encoder size {}x{} != frame",
            encoder.width(),
            encoder.height()
        );
    }
    for _ in 0..6 {
        let started = Instant::now();
        let unit = encoder.encode(&bgra)?;
        times.push(started.elapsed().as_micros() as u64);
        if unit.annex_b.is_empty() {
            anyhow::bail!("encoder delayed a frame; zerolatency path is not working");
        }
        units.push(unit.annex_b);
    }
    let decoded = decode_annex_b(&units)?;
    let avg = times[1..].iter().sum::<u64>() / (times.len() as u64 - 1);
    let max = *times.iter().max().unwrap_or(&0);
    println!(
        "encoder {}  encode_avg {avg} us  encode_max {max} us  decoded {}x{}",
        encoder.kind().label(),
        decoded.width,
        decoded.height
    );
    if decoded.width != moved.width || decoded.height != moved.height {
        anyhow::bail!("decoded dimensions do not match the core frame");
    }
    let state = core.serialize()?;
    core.unserialize(&state)?;
    println!("savestate {} bytes", state.len());
    println!("SELFTEST PASS");
    Ok(())
}

fn yellow_centroid(bgra: &[u8], width: u32, height: u32) -> Option<(f64, f64, u32)> {
    let mut sx = 0u64;
    let mut sy = 0u64;
    let mut n = 0u32;
    for y in 0..height as usize {
        for x in 0..width as usize {
            let i = (y * width as usize + x) * 4;
            let b = bgra[i] as u32;
            let g = bgra[i + 1] as u32;
            let r = bgra[i + 2] as u32;
            if r > 170 && g > 110 && b < 140 && r + 15 > g {
                sx += x as u64;
                sy += y as u64;
                n += 1;
            }
        }
    }
    if n < 16 {
        None
    } else {
        Some((sx as f64 / n as f64, sy as f64 / n as f64, n))
    }
}

fn nonblack_ratio(bgra: &[u8]) -> f64 {
    if bgra.is_empty() {
        return 0.0;
    }
    let mut n = 0usize;
    for px in bgra.chunks_exact(4) {
        if px[0] as u16 + px[1] as u16 + px[2] as u16 > 40 {
            n += 1;
        }
    }
    n as f64 / (bgra.len() / 4) as f64
}

fn sample_pixels(bgra: &[u8], width: u32, height: u32) -> String {
    let mut parts = Vec::new();
    for (x, y) in [(8, 8), (width / 2, height / 2), (120, 100)] {
        if x >= width || y >= height {
            continue;
        }
        let i = ((y * width + x) * 4) as usize;
        if i + 3 < bgra.len() {
            parts.push(format!(
                "({x},{y})=#{:02x}{:02x}{:02x}",
                bgra[i + 2],
                bgra[i + 1],
                bgra[i]
            ));
        }
    }
    parts.join(" ")
}

fn write_ppm(path: &Path, frame: &VideoFrame) -> anyhow::Result<()> {
    let mut file = fs::File::create(path)?;
    write!(file, "P6\n{} {}\n255\n", frame.width, frame.height)?;
    for px in frame.bgra.chunks_exact(4) {
        file.write_all(&[px[2], px[1], px[0]])?;
    }
    Ok(())
}
