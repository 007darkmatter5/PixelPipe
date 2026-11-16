//! Windows service registration. The installer calls `--install-service`.
//! LocalSystem often cannot read a passworded UNC share, so the default
//! installer task starts the same binary at login instead of as a service.

use std::ffi::OsString;
use std::path::PathBuf;
use std::sync::{mpsc, OnceLock};
use std::time::Duration;

use anyhow::Context;
use windows_service::service::{
    ServiceAccess, ServiceControl, ServiceControlAccept, ServiceErrorControl, ServiceExitCode,
    ServiceInfo, ServiceStartType, ServiceState, ServiceStatus, ServiceType,
};
use windows_service::service_control_handler::{self, ServiceControlHandlerResult};
use windows_service::service_dispatcher;
use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};

use crate::config::Config;
use crate::server;

pub const SERVICE_NAME: &str = "PixelPipe";

static CONFIG: OnceLock<Config> = OnceLock::new();

windows_service::define_windows_service!(ffi_service_main, service_main);

pub fn run(config: Config) -> anyhow::Result<()> {
    let _ = CONFIG.set(config);
    service_dispatcher::start(SERVICE_NAME, ffi_service_main)
        .context("service control dispatcher")?;
    Ok(())
}

fn service_main(_arguments: Vec<OsString>) {
    if let Err(err) = run_service() {
        tracing::error!(%err, "PixelPipe service stopped with an error");
    }
}

fn run_service() -> anyhow::Result<()> {
    let (tx, rx) = mpsc::channel();
    let status_handle = service_control_handler::register(SERVICE_NAME, move |event| match event {
        ServiceControl::Stop | ServiceControl::Shutdown => {
            let _ = tx.send(());
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .context("register service control handler")?;
    status_handle
        .set_service_status(status(
            ServiceState::Running,
            ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
        ))
        .context("report running")?;
    let config = CONFIG
        .get()
        .cloned()
        .context("service started without a config")?;
    let runtime = tokio::runtime::Runtime::new().context("tokio runtime")?;
    let result = runtime.block_on(server::serve_with_shutdown(config, async move {
        let _ = tokio::task::spawn_blocking(move || {
            let _ = rx.recv();
        })
        .await;
    }));
    let _ = status_handle
        .set_service_status(status(ServiceState::Stopped, ServiceControlAccept::empty()));
    result
}

fn status(state: ServiceState, controls: ServiceControlAccept) -> ServiceStatus {
    ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: state,
        controls_accepted: controls,
        exit_code: ServiceExitCode::NO_ERROR,
        checkpoint: 0,
        wait_hint: Duration::default(),
        process_id: None,
    }
}

pub fn install() -> anyhow::Result<()> {
    let _ = uninstall();
    let manager =
        ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CREATE_SERVICE)
            .context("open service control manager (run this from an elevated prompt)")?;
    let exe = std::env::current_exe().context("current executable")?;
    let info = ServiceInfo {
        name: OsString::from(SERVICE_NAME),
        display_name: OsString::from("PixelPipe"),
        service_type: ServiceType::OWN_PROCESS,
        start_type: ServiceStartType::AutoStart,
        error_control: ServiceErrorControl::Normal,
        executable_path: PathBuf::from(exe),
        launch_arguments: vec![OsString::from("--service")],
        dependencies: vec![],
        account_name: None,
        account_password: None,
    };
    let _service = manager
        .create_service(&info, ServiceAccess::QUERY_STATUS)
        .context("create PixelPipe service")?;
    println!("Registered the PixelPipe service. Start it with: sc start PixelPipe");
    println!(
        r"LocalSystem cannot see most network shares. For \\server\share, either start PixelPipe at login or set the service account:"
    );
    println!(r#"  sc config PixelPipe obj= ".\YourUser" password= "YourPassword""#);
    println!("  sc start PixelPipe");
    Ok(())
}

pub fn uninstall() -> anyhow::Result<()> {
    let manager = match ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
    {
        Ok(manager) => manager,
        Err(err) => {
            tracing::warn!(%err, "could not connect to the service control manager");
            return Ok(());
        }
    };
    let service = match manager.open_service(
        SERVICE_NAME,
        ServiceAccess::QUERY_STATUS | ServiceAccess::STOP | ServiceAccess::DELETE,
    ) {
        Ok(service) => service,
        Err(_) => return Ok(()),
    };
    let _ = service.stop();
    std::thread::sleep(Duration::from_millis(500));
    service.delete().context("delete PixelPipe service")?;
    println!("Removed the PixelPipe service.");
    Ok(())
}
