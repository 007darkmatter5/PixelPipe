use std::path::PathBuf;

use anyhow::Context;
use clap::Parser;
use pixelpipe_engine::selftest::{self, SelfTestOptions};
use pixelpipe_engine::server;
use pixelpipe_engine::Config;

#[derive(Parser, Debug)]
#[command(
    name = "pixelpipe-engine",
    version,
    about = "PixelPipe libretro streaming engine"
)]
struct Cli {
    #[arg(long, env = "PIXELPIPE_ROMS", default_value = "roms")]
    roms: PathBuf,
    #[arg(long, env = "PIXELPIPE_CORES", default_value = "cores")]
    cores: PathBuf,
    #[arg(long, env = "PIXELPIPE_DATA", default_value = "data")]
    data: PathBuf,
    #[arg(long, env = "PIXELPIPE_BIND", default_value = "0.0.0.0:9090")]
    bind: String,
    #[arg(long, env = "PIXELPIPE_ADVERTISE_IP")]
    advertise_ip: Option<String>,
    #[arg(long, env = "PIXELPIPE_STUN")]
    stun: Option<String>,
    /// Set both min and max to publish a fixed UDP range (Docker). 0 keeps OS-assigned ports.
    #[arg(long, env = "PIXELPIPE_WEBRTC_PORT_MIN", default_value_t = 0)]
    webrtc_port_min: u16,
    #[arg(long, env = "PIXELPIPE_WEBRTC_PORT_MAX", default_value_t = 0)]
    webrtc_port_max: u16,
    #[arg(long, env = "PIXELPIPE_CORE_NES")]
    core_nes: Option<PathBuf>,
    #[arg(long, env = "PIXELPIPE_CORE_SNES")]
    core_snes: Option<PathBuf>,
    #[arg(long, env = "PIXELPIPE_CORE_GENESIS")]
    core_genesis: Option<PathBuf>,
    /// Load the test core and ROM, check that input moves the cursor, and encode a frame.
    #[arg(long)]
    self_test: bool,
    #[arg(long)]
    rom: Option<PathBuf>,
    #[arg(long)]
    core: Option<PathBuf>,
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "pixelpipe_engine=info,webrtc=warn".into()),
        )
        .init();
    let cli = Cli::parse();
    if cli.self_test {
        let rom = cli
            .rom
            .clone()
            .unwrap_or_else(|| cli.roms.join("homebrew/pixelpipe-test.nes"));
        let core = cli
            .core
            .clone()
            .unwrap_or_else(|| cli.cores.join("fceumm_libretro.so"));
        selftest::run(&SelfTestOptions {
            core: &core,
            rom: &rom,
            system_dir: &cli.data.join("system"),
            save_dir: &cli.data.join("saves"),
            dump_dir: &cli.data,
        })
        .with_context(|| format!("self-test core {} rom {}", core.display(), rom.display()))?;
        return Ok(());
    }
    let config = Config {
        roms: cli.roms,
        cores: cli.cores,
        data: cli.data,
        bind: cli.bind,
        advertise_ip: cli.advertise_ip.filter(|s| !s.is_empty()),
        stun: cli.stun.filter(|s| !s.is_empty()),
        webrtc_port_min: cli.webrtc_port_min,
        webrtc_port_max: cli.webrtc_port_max,
        core_nes: cli.core_nes,
        core_snes: cli.core_snes,
        core_genesis: cli.core_genesis,
    };
    server::serve(config).await
}
