use std::sync::Arc;

use axum::extract::{Extension, Query, State};
use axum::http::{header, HeaderValue, Request, StatusCode};
use axum::middleware::{self, Next};
use axum::response::{Html, IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use serde::Deserialize;
use serde::Serialize;
use tower_http::cors::{Any, CorsLayer};

use crate::auth::AuthContext;
use crate::config::Config;
use crate::encoder::{probe, EncoderReport};
use crate::library::{Library, System};
use crate::session::{IceServer, SessionHub, SessionInfo, StartSession, StatsSnapshot};

#[derive(Clone)]
pub struct AppState {
    pub config: Arc<Config>,
    pub library: Arc<Library>,
    pub encoders: Arc<EncoderReport>,
    pub sessions: Arc<SessionHub>,
}

pub async fn serve(config: Config) -> anyhow::Result<()> {
    serve_with_shutdown(config, shutdown_signal()).await
}

pub async fn serve_with_shutdown<F>(config: Config, shutdown: F) -> anyhow::Result<()>
where
    F: std::future::Future<Output = ()> + Send + 'static,
{
    let mut ui = if config.spawn_ui {
        spawn_bundled_ui(&config)
    } else {
        None
    };
    let result = serve_inner(config, shutdown).await;
    if let Some(child) = ui.as_mut() {
        let _ = child.kill();
        let _ = child.wait();
    }
    result
}

async fn serve_inner<F>(config: Config, shutdown: F) -> anyhow::Result<()>
where
    F: std::future::Future<Output = ()> + Send + 'static,
{
    config.ensure_dirs()?;
    let report = probe();
    for item in &report.probes {
        if item.available {
            tracing::info!(encoder = item.ffmpeg_name, detail = %item.detail, "encoder available");
        } else {
            tracing::info!(encoder = item.ffmpeg_name, detail = %item.detail, "encoder skipped");
        }
    }
    tracing::info!(
        selected = report.selected.map(|kind| kind.ffmpeg_name()),
        detail = report.selected_detail.as_deref().unwrap_or(""),
        "encoder preference is NVENC, Quick Sync, VAAPI, AMF, then libx264"
    );
    let state = AppState {
        library: Arc::new(Library::new(config.roms.clone())),
        sessions: SessionHub::new(),
        encoders: Arc::new(report),
        config: Arc::new(config),
    };
    state.sessions.spawn_reaper();
    let bind = state.config.bind.clone();
    let app = Router::new()
        .route("/api/health", get(health))
        .route("/api/library", get(library))
        .route("/api/library/root", axum::routing::post(library_root))
        .route("/api/encoders", get(encoders))
        .route("/api/cores", get(cores))
        .route(
            "/api/session",
            get(session_get).post(session_start).delete(session_stop),
        )
        .route("/api/session/offer", post(session_offer))
        .route("/api/session/answer", post(session_answer))
        .route("/api/session/stats", get(session_stats))
        .route("/api/session/savestate", post(save_state))
        .route("/api/session/loadstate", post(load_state))
        .route("/api/session/pause", post(session_pause))
        .route("/api/session/reset", post(session_reset))
        .route("/api/session/stop", post(session_stop))
        .route("/player", get(player_page))
        .layer(middleware::from_fn_with_state(state.clone(), require_auth))
        .layer(
            CorsLayer::new()
                .allow_origin(Any)
                .allow_methods(Any)
                .allow_headers([header::CONTENT_TYPE, header::AUTHORIZATION]),
        )
        .with_state(state.clone());

    let listener = tokio::net::TcpListener::bind(&bind).await?;
    tracing::info!(%bind, "PixelPipe engine listening");
    axum::serve(listener, app)
        .with_graceful_shutdown(async move {
            shutdown.await;
            tracing::info!("shutting down");
            state.sessions.shutdown().await;
        })
        .await?;
    Ok(())
}

async fn shutdown_signal() {
    let ctrl_c = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    #[cfg(unix)]
    let term = async {
        if let Ok(mut sig) =
            tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
        {
            sig.recv().await;
        }
    };
    #[cfg(not(unix))]
    let term = std::future::pending::<()>();
    tokio::select! {
        _ = ctrl_c => {}
        _ = term => {}
    }
}

/// Start `PixelPipe.Web.exe` when it was installed beside the engine.
/// Docker and a source checkout do not have that file, so they keep starting
/// the UI themselves.
pub fn spawn_bundled_ui(config: &Config) -> Option<std::process::Child> {
    let exe = std::env::current_exe().ok()?;
    let dir = exe.parent()?;
    let ui = dir.join("PixelPipe.Web.exe");
    if !ui.is_file() {
        return None;
    }
    tracing::info!(path = %ui.display(), "starting bundled web UI");
    let mut cmd = std::process::Command::new(&ui);
    cmd.env("PixelPipe__RomsPath", &config.roms)
        .env("PixelPipe__EngineUrl", "http://127.0.0.1:9090")
        .env("ASPNETCORE_URLS", "http://0.0.0.0:8080")
        .env("ASPNETCORE_ENVIRONMENT", "Production")
        .current_dir(dir);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW. The web host is also a WinExe, so a service
        // start does not open a Kestrel console.
        cmd.creation_flags(0x0800_0000);
    }
    match cmd.spawn() {
        Ok(child) => Some(child),
        Err(err) => {
            tracing::warn!(%err, "could not start the web UI");
            None
        }
    }
}

async fn require_auth(
    State(state): State<AppState>,
    mut request: Request<axum::body::Body>,
    next: Next,
) -> Result<Response, StatusCode> {
    let path = request.uri().path().to_string();
    if path == "/api/health" {
        return Ok(next.run(request).await);
    }
    let header = request
        .headers()
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .unwrap_or("");
    let token = header.strip_prefix("Bearer ").unwrap_or("");
    let Some(auth) =
        crate::auth::verify_token(&crate::auth::load_secret(&state.config.data), token)
    else {
        return Err(StatusCode::UNAUTHORIZED);
    };
    if path == "/api/library/root" && auth.role != "Admin" {
        return Err(StatusCode::FORBIDDEN);
    }
    request.extensions_mut().insert(auth);
    Ok(next.run(request).await)
}

async fn health(State(state): State<AppState>) -> Json<serde_json::Value> {
    Json(serde_json::json!({
        "ok": true,
        "version": env!("CARGO_PKG_VERSION"),
        "encoder": state.encoders.selected.map(|k| k.ffmpeg_name()),
        "encoder_label": state.encoders.selected_label,
        "encoder_detail": state.encoders.selected_detail,
    }))
}

async fn library(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let games = state.library.games()?;
    Ok(Json(serde_json::json!({
        "roms_path": state.library.root(),
        "games": games,
    })))
}

async fn library_root(
    State(state): State<AppState>,
    Json(body): Json<LibraryRootBody>,
) -> Result<StatusCode, ApiError> {
    let path = body.path.trim();
    if path.is_empty() {
        return Err(ApiError::bad("rom path is empty"));
    }
    state.library.set_root(std::path::PathBuf::from(path));
    tracing::info!(path, "library root updated");
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize)]
struct LibraryRootBody {
    path: String,
}

async fn encoders(State(state): State<AppState>) -> Json<EncoderReport> {
    Json((*state.encoders).clone())
}

async fn cores(State(state): State<AppState>) -> Json<Vec<CoreDto>> {
    let list = [System::Nes, System::Snes, System::Genesis]
        .into_iter()
        .map(|system| {
            let path = state.config.core_path(system);
            let bytes = std::fs::metadata(&path).ok().map(|m| m.len());
            CoreDto {
                system,
                file: system.core_file().to_string(),
                path: path.display().to_string(),
                present: path.is_file(),
                bytes,
            }
        })
        .collect();
    Json(list)
}

#[derive(Serialize)]
struct CoreDto {
    system: System,
    file: String,
    path: String,
    present: bool,
    bytes: Option<u64>,
}

#[derive(Deserialize)]
struct StartBody {
    rom: String,
    #[serde(default)]
    slot: String,
    #[serde(default)]
    ice_servers: Vec<IceBody>,
}

#[derive(Deserialize)]
struct IceBody {
    urls: Vec<String>,
    #[serde(default)]
    username: String,
    #[serde(default)]
    credential: String,
}

#[derive(Deserialize)]
struct SlotBody {
    #[serde(default)]
    slot: String,
}

async fn session_start(
    State(state): State<AppState>,
    Extension(auth): Extension<AuthContext>,
    Json(body): Json<StartBody>,
) -> Result<Json<SessionInfo>, ApiError> {
    let slot = if body.slot.is_empty() {
        "1".to_string()
    } else {
        body.slot
    };
    let info = state
        .sessions
        .start(
            &state.config,
            &state.library,
            &StartSession {
                rom: body.rom,
                user_id: auth.user_id,
                slot,
                ice: body
                    .ice_servers
                    .into_iter()
                    .map(|server| IceServer {
                        urls: server.urls,
                        username: server.username,
                        credential: server.credential,
                    })
                    .collect(),
            },
        )
        .await?;
    Ok(Json(info))
}

async fn session_get(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let (info, stats) = state
        .sessions
        .stats()
        .await
        .ok_or_else(|| ApiError::not_found("no active session"))?;
    Ok(Json(serde_json::json!({ "session": info, "stats": stats })))
}

async fn session_stop(State(state): State<AppState>) -> StatusCode {
    state.sessions.shutdown().await;
    StatusCode::NO_CONTENT
}

#[derive(Deserialize)]
struct PauseBody {
    paused: bool,
}

async fn session_pause(
    State(state): State<AppState>,
    Json(body): Json<PauseBody>,
) -> Result<StatusCode, ApiError> {
    state.sessions.set_paused(body.paused).await?;
    Ok(StatusCode::NO_CONTENT)
}

async fn session_reset(State(state): State<AppState>) -> Result<StatusCode, ApiError> {
    state.sessions.reset().await?;
    Ok(StatusCode::NO_CONTENT)
}

async fn session_offer(State(state): State<AppState>) -> Result<Json<SdpDto>, ApiError> {
    let offer = state.sessions.offer().await?;
    Ok(Json(SdpDto {
        kind: offer.sdp_type.to_string(),
        sdp: offer.sdp,
    }))
}

#[derive(Deserialize)]
struct AnswerBody {
    sdp: String,
}

async fn session_answer(
    State(state): State<AppState>,
    Json(body): Json<AnswerBody>,
) -> Result<StatusCode, ApiError> {
    state.sessions.accept_answer(body.sdp).await?;
    Ok(StatusCode::NO_CONTENT)
}

async fn session_stats(State(state): State<AppState>) -> Result<Json<StatsSnapshot>, ApiError> {
    let (_info, stats) = state
        .sessions
        .stats()
        .await
        .ok_or_else(|| ApiError::not_found("no active session"))?;
    Ok(Json(stats))
}

async fn save_state(
    State(state): State<AppState>,
    Json(body): Json<SlotBody>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let slot = if body.slot.is_empty() {
        "1"
    } else {
        &body.slot
    };
    let bytes = state.sessions.save_state(slot).await?;
    Ok(Json(serde_json::json!({ "bytes": bytes, "slot": slot })))
}

async fn load_state(
    State(state): State<AppState>,
    Json(body): Json<SlotBody>,
) -> Result<StatusCode, ApiError> {
    let slot = if body.slot.is_empty() {
        "1".to_string()
    } else {
        body.slot
    };
    state.sessions.load_state(&slot).await?;
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize)]
struct PlayerQuery {
    rom: Option<String>,
}

async fn player_page(Query(q): Query<PlayerQuery>) -> Html<String> {
    let rom = q
        .rom
        .unwrap_or_else(|| "homebrew/pixelpipe-test.nes".into());
    let page = include_str!("../assets/player.html").replace("__ROM__", &escape_js(&rom));
    Html(page)
}

fn escape_js(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('\'', "\\'")
        .replace('\n', "")
}

#[derive(Serialize)]
struct SdpDto {
    #[serde(rename = "type")]
    kind: String,
    sdp: String,
}

struct ApiError {
    status: StatusCode,
    message: String,
}

impl ApiError {
    fn bad(message: impl Into<String>) -> Self {
        Self {
            status: StatusCode::BAD_REQUEST,
            message: message.into(),
        }
    }

    fn not_found(message: impl Into<String>) -> Self {
        Self {
            status: StatusCode::NOT_FOUND,
            message: message.into(),
        }
    }
}

impl From<anyhow::Error> for ApiError {
    fn from(err: anyhow::Error) -> Self {
        let message = err.to_string();
        let status = if message.contains("not found") || message.contains("not in the library") {
            StatusCode::NOT_FOUND
        } else {
            StatusCode::BAD_REQUEST
        };
        Self { status, message }
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> axum::response::Response {
        let mut res = (self.status, self.message).into_response();
        res.headers_mut().insert(
            header::CONTENT_TYPE,
            HeaderValue::from_static("text/plain; charset=utf-8"),
        );
        res
    }
}
