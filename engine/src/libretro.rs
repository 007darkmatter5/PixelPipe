//! Minimal libretro host. One core, one thread, software frames only.

use std::ffi::{c_char, c_void, CStr, CString};
use std::fs;
use std::path::Path;
use std::ptr;
use std::sync::atomic::{AtomicPtr, AtomicU32, Ordering};
use std::sync::Arc;

use anyhow::Context;
use libloading::Library;

const ENV_EXPERIMENTAL: u32 = 1 << 16;
const ENV_PRIVATE: u32 = 1 << 17;

const ENV_GET_CAN_DUPE: u32 = 3;
const ENV_SET_MESSAGE: u32 = 6;
const ENV_GET_SYSTEM_DIRECTORY: u32 = 9;
const ENV_SET_PIXEL_FORMAT: u32 = 10;
const ENV_SET_INPUT_DESCRIPTORS: u32 = 11;
const ENV_GET_VARIABLE: u32 = 15;
const ENV_SET_VARIABLES: u32 = 16;
const ENV_GET_VARIABLE_UPDATE: u32 = 17;
const ENV_SET_SUPPORT_NO_GAME: u32 = 18;
const ENV_GET_LIBRETRO_PATH: u32 = 19;
const ENV_GET_INPUT_DEVICE_CAPABILITIES: u32 = 24;
const ENV_GET_SAVE_DIRECTORY: u32 = 31;
const ENV_SET_SYSTEM_AV_INFO: u32 = 32;
const ENV_SET_SUBSYSTEM_INFO: u32 = 34;
const ENV_SET_CONTROLLER_INFO: u32 = 35;
const ENV_SET_GEOMETRY: u32 = 37;
const ENV_GET_USERNAME: u32 = 38;
const ENV_GET_LANGUAGE: u32 = 39;
const ENV_GET_TARGET_REFRESH_RATE: u32 = 50;
const ENV_GET_INPUT_BITMASKS: u32 = 51;
const ENV_GET_CORE_OPTIONS_VERSION: u32 = 52;
const ENV_SET_CORE_OPTIONS: u32 = 53;
const ENV_SET_CORE_OPTIONS_INTL: u32 = 54;
const ENV_SET_CORE_OPTIONS_DISPLAY: u32 = 55;

const PIXEL_0RGB1555: u32 = 0;
const PIXEL_XRGB8888: u32 = 1;
const PIXEL_RGB565: u32 = 2;

static CTX: AtomicPtr<Core> = AtomicPtr::new(ptr::null_mut());

#[derive(Clone, Copy, Debug)]
pub struct AvInfo {
    pub width: u32,
    pub height: u32,
    pub max_width: u32,
    pub max_height: u32,
    pub fps: f64,
    pub sample_rate: f64,
    pub aspect: f32,
}

impl Default for AvInfo {
    fn default() -> Self {
        Self {
            width: 256,
            height: 240,
            max_width: 256,
            max_height: 240,
            fps: 60.0,
            sample_rate: 48000.0,
            aspect: 4.0 / 3.0,
        }
    }
}

#[derive(Clone, Debug)]
pub struct VideoFrame {
    pub width: u32,
    pub height: u32,
    pub bgra: Vec<u8>,
    pub duplicate: bool,
}

pub struct FramePacket {
    pub frame: Option<VideoFrame>,
    pub audio: Vec<i16>,
}

#[repr(C)]
struct RetroSystemInfo {
    library_name: *const c_char,
    library_version: *const c_char,
    valid_extensions: *const c_char,
    need_fullpath: u8,
    block_extract: u8,
}

#[repr(C)]
struct RetroGameGeometry {
    base_width: u32,
    base_height: u32,
    max_width: u32,
    max_height: u32,
    aspect_ratio: f32,
}

#[repr(C)]
struct RetroSystemTiming {
    fps: f64,
    sample_rate: f64,
}

#[repr(C)]
struct RetroSystemAvInfo {
    geometry: RetroGameGeometry,
    timing: RetroSystemTiming,
}

#[repr(C)]
struct RetroGameInfo {
    path: *const c_char,
    data: *const c_void,
    size: usize,
    meta: *const c_char,
}

#[derive(Clone, Copy)]
struct CoreFns {
    api_version: unsafe extern "C" fn() -> u32,
    init: unsafe extern "C" fn(),
    deinit: unsafe extern "C" fn(),
    get_system_info: unsafe extern "C" fn(*mut RetroSystemInfo),
    get_system_av_info: unsafe extern "C" fn(*mut RetroSystemAvInfo),
    set_environment: unsafe extern "C" fn(unsafe extern "C" fn(u32, *mut c_void) -> bool),
    set_video_refresh: unsafe extern "C" fn(unsafe extern "C" fn(*const c_void, u32, u32, usize)),
    set_audio_sample: unsafe extern "C" fn(unsafe extern "C" fn(i16, i16)),
    set_audio_sample_batch: unsafe extern "C" fn(unsafe extern "C" fn(*const i16, usize) -> usize),
    set_input_poll: unsafe extern "C" fn(unsafe extern "C" fn()),
    set_input_state: unsafe extern "C" fn(unsafe extern "C" fn(u32, u32, u32, u32) -> i16),
    set_controller: unsafe extern "C" fn(u32, u32),
    load_game: unsafe extern "C" fn(*const RetroGameInfo) -> bool,
    unload_game: unsafe extern "C" fn(),
    run: unsafe extern "C" fn(),
    reset: unsafe extern "C" fn(),
    serialize_size: unsafe extern "C" fn() -> usize,
    serialize: unsafe extern "C" fn(*mut u8, usize) -> bool,
    unserialize: unsafe extern "C" fn(*const u8, usize) -> bool,
    get_memory_data: unsafe extern "C" fn(u32) -> *mut c_void,
    get_memory_size: unsafe extern "C" fn(u32) -> usize,
}

pub struct Core {
    _lib: Library,
    fns: CoreFns,
    inited: bool,
    game_loaded: bool,
    buttons: Arc<AtomicU32>,
    system_dir: CString,
    save_dir: CString,
    username: CString,
    core_path: CString,
    rom_path: Option<CString>,
    rom_bytes: Vec<u8>,
    name: String,
    version: String,
    need_fullpath: bool,
    pixel_format: u32,
    av: AvInfo,
    bgra: Vec<u8>,
    frame_w: u32,
    frame_h: u32,
    have_frame: bool,
    dup_frame: bool,
    audio: Vec<i16>,
}

impl Core {
    pub fn load(path: &Path, system_dir: &Path, save_dir: &Path) -> anyhow::Result<Self> {
        fs::create_dir_all(system_dir)?;
        fs::create_dir_all(save_dir)?;
        let lib =
            unsafe { Library::new(path) }.with_context(|| format!("dlopen {}", path.display()))?;
        let fns =
            unsafe { load_fns(&lib) }.with_context(|| format!("symbols in {}", path.display()))?;
        let mut info: RetroSystemInfo = unsafe { std::mem::zeroed() };
        unsafe { (fns.get_system_info)(&mut info) };
        let api = unsafe { (fns.api_version)() };
        let name = cstr(info.library_name);
        let version = cstr(info.library_version);
        let need_fullpath = info.need_fullpath != 0;
        tracing::info!(core = %name, version = %version, api, need_fullpath, "loaded libretro core");
        let mut core = Self {
            _lib: lib,
            fns,
            inited: false,
            game_loaded: false,
            buttons: Arc::new(AtomicU32::new(0)),
            system_dir: cstring_path(system_dir)?,
            save_dir: cstring_path(save_dir)?,
            username: CString::new("pixelpipe").unwrap(),
            core_path: cstring_path(path)?,
            rom_path: None,
            rom_bytes: Vec::new(),
            name,
            version,
            need_fullpath,
            pixel_format: PIXEL_0RGB1555,
            av: AvInfo::default(),
            bgra: Vec::new(),
            frame_w: 0,
            frame_h: 0,
            have_frame: false,
            dup_frame: false,
            audio: Vec::new(),
        };
        let ptr = &mut core as *mut Core;
        unsafe {
            CTX.store(ptr, Ordering::SeqCst);
            (fns.set_environment)(environment);
            (fns.set_video_refresh)(video_refresh);
            (fns.set_audio_sample)(audio_sample);
            (fns.set_audio_sample_batch)(audio_sample_batch);
            (fns.set_input_poll)(input_poll);
            (fns.set_input_state)(input_state);
            (fns.init)();
        }
        core.inited = true;
        Ok(core)
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn version(&self) -> &str {
        &self.version
    }

    pub fn buttons(&self) -> Arc<AtomicU32> {
        Arc::clone(&self.buttons)
    }

    pub fn av_info(&self) -> AvInfo {
        self.av
    }

    pub fn load_rom(&mut self, path: &Path) -> anyhow::Result<AvInfo> {
        self.rom_bytes = fs::read(path).with_context(|| format!("read {}", path.display()))?;
        self.rom_path = Some(cstring_path(path)?);
        let path_ptr = self
            .rom_path
            .as_ref()
            .map(|c| c.as_ptr())
            .unwrap_or(ptr::null());
        let data_ptr = self.rom_bytes.as_ptr();
        let size = self.rom_bytes.len();
        let fullpath = self.need_fullpath;
        let load = self.fns.load_game;
        let get_av = self.fns.get_system_av_info;
        let set_pad = self.fns.set_controller;
        let this = self as *mut Core;
        let info = if fullpath {
            RetroGameInfo {
                path: path_ptr,
                data: ptr::null(),
                size: 0,
                meta: ptr::null(),
            }
        } else {
            RetroGameInfo {
                path: path_ptr,
                data: data_ptr as *const c_void,
                size,
                meta: ptr::null(),
            }
        };
        let ok = unsafe {
            CTX.store(this, Ordering::SeqCst);
            load(&info)
        };
        if !ok {
            anyhow::bail!("core rejected {}", path.display());
        }
        self.game_loaded = true;
        let mut av: RetroSystemAvInfo = unsafe { std::mem::zeroed() };
        unsafe {
            get_av(&mut av);
            set_pad(0, 1);
        }
        self.av = av_from_retro(&av);
        if self.av.fps < 1.0 {
            self.av.fps = 60.0;
        }
        if self.av.sample_rate < 8000.0 {
            self.av.sample_rate = 48000.0;
        }
        tracing::info!(
            width = self.av.width,
            height = self.av.height,
            fps = self.av.fps,
            sample_rate = self.av.sample_rate,
            "ROM loaded"
        );
        Ok(self.av)
    }

    pub fn run_frame(&mut self) -> FramePacket {
        self.audio.clear();
        self.dup_frame = false;
        let run = self.fns.run;
        let this = self as *mut Core;
        unsafe {
            CTX.store(this, Ordering::SeqCst);
            run();
        }
        let audio = std::mem::take(&mut self.audio);
        let frame = if self.have_frame {
            Some(VideoFrame {
                width: self.frame_w,
                height: self.frame_h,
                bgra: self.bgra.clone(),
                duplicate: self.dup_frame,
            })
        } else {
            None
        };
        FramePacket { frame, audio }
    }

    pub fn reset(&mut self) {
        let reset = self.fns.reset;
        let this = self as *mut Core;
        unsafe {
            CTX.store(this, Ordering::SeqCst);
            reset();
        }
    }

    pub fn serialize(&mut self) -> anyhow::Result<Vec<u8>> {
        let size_fn = self.fns.serialize_size;
        let ser = self.fns.serialize;
        let this = self as *mut Core;
        unsafe {
            CTX.store(this, Ordering::SeqCst);
            let size = size_fn();
            if size == 0 {
                anyhow::bail!("this core does not support save states");
            }
            let mut buf = vec![0u8; size];
            if !ser(buf.as_mut_ptr(), size) {
                anyhow::bail!("retro_serialize failed");
            }
            Ok(buf)
        }
    }

    pub fn unserialize(&mut self, data: &[u8]) -> anyhow::Result<()> {
        let unser = self.fns.unserialize;
        let this = self as *mut Core;
        let ok = unsafe {
            CTX.store(this, Ordering::SeqCst);
            unser(data.as_ptr(), data.len())
        };
        if ok {
            Ok(())
        } else {
            anyhow::bail!("retro_unserialize failed")
        }
    }

    pub fn sram(&self) -> Option<Vec<u8>> {
        unsafe {
            let size = (self.fns.get_memory_size)(0);
            if size == 0 {
                return None;
            }
            let p = (self.fns.get_memory_data)(0);
            if p.is_null() {
                return None;
            }
            Some(std::slice::from_raw_parts(p as *const u8, size).to_vec())
        }
    }

    pub fn load_sram(&mut self, data: &[u8]) -> bool {
        unsafe {
            let size = (self.fns.get_memory_size)(0);
            if size == 0 || data.len() != size {
                return false;
            }
            let p = (self.fns.get_memory_data)(0) as *mut u8;
            if p.is_null() {
                return false;
            }
            ptr::copy_nonoverlapping(data.as_ptr(), p, size);
            true
        }
    }
}

impl Drop for Core {
    fn drop(&mut self) {
        let fns = self.fns;
        let this = self as *mut Core;
        unsafe {
            CTX.store(this, Ordering::SeqCst);
            if self.game_loaded {
                (fns.unload_game)();
                self.game_loaded = false;
            }
            if self.inited {
                (fns.deinit)();
                self.inited = false;
            }
            CTX.store(ptr::null_mut(), Ordering::SeqCst);
        }
    }
}

unsafe fn load_fns(lib: &Library) -> anyhow::Result<CoreFns> {
    Ok(CoreFns {
        api_version: *lib.get(b"retro_api_version\0")?,
        init: *lib.get(b"retro_init\0")?,
        deinit: *lib.get(b"retro_deinit\0")?,
        get_system_info: *lib.get(b"retro_get_system_info\0")?,
        get_system_av_info: *lib.get(b"retro_get_system_av_info\0")?,
        set_environment: *lib.get(b"retro_set_environment\0")?,
        set_video_refresh: *lib.get(b"retro_set_video_refresh\0")?,
        set_audio_sample: *lib.get(b"retro_set_audio_sample\0")?,
        set_audio_sample_batch: *lib.get(b"retro_set_audio_sample_batch\0")?,
        set_input_poll: *lib.get(b"retro_set_input_poll\0")?,
        set_input_state: *lib.get(b"retro_set_input_state\0")?,
        set_controller: *lib.get(b"retro_set_controller_port_device\0")?,
        load_game: *lib.get(b"retro_load_game\0")?,
        unload_game: *lib.get(b"retro_unload_game\0")?,
        run: *lib.get(b"retro_run\0")?,
        reset: *lib.get(b"retro_reset\0")?,
        serialize_size: *lib.get(b"retro_serialize_size\0")?,
        serialize: *lib.get(b"retro_serialize\0")?,
        unserialize: *lib.get(b"retro_unserialize\0")?,
        get_memory_data: *lib.get(b"retro_get_memory_data\0")?,
        get_memory_size: *lib.get(b"retro_get_memory_size\0")?,
    })
}

fn cstring_path(path: &Path) -> anyhow::Result<CString> {
    CString::new(path.to_string_lossy().as_bytes()).context("path contains a nul")
}

fn cstr(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    unsafe { CStr::from_ptr(ptr).to_string_lossy().into_owned() }
}

fn av_from_retro(av: &RetroSystemAvInfo) -> AvInfo {
    AvInfo {
        width: av.geometry.base_width.max(1),
        height: av.geometry.base_height.max(1),
        max_width: av.geometry.max_width.max(1),
        max_height: av.geometry.max_height.max(1),
        fps: av.timing.fps,
        sample_rate: av.timing.sample_rate,
        aspect: if av.geometry.aspect_ratio > 0.0 {
            av.geometry.aspect_ratio
        } else {
            4.0 / 3.0
        },
    }
}

fn with_ctx<R>(default: R, f: impl FnOnce(&mut Core) -> R) -> R {
    let ptr = CTX.load(Ordering::SeqCst);
    if ptr.is_null() {
        return default;
    }
    unsafe { f(&mut *ptr) }
}

unsafe extern "C" fn environment(cmd: u32, data: *mut c_void) -> bool {
    let cmd = cmd & !(ENV_EXPERIMENTAL | ENV_PRIVATE);
    with_ctx(false, |core| match cmd {
        ENV_GET_CAN_DUPE => write_u8(data, 1),
        ENV_GET_SYSTEM_DIRECTORY => write_ptr(data, core.system_dir.as_ptr()),
        ENV_GET_SAVE_DIRECTORY => write_ptr(data, core.save_dir.as_ptr()),
        ENV_GET_LIBRETRO_PATH => write_ptr(data, core.core_path.as_ptr()),
        ENV_GET_USERNAME => write_ptr(data, core.username.as_ptr()),
        ENV_SET_PIXEL_FORMAT => {
            if data.is_null() {
                return false;
            }
            let fmt = *(data as *const u32);
            if fmt > PIXEL_RGB565 {
                tracing::warn!(fmt, "rejecting pixel format");
                return false;
            }
            core.pixel_format = fmt;
            tracing::debug!(fmt, "pixel format");
            true
        }
        ENV_GET_VARIABLE_UPDATE => write_u8(data, 0),
        ENV_GET_VARIABLE => false,
        ENV_SET_VARIABLES
        | ENV_SET_SUPPORT_NO_GAME
        | ENV_SET_INPUT_DESCRIPTORS
        | ENV_SET_CONTROLLER_INFO
        | ENV_SET_SUBSYSTEM_INFO
        | ENV_SET_MESSAGE
        | ENV_SET_CORE_OPTIONS
        | ENV_SET_CORE_OPTIONS_INTL
        | ENV_SET_CORE_OPTIONS_DISPLAY => true,
        ENV_GET_CORE_OPTIONS_VERSION => false,
        ENV_GET_LANGUAGE => write_u32(data, 0),
        ENV_GET_INPUT_DEVICE_CAPABILITIES => write_u64(data, 1 << 1),
        ENV_GET_INPUT_BITMASKS => true,
        ENV_GET_TARGET_REFRESH_RATE => write_f32(data, 60.0),
        ENV_SET_GEOMETRY => {
            if data.is_null() {
                return false;
            }
            let g = &*(data as *const RetroGameGeometry);
            if g.base_width > 0 && g.base_height > 0 {
                core.av.width = g.base_width;
                core.av.height = g.base_height;
                core.av.max_width = g.max_width.max(g.base_width);
                core.av.max_height = g.max_height.max(g.base_height);
                if g.aspect_ratio > 0.0 {
                    core.av.aspect = g.aspect_ratio;
                }
            }
            true
        }
        ENV_SET_SYSTEM_AV_INFO => {
            if data.is_null() {
                return false;
            }
            core.av = av_from_retro(&*(data as *const RetroSystemAvInfo));
            true
        }
        other => {
            tracing::trace!(cmd = other, "unhandled environment command");
            false
        }
    })
}

unsafe extern "C" fn video_refresh(data: *const c_void, width: u32, height: u32, pitch: usize) {
    with_ctx((), |core| {
        if data.is_null() || width == 0 || height == 0 {
            core.dup_frame = true;
            return;
        }
        let bpp = if core.pixel_format == PIXEL_XRGB8888 {
            4
        } else {
            2
        };
        if pitch < width as usize * bpp {
            return;
        }
        let bytes = pitch.saturating_mul(height as usize);
        let src = std::slice::from_raw_parts(data as *const u8, bytes);
        core.bgra = to_bgra(core.pixel_format, src, width, height, pitch);
        core.frame_w = width;
        core.frame_h = height;
        core.have_frame = true;
        core.dup_frame = false;
    });
}

unsafe extern "C" fn audio_sample(left: i16, right: i16) {
    with_ctx((), |core| {
        core.audio.push(left);
        core.audio.push(right);
    });
}

unsafe extern "C" fn audio_sample_batch(data: *const i16, frames: usize) -> usize {
    if data.is_null() || frames == 0 {
        return 0;
    }
    with_ctx(0, |core| {
        let samples = std::slice::from_raw_parts(data, frames.saturating_mul(2));
        core.audio.extend_from_slice(samples);
        frames
    })
}

unsafe extern "C" fn input_poll() {}

unsafe extern "C" fn input_state(port: u32, device: u32, _index: u32, id: u32) -> i16 {
    if port != 0 || (device & 0xff) != 1 {
        return 0;
    }
    with_ctx(0, |core| {
        let mask = core.buttons.load(Ordering::Relaxed);
        if id == crate::input::JOYPAD_MASK_ID {
            return (mask & 0xffff) as i16;
        }
        if crate::input::pressed(mask, id) {
            1
        } else {
            0
        }
    })
}

fn write_u8(data: *mut c_void, v: u8) -> bool {
    if data.is_null() {
        return false;
    }
    unsafe { *(data as *mut u8) = v };
    true
}

fn write_u32(data: *mut c_void, v: u32) -> bool {
    if data.is_null() {
        return false;
    }
    unsafe { *(data as *mut u32) = v };
    true
}

fn write_u64(data: *mut c_void, v: u64) -> bool {
    if data.is_null() {
        return false;
    }
    unsafe { *(data as *mut u64) = v };
    true
}

fn write_f32(data: *mut c_void, v: f32) -> bool {
    if data.is_null() {
        return false;
    }
    unsafe { *(data as *mut f32) = v };
    true
}

fn write_ptr(data: *mut c_void, ptr: *const c_char) -> bool {
    if data.is_null() {
        return false;
    }
    unsafe { *(data as *mut *const c_char) = ptr };
    true
}

pub fn to_bgra(fmt: u32, src: &[u8], width: u32, height: u32, pitch: usize) -> Vec<u8> {
    let w = width as usize;
    let h = height as usize;
    let mut out = vec![0u8; w * h * 4];
    match fmt {
        PIXEL_XRGB8888 => {
            for y in 0..h {
                let s = y * pitch;
                let d = y * w * 4;
                if s + w * 4 > src.len() {
                    break;
                }
                out[d..d + w * 4].copy_from_slice(&src[s..s + w * 4]);
            }
        }
        PIXEL_RGB565 => {
            for y in 0..h {
                for x in 0..w {
                    let s = y * pitch + x * 2;
                    if s + 1 >= src.len() {
                        break;
                    }
                    let p = u16::from_le_bytes([src[s], src[s + 1]]);
                    let r = (((p >> 11) & 0x1f) * 255 / 31) as u8;
                    let g = (((p >> 5) & 0x3f) * 255 / 63) as u8;
                    let b = ((p & 0x1f) * 255 / 31) as u8;
                    let d = (y * w + x) * 4;
                    out[d] = b;
                    out[d + 1] = g;
                    out[d + 2] = r;
                    out[d + 3] = 255;
                }
            }
        }
        _ => {
            for y in 0..h {
                for x in 0..w {
                    let s = y * pitch + x * 2;
                    if s + 1 >= src.len() {
                        break;
                    }
                    let p = u16::from_le_bytes([src[s], src[s + 1]]);
                    let r = (((p >> 10) & 0x1f) * 255 / 31) as u8;
                    let g = (((p >> 5) & 0x1f) * 255 / 31) as u8;
                    let b = ((p & 0x1f) * 255 / 31) as u8;
                    let d = (y * w + x) * 4;
                    out[d] = b;
                    out[d + 1] = g;
                    out[d + 2] = r;
                    out[d + 3] = 255;
                }
            }
        }
    }
    out
}
