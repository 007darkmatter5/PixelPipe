//! One libretro session: emulate, encode, and publish WebRTC tracks.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::Context;
use bytes::Bytes;
use serde::Serialize;
use tokio::sync::mpsc;
use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_H264, MIME_TYPE_OPUS};
use webrtc::api::setting_engine::SettingEngine;
use webrtc::api::APIBuilder;
use webrtc::data_channel::data_channel_init::RTCDataChannelInit;
use webrtc::data_channel::RTCDataChannel;
use webrtc::ice::udp_network::{EphemeralUDP, UDPNetwork};
use webrtc::ice_transport::ice_candidate_type::RTCIceCandidateType;
use webrtc::ice_transport::ice_gatherer_state::RTCIceGathererState;
use webrtc::ice_transport::ice_gathering_state::RTCIceGatheringState;
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::interceptor::registry::Registry;
use webrtc::media::Sample;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

use crate::audio::AudioPipe;
use crate::config::Config;
use crate::encoder::{EncoderKind, H264Encoder};
use crate::library::{Game, Library, System};
use crate::libretro::{Core, VideoFrame};
use crate::play_control::{emulation_step, DisconnectWatch, EmulationStep, PeerLink};

#[derive(Clone, Debug, Serialize)]
pub struct SessionInfo {
    pub rom: String,
    pub title: String,
    pub system: System,
    pub core: String,
    pub core_version: String,
    pub encoder: String,
    pub fps: f64,
    pub sample_rate: f64,
    pub crc32: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct StatsSnapshot {
    pub frames: u64,
    pub fps: f64,
    pub encode_us_last: u64,
    pub encode_us_avg: u64,
    pub encode_us_max: u64,
    pub encoded_frames: u64,
    pub dropped_video: u64,
    pub audio_packets: u64,
    pub width: u32,
    pub height: u32,
    pub connected: bool,
    pub paused: bool,
    pub uptime_ms: u64,
}

pub struct LiveStats {
    frames: AtomicU64,
    encode_us_total: AtomicU64,
    encode_us_last: AtomicU64,
    encode_us_max: AtomicU64,
    encoded_frames: AtomicU64,
    dropped_video: AtomicU64,
    audio_packets: AtomicU64,
    width: AtomicU32,
    height: AtomicU32,
    connected: AtomicBool,
    paused: AtomicBool,
    started_ms: AtomicU64,
}

impl LiveStats {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            frames: AtomicU64::new(0),
            encode_us_total: AtomicU64::new(0),
            encode_us_last: AtomicU64::new(0),
            encode_us_max: AtomicU64::new(0),
            encoded_frames: AtomicU64::new(0),
            dropped_video: AtomicU64::new(0),
            audio_packets: AtomicU64::new(0),
            width: AtomicU32::new(0),
            height: AtomicU32::new(0),
            connected: AtomicBool::new(false),
            paused: AtomicBool::new(false),
            started_ms: AtomicU64::new(now_ms()),
        })
    }

    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::Relaxed);
    }

    pub fn set_connected(&self, up: bool) {
        self.connected.store(up, Ordering::Relaxed);
    }

    pub fn snapshot(&self) -> StatsSnapshot {
        let frames = self.frames.load(Ordering::Relaxed);
        let encoded = self.encoded_frames.load(Ordering::Relaxed);
        let total = self.encode_us_total.load(Ordering::Relaxed);
        let elapsed = now_ms()
            .saturating_sub(self.started_ms.load(Ordering::Relaxed))
            .max(1);
        StatsSnapshot {
            frames,
            fps: frames as f64 * 1000.0 / elapsed as f64,
            encode_us_last: self.encode_us_last.load(Ordering::Relaxed),
            encode_us_avg: if encoded == 0 { 0 } else { total / encoded },
            encode_us_max: self.encode_us_max.load(Ordering::Relaxed),
            encoded_frames: encoded,
            dropped_video: self.dropped_video.load(Ordering::Relaxed),
            audio_packets: self.audio_packets.load(Ordering::Relaxed),
            width: self.width.load(Ordering::Relaxed),
            height: self.height.load(Ordering::Relaxed),
            connected: self.connected.load(Ordering::Relaxed),
            paused: self.paused.load(Ordering::Relaxed),
            uptime_ms: elapsed,
        }
    }
}

enum Outbound {
    Video {
        annex_b: Vec<u8>,
        keyframe: bool,
        duration: Duration,
    },
    Audio {
        packet: Vec<u8>,
    },
}

enum EmuCmd {
    Save(std::sync::mpsc::Sender<anyhow::Result<Vec<u8>>>),
    Load(Vec<u8>, std::sync::mpsc::Sender<anyhow::Result<()>>),
    Reset,
    Stop,
}

struct BootInfo {
    buttons: Arc<AtomicU32>,
    name: String,
    version: String,
    fps: f64,
    sample_rate: f64,
}

pub struct ActiveSession {
    pub info: SessionInfo,
    pub stats: Arc<LiveStats>,
    cmd_tx: std::sync::mpsc::Sender<EmuCmd>,
    stop: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
    force_idr: Arc<AtomicBool>,
    disconnect: Arc<std::sync::Mutex<DisconnectWatch>>,
    thread: Option<std::thread::JoinHandle<()>>,
    pc: Arc<RTCPeerConnection>,
    _dc: Arc<RTCDataChannel>,
    progress_dir: PathBuf,
    offer: tokio::sync::Mutex<Option<RTCSessionDescription>>,
}

impl ActiveSession {
    pub async fn offer(&self) -> anyhow::Result<RTCSessionDescription> {
        let mut slot = self.offer.lock().await;
        if let Some(existing) = slot.clone() {
            return Ok(existing);
        }
        let offer = self.pc.create_offer(None).await.context("create offer")?;
        self.pc
            .set_local_description(offer)
            .await
            .context("set local description")?;
        wait_for_gathering(&self.pc).await;
        let local = self
            .pc
            .local_description()
            .await
            .context("missing local description")?;
        *slot = Some(local.clone());
        Ok(local)
    }

    pub async fn accept_answer(&self, sdp: String) -> anyhow::Result<()> {
        let answer = RTCSessionDescription::answer(sdp).context("parse answer")?;
        self.pc
            .set_remote_description(answer)
            .await
            .context("set remote description")?;
        Ok(())
    }

    pub fn save_state(&self, slot: &str) -> anyhow::Result<u64> {
        let (tx, rx) = std::sync::mpsc::channel();
        self.cmd_tx
            .send(EmuCmd::Save(tx))
            .context("emulator is not running")?;
        let bytes = rx
            .recv_timeout(Duration::from_secs(2))
            .context("save timed out")??;
        let path = crate::auth::slot_file(&self.progress_dir, slot);
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(&path, &bytes)?;
        Ok(bytes.len() as u64)
    }

    pub fn load_state(&self, slot: &str) -> anyhow::Result<()> {
        let path = crate::auth::slot_file(&self.progress_dir, slot);
        let bytes = std::fs::read(&path).context("no save state yet")?;
        let (tx, rx) = std::sync::mpsc::channel();
        self.cmd_tx
            .send(EmuCmd::Load(bytes, tx))
            .context("emulator is not running")?;
        rx.recv_timeout(Duration::from_secs(2))
            .context("load timed out")??;
        Ok(())
    }

    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::SeqCst);
        self.stats.set_paused(paused);
        if !paused {
            self.force_idr.store(true, Ordering::SeqCst);
        }
        tracing::info!(paused, "core pause");
    }

    pub fn reset(&self) -> anyhow::Result<()> {
        self.cmd_tx
            .send(EmuCmd::Reset)
            .context("emulator is not running")?;
        self.force_idr.store(true, Ordering::SeqCst);
        tracing::info!("core reset");
        Ok(())
    }

    pub fn abandoned(&self, now: Instant) -> bool {
        self.disconnect
            .lock()
            .map(|watch| watch.should_end(now))
            .unwrap_or(false)
    }

    pub async fn shutdown(mut self) {
        self.stop.store(true, Ordering::SeqCst);
        let _ = self.cmd_tx.send(EmuCmd::Stop);
        if let Some(thread) = self.thread.take() {
            let _ = tokio::task::spawn_blocking(move || thread.join()).await;
        }
        let _ = self.pc.close().await;
    }
}

pub struct SessionHub {
    inner: tokio::sync::Mutex<Option<ActiveSession>>,
}

impl SessionHub {
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            inner: tokio::sync::Mutex::new(None),
        })
    }

    pub async fn start(
        &self,
        config: &Config,
        library: &Library,
        request: &StartSession,
    ) -> anyhow::Result<SessionInfo> {
        let mut guard = self.inner.lock().await;
        if let Some(existing) = guard.take() {
            existing.shutdown().await;
        }
        let session = spawn_session(config, library, request).await?;
        let info = session.info.clone();
        *guard = Some(session);
        Ok(info)
    }

    pub async fn offer(&self) -> anyhow::Result<RTCSessionDescription> {
        let guard = self.inner.lock().await;
        guard.as_ref().context("no active session")?.offer().await
    }

    pub async fn accept_answer(&self, sdp: String) -> anyhow::Result<()> {
        let guard = self.inner.lock().await;
        guard
            .as_ref()
            .context("no active session")?
            .accept_answer(sdp)
            .await
    }

    pub async fn save_state(&self, slot: &str) -> anyhow::Result<u64> {
        let guard = self.inner.lock().await;
        guard
            .as_ref()
            .context("no active session")?
            .save_state(slot)
    }

    pub async fn load_state(&self, slot: &str) -> anyhow::Result<()> {
        let guard = self.inner.lock().await;
        guard
            .as_ref()
            .context("no active session")?
            .load_state(slot)
    }

    pub async fn set_paused(&self, paused: bool) -> anyhow::Result<()> {
        let guard = self.inner.lock().await;
        guard
            .as_ref()
            .context("no active session")?
            .set_paused(paused);
        Ok(())
    }

    pub async fn reset(&self) -> anyhow::Result<()> {
        let guard = self.inner.lock().await;
        guard.as_ref().context("no active session")?.reset()
    }

    pub fn spawn_reaper(self: &Arc<Self>) {
        let hub = Arc::downgrade(self);
        tokio::spawn(async move {
            loop {
                tokio::time::sleep(Duration::from_millis(500)).await;
                let Some(hub) = hub.upgrade() else {
                    break;
                };
                hub.reap_if_abandoned().await;
            }
        });
    }

    async fn reap_if_abandoned(&self) {
        let abandoned = {
            let guard = self.inner.lock().await;
            guard
                .as_ref()
                .is_some_and(|session| session.abandoned(Instant::now()))
        };
        if abandoned {
            tracing::info!("browser disconnected; saving and ending the session");
            self.shutdown().await;
        }
    }

    pub async fn stats(&self) -> Option<(SessionInfo, StatsSnapshot)> {
        let guard = self.inner.lock().await;
        guard.as_ref().map(|s| (s.info.clone(), s.stats.snapshot()))
    }

    pub async fn shutdown(&self) {
        let mut guard = self.inner.lock().await;
        if let Some(session) = guard.take() {
            session.shutdown().await;
        }
    }
}

pub struct StartSession {
    pub rom: String,
    pub user_id: String,
    pub slot: String,
    pub ice: Vec<IceServer>,
}

#[derive(Clone, Debug)]
pub struct IceServer {
    pub urls: Vec<String>,
    pub username: String,
    pub credential: String,
}

async fn spawn_session(
    config: &Config,
    library: &Library,
    request: &StartSession,
) -> anyhow::Result<ActiveSession> {
    let rom_rel = request.rom.as_str();
    let games = library.games()?;
    let game = games
        .iter()
        .find(|g| g.file == rom_rel.trim_start_matches(['/', '\\']))
        .cloned()
        .context("ROM is not in the library")?;
    let rom_path = library.materialize(&game.file, &config.data.join("cache"))?;
    let core_path = config.core_path(game.system);
    if !core_path.is_file() {
        anyhow::bail!(
            "core {} is missing ({})",
            game.system.core_file(),
            core_path.display()
        );
    }
    let encoder_kind = crate::encoder::probe()
        .selected
        .context("no H.264 encoder available")?;

    let (pc, video, audio, dc) = build_peer(config, &request.ice).await?;
    let stats = LiveStats::new();
    let connected = Arc::new(AtomicBool::new(false));
    let force_idr = Arc::new(AtomicBool::new(true));
    let stop = Arc::new(AtomicBool::new(false));

    let connected_cb = connected.clone();
    let force_cb = force_idr.clone();
    let stats_cb = stats.clone();
    let disconnect = Arc::new(std::sync::Mutex::new(DisconnectWatch::new()));
    let watch_cb = disconnect.clone();
    pc.on_peer_connection_state_change(Box::new(move |state| {
        let up = state == RTCPeerConnectionState::Connected;
        connected_cb.store(up, Ordering::SeqCst);
        stats_cb.set_connected(up);
        if let Some(link) = peer_link(state) {
            if let Ok(mut watch) = watch_cb.lock() {
                watch.observe(link, Instant::now());
            }
        }
        if up {
            force_cb.store(true, Ordering::SeqCst);
        }
        tracing::info!(%state, "peer connection");
        Box::pin(async {})
    }));
    let watch_close = disconnect.clone();
    dc.on_close(Box::new(move || {
        if let Ok(mut watch) = watch_close.lock() {
            watch.observe(PeerLink::Closed, Instant::now());
        }
        tracing::info!("input channel closed");
        Box::pin(async {})
    }));

    let (out_tx, out_rx) = mpsc::channel::<Outbound>(3);
    spawn_writer(video, audio, out_rx, connected.clone());

    let (cmd_tx, cmd_rx) = std::sync::mpsc::channel::<EmuCmd>();
    let (ready_tx, ready_rx) = std::sync::mpsc::channel();
    let game_hash = crate::auth::sha256_file(&rom_path)
        .with_context(|| format!("hash {}", rom_path.display()))?;
    let progress =
        crate::auth::progress_dir(&config.data, &request.user_id, game.system.id(), &game_hash);
    std::fs::create_dir_all(&progress)?;
    let sram_path = progress.join("sram.bin");
    let auto_path = progress.join("auto.state");
    let initial_state = std::fs::read(crate::auth::slot_file(&progress, &request.slot)).ok();
    let paused = Arc::new(AtomicBool::new(false));
    let thread_stop = stop.clone();
    let thread_paused = paused.clone();
    let thread_stats = stats.clone();
    let thread_force = force_idr.clone();
    let system_dir = config.system_dir();
    let save_dir = config.save_dir();
    let thread = std::thread::Builder::new()
        .name("pixelpipe-emu".into())
        .spawn(move || {
            emu_thread(EmuArgs {
                core_path,
                rom_path,
                system_dir,
                save_dir,
                sram_path,
                auto_path,
                initial_state,
                encoder_kind,
                cmd_rx,
                out_tx,
                stop: thread_stop,
                paused: thread_paused,
                force_idr: thread_force,
                stats: thread_stats,
                ready_tx: Some(ready_tx),
            })
        })
        .context("spawn emulator thread")?;

    let boot = ready_rx
        .recv_timeout(Duration::from_secs(20))
        .context("emulator did not start")?
        .map_err(|err| anyhow::anyhow!(err))?;

    wire_input(dc.clone(), boot.buttons);

    let info = SessionInfo {
        rom: game.file.clone(),
        title: game.title.clone(),
        system: game.system,
        core: boot.name,
        core_version: boot.version,
        encoder: encoder_kind.label().to_string(),
        fps: boot.fps,
        sample_rate: boot.sample_rate,
        crc32: game.crc32.clone(),
    };
    tracing::info!(
        rom = %info.rom,
        user = %request.user_id,
        system = game_system_label(&game),
        core = %info.core,
        encoder = %info.encoder,
        "session ready"
    );

    Ok(ActiveSession {
        info,
        stats,
        cmd_tx,
        stop,
        paused,
        force_idr,
        disconnect,
        thread: Some(thread),
        pc,
        _dc: dc,
        progress_dir: progress,
        offer: tokio::sync::Mutex::new(None),
    })
}

fn game_system_label(game: &Game) -> &'static str {
    game.system.label()
}

struct EmuArgs {
    core_path: PathBuf,
    rom_path: PathBuf,
    system_dir: PathBuf,
    save_dir: PathBuf,
    sram_path: PathBuf,
    auto_path: PathBuf,
    initial_state: Option<Vec<u8>>,
    encoder_kind: EncoderKind,
    cmd_rx: std::sync::mpsc::Receiver<EmuCmd>,
    out_tx: mpsc::Sender<Outbound>,
    stop: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
    force_idr: Arc<AtomicBool>,
    stats: Arc<LiveStats>,
    ready_tx: Option<std::sync::mpsc::Sender<Result<BootInfo, String>>>,
}

fn emu_thread(mut args: EmuArgs) {
    let mut ready = args.ready_tx.take();
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if let Err(err) = emu_loop(&args, &mut ready) {
            tracing::error!(%err, "emulator stopped");
            if let Some(tx) = ready.take() {
                let _ = tx.send(Err(err.to_string()));
            }
        }
    }));
    if result.is_err() {
        if let Some(tx) = ready.take() {
            let _ = tx.send(Err("emulator thread panicked".into()));
        }
        tracing::error!("emulator thread panicked");
    }
}

fn emu_loop(
    args: &EmuArgs,
    ready: &mut Option<std::sync::mpsc::Sender<Result<BootInfo, String>>>,
) -> anyhow::Result<()> {
    let mut core = Core::load(&args.core_path, &args.system_dir, &args.save_dir)?;
    let av = core.load_rom(&args.rom_path)?;
    if let Ok(bytes) = std::fs::read(&args.sram_path) {
        if core.load_sram(&bytes) {
            tracing::info!(path = %args.sram_path.display(), "restored SRAM");
        }
    }
    if let Some(bytes) = &args.initial_state {
        if core.unserialize(bytes).is_ok() {
            tracing::info!("restored save state");
        }
    }
    let boot = BootInfo {
        buttons: core.buttons(),
        name: core.name().to_string(),
        version: core.version().to_string(),
        fps: av.fps,
        sample_rate: av.sample_rate,
    };
    if let Some(tx) = ready.take() {
        let _ = tx.send(Ok(boot));
    }

    let mut encoder: Option<H264Encoder> = None;
    let mut audio = AudioPipe::new(av.sample_rate)?;
    let frame_budget = Duration::from_secs_f64(1.0 / av.fps.max(1.0));
    let sample_duration = Duration::from_secs_f64(1.0 / av.fps.max(1.0));

    let mut frames = 0u64;
    while !args.stop.load(Ordering::Relaxed) {
        while let Ok(cmd) = args.cmd_rx.try_recv() {
            match cmd {
                EmuCmd::Stop => {
                    persist_sram(&core, &args.sram_path);
                    persist_state(&mut core, &args.auto_path);
                    return Ok(());
                }
                EmuCmd::Save(tx) => {
                    let _ = tx.send(core.serialize());
                }
                EmuCmd::Load(bytes, tx) => {
                    let _ = tx.send(core.unserialize(&bytes));
                }
                EmuCmd::Reset => core.reset(),
            }
        }

        if emulation_step(args.paused.load(Ordering::Relaxed)) == EmulationStep::Hold {
            // The core stays where it is. Video and audio are not produced.
            precise_wait(frame_budget);
            continue;
        }

        let started = Instant::now();
        let packet = core.run_frame();
        args.stats.frames.fetch_add(1, Ordering::Relaxed);

        if let Some(frame) = packet.frame.as_ref() {
            if let Err(err) = publish_video(args, &mut encoder, frame, sample_duration) {
                tracing::warn!(%err, "video encode");
            }
        }
        match audio.push(&packet.audio) {
            Ok(packets) => {
                for packet in packets {
                    args.stats.audio_packets.fetch_add(1, Ordering::Relaxed);
                    if args.out_tx.try_send(Outbound::Audio { packet }).is_err() {
                        // The writer is behind. Dropping audio keeps latency down.
                    }
                }
            }
            Err(err) => tracing::warn!(%err, "opus"),
        }

        frames += 1;
        if frames % 120 == 0 {
            persist_sram(&core, &args.sram_path);
        }
        let elapsed = started.elapsed();
        if let Some(rest) = frame_budget.checked_sub(elapsed) {
            precise_wait(rest);
        }
    }
    persist_sram(&core, &args.sram_path);
    persist_state(&mut core, &args.auto_path);
    Ok(())
}

fn publish_video(
    args: &EmuArgs,
    encoder: &mut Option<H264Encoder>,
    frame: &VideoFrame,
    duration: Duration,
) -> anyhow::Result<()> {
    if frame.width < 2 || frame.height < 2 {
        return Ok(());
    }
    let width = frame.width & !1;
    let height = frame.height & !1;
    let bgra = pad_even(&frame.bgra, frame.width, frame.height, width, height);
    let needs_open = match encoder.as_ref() {
        Some(enc) => enc.width() != width || enc.height() != height,
        None => true,
    };
    if needs_open {
        *encoder = Some(H264Encoder::open(
            args.encoder_kind,
            width,
            height,
            1.0 / duration.as_secs_f64(),
        )?);
        encoder.as_mut().unwrap().request_keyframe();
    }
    let enc = encoder.as_mut().unwrap();
    if args.force_idr.swap(false, Ordering::Relaxed) {
        enc.request_keyframe();
    }
    let encode_started = Instant::now();
    let unit = enc.encode(&bgra)?;
    let us = encode_started.elapsed().as_micros() as u64;
    args.stats.encode_us_last.store(us, Ordering::Relaxed);
    args.stats.encode_us_total.fetch_add(us, Ordering::Relaxed);
    args.stats.encoded_frames.fetch_add(1, Ordering::Relaxed);
    args.stats.encode_us_max.fetch_max(us, Ordering::Relaxed);
    args.stats.width.store(width, Ordering::Relaxed);
    args.stats.height.store(height, Ordering::Relaxed);
    if unit.annex_b.is_empty() {
        return Ok(());
    }
    if args
        .out_tx
        .try_send(Outbound::Video {
            annex_b: unit.annex_b,
            keyframe: unit.keyframe,
            duration,
        })
        .is_err()
    {
        args.stats.dropped_video.fetch_add(1, Ordering::Relaxed);
        if unit.keyframe {
            args.force_idr.store(true, Ordering::Relaxed);
        }
    }
    Ok(())
}

fn pad_even(src: &[u8], src_w: u32, src_h: u32, dst_w: u32, dst_h: u32) -> Vec<u8> {
    if src_w == dst_w && src_h == dst_h {
        return src.to_vec();
    }
    let mut out = vec![0u8; dst_w as usize * dst_h as usize * 4];
    let copy_w = src_w.min(dst_w) as usize;
    for y in 0..src_h.min(dst_h) as usize {
        let s = y * src_w as usize * 4;
        let d = y * dst_w as usize * 4;
        out[d..d + copy_w * 4].copy_from_slice(&src[s..s + copy_w * 4]);
    }
    out
}

fn persist_state(core: &mut Core, path: &Path) {
    let Ok(bytes) = core.serialize() else {
        return;
    };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Err(err) = std::fs::write(path, bytes) {
        tracing::warn!(%err, "writing auto save");
    }
}

fn persist_sram(core: &Core, path: &Path) {
    let Some(bytes) = core.sram() else {
        return;
    };
    if bytes.iter().all(|b| *b == 0) {
        return;
    }
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Err(err) = std::fs::write(path, bytes) {
        tracing::warn!(%err, "writing SRAM");
    }
}

fn precise_wait(total: Duration) {
    let start = Instant::now();
    if total > Duration::from_millis(2) {
        std::thread::sleep(total - Duration::from_millis(1));
    }
    while start.elapsed() < total {
        std::thread::yield_now();
    }
}

fn spawn_writer(
    video: Arc<TrackLocalStaticSample>,
    audio: Arc<TrackLocalStaticSample>,
    mut rx: mpsc::Receiver<Outbound>,
    connected: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        let mut need_key = true;
        while let Some(msg) = rx.recv().await {
            if !connected.load(Ordering::Relaxed) {
                need_key = true;
                continue;
            }
            match msg {
                Outbound::Video {
                    annex_b,
                    keyframe,
                    duration,
                } => {
                    if need_key && !keyframe {
                        continue;
                    }
                    let sample = Sample {
                        data: Bytes::from(annex_b),
                        duration,
                        ..Sample::default()
                    };
                    if video.write_sample(&sample).await.is_err() {
                        need_key = true;
                    } else {
                        need_key = false;
                    }
                }
                Outbound::Audio { packet } => {
                    let sample = Sample {
                        data: Bytes::from(packet),
                        duration: Duration::from_millis(10),
                        ..Sample::default()
                    };
                    let _ = audio.write_sample(&sample).await;
                }
            }
        }
    });
}

fn wire_input(dc: Arc<RTCDataChannel>, buttons: Arc<AtomicU32>) {
    let inbound = dc.clone();
    dc.on_message(Box::new(move |msg| {
        let buttons = buttons.clone();
        let dc = inbound.clone();
        let bytes = msg.data.to_vec();
        Box::pin(async move {
            let Ok(text) = std::str::from_utf8(&bytes) else {
                return;
            };
            let Ok(value) = serde_json::from_str::<serde_json::Value>(text) else {
                return;
            };
            if let Some(mask) = value.get("mask").and_then(|v| v.as_u64()) {
                buttons.store(mask as u32, Ordering::Relaxed);
            }
            if let Some(ping) = value.get("ping").and_then(|v| v.as_f64()) {
                let reply = serde_json::json!({ "pong": ping }).to_string();
                let _ = dc.send_text(reply).await;
            }
        })
    }));
}

async fn build_peer(
    config: &Config,
    ice: &[IceServer],
) -> anyhow::Result<(
    Arc<RTCPeerConnection>,
    Arc<TrackLocalStaticSample>,
    Arc<TrackLocalStaticSample>,
    Arc<RTCDataChannel>,
)> {
    let mut media = MediaEngine::default();
    media.register_default_codecs().context("media codecs")?;
    let mut registry = Registry::new();
    registry = register_default_interceptors(registry, &mut media).context("interceptors")?;
    let mut setting = SettingEngine::default();
    if config.webrtc_port_min > 0 && config.webrtc_port_max >= config.webrtc_port_min {
        let udp = EphemeralUDP::new(config.webrtc_port_min, config.webrtc_port_max)
            .map_err(|err| anyhow::anyhow!("webrtc port range: {err}"))?;
        setting.set_udp_network(UDPNetwork::Ephemeral(udp));
    }
    if let Some(ip) = &config.advertise_ip {
        setting.set_nat_1to1_ips(vec![ip.clone()], RTCIceCandidateType::Host);
    }
    let api = APIBuilder::new()
        .with_setting_engine(setting)
        .with_media_engine(media)
        .with_interceptor_registry(registry)
        .build();
    let mut ice_servers = Vec::new();
    if let Some(url) = config.stun.clone().filter(|url| !url.is_empty()) {
        ice_servers.push(RTCIceServer {
            urls: vec![url],
            ..Default::default()
        });
    }
    for server in ice {
        if server.urls.is_empty() {
            continue;
        }
        tracing::info!(urls = ?server.urls, "adding ICE server");
        ice_servers.push(RTCIceServer {
            urls: server.urls.clone(),
            username: server.username.clone(),
            credential: server.credential.clone(),
            ..Default::default()
        });
    }
    let pc = Arc::new(
        api.new_peer_connection(RTCConfiguration {
            ice_servers,
            ..Default::default()
        })
        .await
        .context("peer connection")?,
    );
    let video = Arc::new(TrackLocalStaticSample::new(
        RTCRtpCodecCapability {
            mime_type: MIME_TYPE_H264.to_owned(),
            ..Default::default()
        },
        "video".into(),
        "pixelpipe".into(),
    ));
    let audio = Arc::new(TrackLocalStaticSample::new(
        RTCRtpCodecCapability {
            mime_type: MIME_TYPE_OPUS.to_owned(),
            ..Default::default()
        },
        "audio".into(),
        "pixelpipe".into(),
    ));
    let video_sender = pc
        .add_track(Arc::clone(&video) as Arc<dyn TrackLocal + Send + Sync>)
        .await
        .context("add video")?;
    let audio_sender = pc
        .add_track(Arc::clone(&audio) as Arc<dyn TrackLocal + Send + Sync>)
        .await
        .context("add audio")?;
    tokio::spawn(async move {
        let mut buf = vec![0u8; 1500];
        while video_sender.read(&mut buf).await.is_ok() {}
    });
    tokio::spawn(async move {
        let mut buf = vec![0u8; 1500];
        while audio_sender.read(&mut buf).await.is_ok() {}
    });
    let dc = pc
        .create_data_channel(
            "input",
            Some(RTCDataChannelInit {
                ordered: Some(false),
                max_retransmits: Some(0),
                ..Default::default()
            }),
        )
        .await
        .context("data channel")?;
    Ok((pc, video, audio, dc))
}

async fn wait_for_gathering(pc: &Arc<RTCPeerConnection>) {
    if pc.ice_gathering_state() == RTCIceGatheringState::Complete {
        return;
    }
    let (tx, rx) = tokio::sync::oneshot::channel();
    let tx = Arc::new(std::sync::Mutex::new(Some(tx)));
    let tx_cb = tx.clone();
    pc.on_ice_gathering_state_change(Box::new(move |state| {
        if state == RTCIceGathererState::Complete {
            if let Ok(mut guard) = tx_cb.lock() {
                if let Some(tx) = guard.take() {
                    let _ = tx.send(());
                }
            }
        }
        Box::pin(async {})
    }));
    if pc.ice_gathering_state() == RTCIceGatheringState::Complete {
        return;
    }
    let _ = tokio::time::timeout(Duration::from_millis(1500), rx).await;
}

fn peer_link(state: RTCPeerConnectionState) -> Option<PeerLink> {
    match state {
        RTCPeerConnectionState::Connected => Some(PeerLink::Connected),
        RTCPeerConnectionState::Disconnected | RTCPeerConnectionState::Failed => {
            Some(PeerLink::Dropped)
        }
        RTCPeerConnectionState::Closed => Some(PeerLink::Closed),
        _ => None,
    }
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}
