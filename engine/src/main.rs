#![cfg_attr(windows, windows_subsystem = "windows")]

use std::io::IsTerminal;
use std::path::PathBuf;

use anyhow::Context;
use clap::Parser;
use pixelpipe_engine::config;
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
    /// ROM library. Overrides config.json. A UNC path such as \\unas-pro\roms is accepted.
    #[arg(long, env = "PIXELPIPE_ROMS")]
    roms: Option<PathBuf>,
    #[arg(long, env = "PIXELPIPE_CORES")]
    cores: Option<PathBuf>,
    #[arg(long, env = "PIXELPIPE_DATA")]
    data: Option<PathBuf>,
    #[arg(long, env = "PIXELPIPE_BIND")]
    bind: Option<String>,
    #[arg(long, env = "PIXELPIPE_ADVERTISE_IP")]
    advertise_ip: Option<String>,
    #[arg(long, env = "PIXELPIPE_STUN")]
    stun: Option<String>,
    /// Set both min and max to publish a fixed UDP range (Docker and the Windows firewall).
    /// 0 keeps OS-assigned ports.
    #[arg(long, env = "PIXELPIPE_WEBRTC_PORT_MIN")]
    webrtc_port_min: Option<u16>,
    #[arg(long, env = "PIXELPIPE_WEBRTC_PORT_MAX")]
    webrtc_port_max: Option<u16>,
    #[arg(long, env = "PIXELPIPE_CORE_NES")]
    core_nes: Option<PathBuf>,
    #[arg(long, env = "PIXELPIPE_CORE_SNES")]
    core_snes: Option<PathBuf>,
    #[arg(long, env = "PIXELPIPE_CORE_GENESIS")]
    core_genesis: Option<PathBuf>,
    /// Load a test core and ROM, check that input moves the cursor, and encode a frame.
    #[arg(long)]
    self_test: bool,
    #[arg(long)]
    rom: Option<PathBuf>,
    #[arg(long)]
    core: Option<PathBuf>,
    /// Do not start PixelPipe.Web.exe even when it sits beside this binary.
    #[arg(long)]
    no_ui: bool,
    /// Run under the Windows service control manager. Installed with --install-service.
    #[arg(long)]
    service: bool,
    /// Register the PixelPipe Windows service (elevated prompt). Does not start it.
    #[arg(long)]
    install_service: bool,
    /// Remove the PixelPipe Windows service.
    #[arg(long)]
    uninstall_service: bool,
}

fn main() -> anyhow::Result<()> {
    if let Err(err) = try_main() {
        eprintln!("{err:#}");
        if let Some(path) = log_file_path() {
            if let Ok(mut file) = std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&path)
            {
                use std::io::Write;
                let _ = writeln!(file, "{err:#}");
            }
        }
        std::process::exit(1);
    }
    Ok(())
}

fn try_main() -> anyhow::Result<()> {
    init_tracing();
    let cli = Cli::parse();
    if cli.install_service {
        return install_service();
    }
    if cli.uninstall_service {
        return uninstall_service();
    }
    let config = build_config(&cli);
    if cli.self_test {
        let rom = cli
            .rom
            .clone()
            .unwrap_or_else(|| config.roms.join("homebrew/pixelpipe-test.nes"));
        let core = cli
            .core
            .clone()
            .unwrap_or_else(|| default_nes_core(&config.cores));
        selftest::run(&SelfTestOptions {
            core: &core,
            rom: &rom,
            system_dir: &config.data.join("system"),
            save_dir: &config.data.join("saves"),
            dump_dir: &config.data,
        })
        .with_context(|| format!("self-test core {} rom {}", core.display(), rom.display()))?;
        return Ok(());
    }
    if cli.service {
        return run_service(config);
    }
    let runtime = tokio::runtime::Runtime::new().context("tokio runtime")?;
    runtime.block_on(server::serve(config))
}

fn build_config(cli: &Cli) -> Config {
    let file = config::load_file_config();
    Config {
        roms: first_path(cli.roms.clone(), file.roms, "roms"),
        cores: first_path(cli.cores.clone(), file.cores, "cores"),
        data: first_path(cli.data.clone(), file.data, "data"),
        bind: cli
            .bind
            .clone()
            .or(file.bind)
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "0.0.0.0:9090".to_string()),
        advertise_ip: nonempty(cli.advertise_ip.clone().or(file.advertise_ip)),
        stun: nonempty(cli.stun.clone().or(file.stun)),
        webrtc_port_min: cli.webrtc_port_min.or(file.webrtc_port_min).unwrap_or(0),
        webrtc_port_max: cli.webrtc_port_max.or(file.webrtc_port_max).unwrap_or(0),
        core_nes: cli.core_nes.clone(),
        core_snes: cli.core_snes.clone(),
        core_genesis: cli.core_genesis.clone(),
        spawn_ui: !cli.no_ui,
    }
}

fn first_path(cli: Option<PathBuf>, file: Option<PathBuf>, default: &str) -> PathBuf {
    cli.filter(|path| !path.as_os_str().is_empty())
        .or(file.filter(|path| !path.as_os_str().is_empty()))
        .unwrap_or_else(|| PathBuf::from(default))
}

fn nonempty(value: Option<String>) -> Option<String> {
    value.filter(|text| !text.is_empty())
}

fn default_nes_core(cores: &std::path::Path) -> PathBuf {
    for name in ["fceumm_libretro.so", "fceumm_libretro.dll"] {
        let path = cores.join(name);
        if path.is_file() {
            return path;
        }
    }
    cores.join(if cfg!(windows) {
        "fceumm_libretro.dll"
    } else {
        "fceumm_libretro.so"
    })
}

fn init_tracing() {
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| "pixelpipe_engine=info,webrtc=warn".into());
    if let Some(path) = log_file_path() {
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        if let Ok(file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
        {
            tracing_subscriber::fmt()
                .with_env_filter(filter)
                .with_ansi(false)
                .with_writer(std::sync::Mutex::new(file))
                .init();
            return;
        }
    }
    tracing_subscriber::fmt().with_env_filter(filter).init();
}

fn log_file_path() -> Option<PathBuf> {
    if std::io::stdout().is_terminal() {
        return None;
    }
    let base = std::env::var_os("PROGRAMDATA")?;
    Some(PathBuf::from(base).join("PixelPipe").join("pixelpipe.log"))
}

#[cfg(windows)]
fn install_service() -> anyhow::Result<()> {
    pixelpipe_engine::service::install()
}

#[cfg(windows)]
fn uninstall_service() -> anyhow::Result<()> {
    pixelpipe_engine::service::uninstall()
}

#[cfg(windows)]
fn run_service(config: Config) -> anyhow::Result<()> {
    pixelpipe_engine::service::run(config)
}

#[cfg(not(windows))]
fn install_service() -> anyhow::Result<()> {
    anyhow::bail!("--install-service is only available on Windows")
}

#[cfg(not(windows))]
fn uninstall_service() -> anyhow::Result<()> {
    anyhow::bail!("--uninstall-service is only available on Windows")
}

#[cfg(not(windows))]
fn run_service(_config: Config) -> anyhow::Result<()> {
    anyhow::bail!("--service is only available on Windows")
}
