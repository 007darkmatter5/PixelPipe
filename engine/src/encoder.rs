//! H.264 encoder selection. NVENC is preferred, then QuickSync, VAAPI, AMF, then libx264.

use std::sync::Once;

use anyhow::Context;
use ffmpeg_next as ffmpeg;
use ffmpeg_next::software::scaling::{Context as Scaler, Flags as ScaleFlags};
use serde::Serialize;

static FFMPEG_INIT: Once = Once::new();
static SELECTED: std::sync::OnceLock<EncoderKind> = std::sync::OnceLock::new();

fn ensure_ffmpeg() {
    FFMPEG_INIT.call_once(|| {
        if let Err(err) = ffmpeg::init() {
            tracing::warn!(%err, "ffmpeg::init");
        }
    });
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum EncoderKind {
    Nvenc,
    Qsv,
    Vaapi,
    Amf,
    X264,
}

impl EncoderKind {
    pub fn ffmpeg_name(self) -> &'static str {
        match self {
            EncoderKind::Nvenc => "h264_nvenc",
            EncoderKind::Qsv => "h264_qsv",
            EncoderKind::Vaapi => "h264_vaapi",
            EncoderKind::Amf => "h264_amf",
            EncoderKind::X264 => "libx264",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            EncoderKind::Nvenc => "NVIDIA NVENC",
            EncoderKind::Qsv => "Intel Quick Sync",
            EncoderKind::Vaapi => "VAAPI",
            EncoderKind::Amf => "AMD AMF",
            EncoderKind::X264 => "libx264 (software)",
        }
    }
}

pub fn preference_order() -> &'static [EncoderKind] {
    &[
        EncoderKind::Nvenc,
        EncoderKind::Qsv,
        EncoderKind::Vaapi,
        EncoderKind::Amf,
        EncoderKind::X264,
    ]
}

#[derive(Clone, Debug, Serialize)]
pub struct ProbeResult {
    pub kind: EncoderKind,
    pub ffmpeg_name: &'static str,
    pub label: &'static str,
    pub available: bool,
    pub detail: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct EncoderReport {
    pub selected: Option<EncoderKind>,
    pub selected_label: Option<String>,
    pub probes: Vec<ProbeResult>,
}

#[derive(Clone, Debug)]
pub struct EncodedAccessUnit {
    pub annex_b: Vec<u8>,
    pub keyframe: bool,
}

pub struct H264Encoder {
    kind: EncoderKind,
    encoder: ffmpeg::encoder::Video,
    scaler: Scaler,
    src: ffmpeg::frame::Video,
    yuv: ffmpeg::frame::Video,
    width: u32,
    height: u32,
    pts: i64,
    force_idr: bool,
}

impl H264Encoder {
    pub fn open(kind: EncoderKind, width: u32, height: u32, fps: f64) -> anyhow::Result<Self> {
        ensure_ffmpeg();
        let width = width.max(2) & !1;
        let height = height.max(2) & !1;
        let fps_i = fps.round().clamp(1.0, 240.0) as i32;
        let name = kind.ffmpeg_name();
        let codec = ffmpeg::encoder::find_by_name(name)
            .with_context(|| format!("{name} is not included in this FFmpeg build"))?;
        let mut video = ffmpeg::codec::context::Context::new()
            .encoder()
            .video()
            .context("video encoder context")?;
        video.set_width(width);
        video.set_height(height);
        video.set_format(ffmpeg::format::Pixel::YUV420P);
        video.set_time_base(ffmpeg::Rational(1, fps_i));
        video.set_frame_rate(Some(ffmpeg::Rational(fps_i, 1)));
        video.set_gop(15);
        video.set_max_b_frames(0);
        video.set_bit_rate(4_000_000);
        if kind == EncoderKind::X264 {
            // libx264 copies several AVCodecContext fields over the preset and then
            // refuses to run if they still look like FFmpeg's historical defaults.
            unsafe {
                let ctx = video.as_mut_ptr();
                (*ctx).qmin = -1;
                (*ctx).qmax = -1;
                (*ctx).max_qdiff = -1;
                (*ctx).me_range = -1;
                (*ctx).me_subpel_quality = -1;
                (*ctx).i_quant_factor = -1.0;
                (*ctx).b_quant_factor = -1.0;
                (*ctx).qcompress = -1.0;
            }
        }
        let mut opts = ffmpeg::Dictionary::new();
        for (key, value) in private_options(kind) {
            opts.set(key, value);
        }
        let encoder = video
            .open_as_with(codec, opts)
            .with_context(|| format!("open {name}"))?;
        let scaler = Scaler::get(
            ffmpeg::format::Pixel::BGRA,
            width,
            height,
            ffmpeg::format::Pixel::YUV420P,
            width,
            height,
            ScaleFlags::POINT,
        )
        .context("swscale")?;
        Ok(Self {
            kind,
            encoder,
            scaler,
            src: ffmpeg::frame::Video::new(ffmpeg::format::Pixel::BGRA, width, height),
            yuv: ffmpeg::frame::Video::new(ffmpeg::format::Pixel::YUV420P, width, height),
            width,
            height,
            pts: 0,
            force_idr: true,
        })
    }

    pub fn open_best(width: u32, height: u32, fps: f64) -> anyhow::Result<Self> {
        if let Some(kind) = SELECTED.get().copied() {
            if let Ok(enc) = Self::open(kind, width, height, fps) {
                return Ok(enc);
            }
        }
        let report = probe();
        let kind = report
            .selected
            .context("no H.264 encoder could be opened")?;
        Self::open(kind, width, height, fps)
    }

    pub fn kind(&self) -> EncoderKind {
        self.kind
    }

    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    pub fn request_keyframe(&mut self) {
        self.force_idr = true;
    }

    pub fn encode(&mut self, bgra: &[u8]) -> anyhow::Result<EncodedAccessUnit> {
        let expected = self.width as usize * self.height as usize * 4;
        if bgra.len() < expected {
            anyhow::bail!("frame is {} bytes, encoder expects {expected}", bgra.len());
        }
        copy_bgra(&mut self.src, bgra, self.width, self.height);
        self.scaler
            .run(&self.src, &mut self.yuv)
            .context("scale to yuv420")?;
        self.yuv.set_pts(Some(self.pts));
        self.pts += 1;
        if self.force_idr {
            self.yuv.set_kind(ffmpeg::picture::Type::I);
            self.force_idr = false;
        } else {
            self.yuv.set_kind(ffmpeg::picture::Type::None);
        }
        self.encoder.send_frame(&self.yuv).context("send frame")?;
        let mut annex_b = Vec::new();
        let mut packet = ffmpeg::Packet::empty();
        loop {
            match self.encoder.receive_packet(&mut packet) {
                Ok(()) => {
                    if let Some(data) = packet.data() {
                        annex_b.extend_from_slice(data);
                    }
                }
                Err(err) if is_again(&err) || matches!(err, ffmpeg::Error::Eof) => break,
                Err(err) => return Err(err).context("receive packet"),
            }
        }
        let annex_b = normalize_annex_b(&annex_b);
        let keyframe = annex_b_is_keyframe(&annex_b);
        Ok(EncodedAccessUnit { annex_b, keyframe })
    }
}

pub fn probe() -> EncoderReport {
    let report = probe_at(256, 240, 60.0);
    if let Some(kind) = report.selected {
        let _ = SELECTED.set(kind);
    }
    report
}

fn probe_at(width: u32, height: u32, fps: f64) -> EncoderReport {
    ensure_ffmpeg();
    let mut probes = Vec::new();
    let mut selected = None;
    for kind in preference_order() {
        if *kind == EncoderKind::Vaapi && !std::path::Path::new("/dev/dri/renderD128").exists() {
            probes.push(ProbeResult {
                kind: *kind,
                ffmpeg_name: kind.ffmpeg_name(),
                label: kind.label(),
                available: false,
                detail: "no /dev/dri/renderD128".into(),
            });
            continue;
        }
        match H264Encoder::open(*kind, width, height, fps) {
            Ok(enc) => {
                let detail = format!("opened {}x{} @ {fps} fps", enc.width, enc.height);
                drop(enc);
                if selected.is_none() {
                    selected = Some(*kind);
                }
                probes.push(ProbeResult {
                    kind: *kind,
                    ffmpeg_name: kind.ffmpeg_name(),
                    label: kind.label(),
                    available: true,
                    detail,
                });
            }
            Err(err) => {
                probes.push(ProbeResult {
                    kind: *kind,
                    ffmpeg_name: kind.ffmpeg_name(),
                    label: kind.label(),
                    available: false,
                    detail: err.to_string(),
                });
            }
        }
    }
    let selected_label = selected.map(|k| k.label().to_string());
    EncoderReport {
        selected,
        selected_label,
        probes,
    }
}

fn private_options(kind: EncoderKind) -> Vec<(&'static str, &'static str)> {
    match kind {
        EncoderKind::Nvenc => vec![
            ("preset", "p1"),
            ("tune", "ull"),
            ("zerolatency", "1"),
            ("delay", "0"),
            ("rc", "cbr"),
            ("profile", "baseline"),
            ("forced-idr", "1"),
            ("g", "15"),
            ("bf", "0"),
        ],
        EncoderKind::Qsv => vec![
            ("preset", "veryfast"),
            ("async_depth", "1"),
            ("look_ahead", "0"),
            ("profile", "baseline"),
            ("g", "15"),
            ("bf", "0"),
        ],
        EncoderKind::Vaapi => vec![("profile", "constrained_baseline"), ("bf", "0")],
        EncoderKind::Amf => vec![
            ("usage", "ultralowlatency"),
            ("quality", "speed"),
            ("rc", "cbr"),
            ("profile", "baseline"),
            ("bf", "0"),
        ],
        EncoderKind::X264 => vec![
            ("preset", "ultrafast"),
            ("tune", "zerolatency"),
            ("profile", "baseline"),
            ("level", "3.1"),
            ("crf", "16"),
            (
                "x264-params",
                "repeat-headers=1:keyint=15:min-keyint=15:scenecut=0:bframes=0:rc-lookahead=0:sync-lookahead=0:aud=1",
            ),
        ],
    }
}

fn copy_bgra(frame: &mut ffmpeg::frame::Video, bgra: &[u8], width: u32, height: u32) {
    let stride = frame.stride(0);
    let row_bytes = width as usize * 4;
    let data = frame.data_mut(0);
    for y in 0..height as usize {
        let src = y * row_bytes;
        let dst = y * stride;
        data[dst..dst + row_bytes].copy_from_slice(&bgra[src..src + row_bytes]);
    }
}

fn is_again(err: &ffmpeg::Error) -> bool {
    matches!(err, ffmpeg::Error::Other { errno } if *errno == ffmpeg::error::EAGAIN)
}

/// Make sure the buffer is Annex-B. AVCC length prefixes are converted.
pub fn normalize_annex_b(data: &[u8]) -> Vec<u8> {
    if data.is_empty() {
        return Vec::new();
    }
    if has_start_code(data) {
        return data.to_vec();
    }
    if let Some(nals) = try_avcc(data) {
        return nals_to_annex_b(&nals);
    }
    let mut out = vec![0, 0, 0, 1];
    out.extend_from_slice(data);
    out
}

pub fn annex_b_is_keyframe(data: &[u8]) -> bool {
    nal_units(data)
        .into_iter()
        .any(|nal| nal.first().map(|b| b & 0x1f) == Some(5))
}

fn has_start_code(data: &[u8]) -> bool {
    data.windows(3).any(|w| w == [0, 0, 1]) || data.windows(4).any(|w| w == [0, 0, 0, 1])
}

fn try_avcc(data: &[u8]) -> Option<Vec<Vec<u8>>> {
    let mut nals = Vec::new();
    let mut i = 0;
    while i + 4 <= data.len() {
        let len = u32::from_be_bytes(data[i..i + 4].try_into().ok()?) as usize;
        i += 4;
        if len == 0 || i + len > data.len() {
            return None;
        }
        let nal = &data[i..i + len];
        if nal.first().map(|b| b & 0x80) != Some(0) {
            return None;
        }
        nals.push(nal.to_vec());
        i += len;
    }
    if i == data.len() && !nals.is_empty() {
        Some(nals)
    } else {
        None
    }
}

fn nals_to_annex_b(nals: &[Vec<u8>]) -> Vec<u8> {
    let mut out = Vec::new();
    for nal in nals {
        out.extend_from_slice(&[0, 0, 0, 1]);
        out.extend_from_slice(nal);
    }
    out
}

fn nal_units(data: &[u8]) -> Vec<&[u8]> {
    let mut starts = Vec::new();
    let mut i = 0;
    while i + 3 < data.len() {
        if i + 4 <= data.len() && data[i..i + 4] == [0, 0, 0, 1] {
            starts.push(i + 4);
            i += 4;
            continue;
        }
        if data[i..i + 3] == [0, 0, 1] {
            starts.push(i + 3);
            i += 3;
            continue;
        }
        i += 1;
    }
    let mut out = Vec::new();
    for (idx, &start) in starts.iter().enumerate() {
        let end = if idx + 1 < starts.len() {
            let next = starts[idx + 1];
            if next >= 4 && data[next - 4..next] == [0, 0, 0, 1] {
                next - 4
            } else {
                next - 3
            }
        } else {
            data.len()
        };
        if start < end {
            let mut nal = &data[start..end];
            while nal.last() == Some(&0) {
                nal = &nal[..nal.len() - 1];
            }
            if !nal.is_empty() {
                out.push(nal);
            }
        }
    }
    out
}

pub fn decode_annex_b(frames: &[Vec<u8>]) -> anyhow::Result<DecodedFrame> {
    ensure_ffmpeg();
    let codec = ffmpeg::decoder::find(ffmpeg::codec::Id::H264).context("h264 decoder")?;
    let mut ctx = ffmpeg::codec::context::Context::new().decoder();
    unsafe {
        (*ctx.as_mut_ptr()).thread_count = 1;
    }
    let mut decoder = ctx
        .open_as(codec)
        .context("open h264 decoder")?
        .video()
        .context("h264 video decoder")?;
    let mut decoded = ffmpeg::frame::Video::empty();
    let mut last: Option<DecodedFrame> = None;
    let take =
        |decoded: &ffmpeg::frame::Video, last: &mut Option<DecodedFrame>| -> anyhow::Result<()> {
            if decoded.width() == 0 {
                return Ok(());
            }
            let (r, g, b) = sample_rgb(decoded)?;
            *last = Some(DecodedFrame {
                width: decoded.width(),
                height: decoded.height(),
                r,
                g,
                b,
            });
            Ok(())
        };
    for frame in frames {
        if frame.is_empty() {
            continue;
        }
        let packet = ffmpeg::Packet::copy(frame);
        decoder.send_packet(&packet).context("decode send")?;
        loop {
            match decoder.receive_frame(&mut decoded) {
                Ok(()) => take(&decoded, &mut last)?,
                Err(err) if is_again(&err) || matches!(err, ffmpeg::Error::Eof) => break,
                Err(err) => return Err(err).context("decode receive"),
            }
        }
    }
    decoder.send_eof().context("decode flush")?;
    loop {
        match decoder.receive_frame(&mut decoded) {
            Ok(()) => take(&decoded, &mut last)?,
            Err(err) if is_again(&err) || matches!(err, ffmpeg::Error::Eof) => break,
            Err(err) => return Err(err).context("decode flush receive"),
        }
    }
    last.context("decoder produced no frames")
}

fn sample_rgb(frame: &ffmpeg::frame::Video) -> anyhow::Result<(u8, u8, u8)> {
    let width = frame.width();
    let height = frame.height();
    let mut scaler = Scaler::get(
        frame.format(),
        width,
        height,
        ffmpeg::format::Pixel::BGRA,
        width,
        height,
        ScaleFlags::POINT,
    )
    .context("scale decoded frame")?;
    let mut bgra = ffmpeg::frame::Video::empty();
    scaler.run(frame, &mut bgra).context("read decoded pixel")?;
    let x = (width / 2) as usize;
    let y = (height / 2) as usize;
    let stride = bgra.stride(0);
    let data = bgra.data(0);
    let i = y * stride + x * 4;
    Ok((data[i + 2], data[i + 1], data[i]))
}

#[derive(Debug)]
pub struct DecodedFrame {
    pub width: u32,
    pub height: u32,
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nvenc_is_preferred_over_software() {
        let order = preference_order();
        let nv = order.iter().position(|k| *k == EncoderKind::Nvenc).unwrap();
        let soft = order.iter().position(|k| *k == EncoderKind::X264).unwrap();
        assert!(nv < soft);
        assert_eq!(*order.last().unwrap(), EncoderKind::X264);
        assert_eq!(EncoderKind::Nvenc.ffmpeg_name(), "h264_nvenc");
        assert_eq!(EncoderKind::Qsv.ffmpeg_name(), "h264_qsv");
        assert_eq!(EncoderKind::Vaapi.ffmpeg_name(), "h264_vaapi");
        assert_eq!(EncoderKind::Amf.ffmpeg_name(), "h264_amf");
    }

    #[test]
    fn annex_b_and_avcc_round_trip_helpers() {
        let raw = vec![0x65, 0x88, 0x84, 0x00, 0x10];
        let annex = normalize_annex_b(&raw);
        assert!(annex.starts_with(&[0, 0, 0, 1]));
        assert!(annex_b_is_keyframe(&annex));
        let avcc = {
            let mut v = Vec::new();
            v.extend_from_slice(&(raw.len() as u32).to_be_bytes());
            v.extend_from_slice(&raw);
            v
        };
        let converted = normalize_annex_b(&avcc);
        assert_eq!(converted, annex);
    }

    #[test]
    fn x264_roundtrip_red_frame() {
        let mut enc = H264Encoder::open(EncoderKind::X264, 256, 240, 60.0).expect("open x264");
        assert_eq!(enc.kind(), EncoderKind::X264);
        let mut bgra = vec![0u8; 256 * 240 * 4];
        for px in bgra.chunks_exact_mut(4) {
            px[0] = 20;
            px[1] = 30;
            px[2] = 220;
            px[3] = 255;
        }
        let mut units = Vec::new();
        for _ in 0..4 {
            let au = enc.encode(&bgra).expect("encode");
            assert!(
                !au.annex_b.is_empty(),
                "zerolatency encoder delayed a frame"
            );
            units.push(au.annex_b);
        }
        assert!(
            annex_b_is_keyframe(&units[0]),
            "first frame should be an IDR"
        );
        let decoded = decode_annex_b(&units).expect("decode");
        assert_eq!(decoded.width, 256);
        assert_eq!(decoded.height, 240);
        assert!(
            decoded.r > decoded.g && decoded.r > decoded.b,
            "center pixel {decoded:?}"
        );
        assert!(decoded.r > 80, "red channel collapsed: {decoded:?}");
    }
}
