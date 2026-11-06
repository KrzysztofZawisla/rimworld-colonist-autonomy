//! One-click downloader for llama.cpp and the Clef GGUF model.
//!
//! All network and disk work runs on a plain worker thread. The installer window shows [`status`],
//! and completion is applied to the persisted settings on the game thread.

use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant};

use rimworld_rs::prelude::*;
use sha2::{Digest, Sha256};

use crate::settings::{self, Settings, LLAMA_BACKEND, SERVER_EXE_NAME};

#[derive(Clone, Copy)]
#[allow(dead_code)]
enum ArchiveKind {
    Zip,
    TarGz,
}

struct Package {
    url: &'static str,
    sha256: &'static str,
    bytes: u64,
    file: &'static str,
    archive: ArchiveKind,
}

#[cfg(all(windows, target_arch = "x86_64"))]
const LLAMA_PACKAGE: Package = Package {
    url: "https://github.com/ggml-org/llama.cpp/releases/download/b12069/llama-b12069-bin-win-vulkan-x64.zip",
    sha256: "e1b1c4e5481fe88e3f49f97908b53bb7699ad8aa3f6332a58f944e7a8f068b33",
    bytes: 37_554_286,
    file: "llama-b12069-bin-win-vulkan-x64.zip",
    archive: ArchiveKind::Zip,
};

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const LLAMA_PACKAGE: Package = Package {
    url: "https://github.com/ggml-org/llama.cpp/releases/download/b12069/llama-b12069-bin-ubuntu-vulkan-x64.tar.gz",
    sha256: "8001b930cc5e7cb500193dbdbea762a7f808ad2996ae12440e7af5020c94a4da",
    bytes: 35_740_211,
    file: "llama-b12069-bin-ubuntu-vulkan-x64.tar.gz",
    archive: ArchiveKind::TarGz,
};

#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
const LLAMA_PACKAGE: Package = Package {
    url: "https://github.com/ggml-org/llama.cpp/releases/download/b12069/llama-b12069-bin-macos-arm64.tar.gz",
    sha256: "91023146fab641e15d429c84002bf2e614a7a127992eedb443872fb23ba69439",
    bytes: 16_548_295,
    file: "llama-b12069-bin-macos-arm64.tar.gz",
    archive: ArchiveKind::TarGz,
};

#[cfg(all(target_os = "macos", target_arch = "x86_64"))]
const LLAMA_PACKAGE: Package = Package {
    url: "https://github.com/ggml-org/llama.cpp/releases/download/b12069/llama-b12069-bin-macos-x64.tar.gz",
    sha256: "7f1e6a7ce371c582ad8a9d9abed7d0f96a2a8060d4679109897cb3b98c181620",
    bytes: 12_287_221,
    file: "llama-b12069-bin-macos-x64.tar.gz",
    archive: ArchiveKind::TarGz,
};

#[cfg(not(any(
    all(windows, target_arch = "x86_64"),
    all(target_os = "linux", target_arch = "x86_64"),
    all(target_os = "macos", target_arch = "aarch64"),
    all(target_os = "macos", target_arch = "x86_64"),
)))]
const LLAMA_PACKAGE: Package = Package {
    url: "",
    sha256: "",
    bytes: 0,
    file: "",
    archive: ArchiveKind::Zip,
};

const CLEF_URL: &str =
    "https://huggingface.co/bartowski/Cloudflare_clef-GGUF/resolve/main/Cloudflare_clef-Q4_K_M.gguf?download=true";
const CLEF_SHA256: &str = "f1e2cc7f36893b52f773cc01f0ed351abab17c510502423bc4d2f5782a93cebb";
const CLEF_BYTES: u64 = 17_652_432_960;
const CLEF_FILE: &str = "Cloudflare_clef-Q4_K_M.gguf";

const CLEF_FLASH_URL: &str = "https://huggingface.co/bartowski/Cloudflare_clef-flash-GGUF/resolve/main/Cloudflare_clef-flash-Q8_0.gguf?download=true";
const CLEF_FLASH_SHA256: &str = "55c222dfc9f5760d06015ee6d6452ea45113bb6f333178c345c489dbc134bcfe";
const CLEF_FLASH_BYTES: u64 = 9_675_741_344;
pub(crate) const CLEF_FLASH_FILE: &str = "Cloudflare_clef-flash-Q8_0.gguf";

const PROGRESS_INTERVAL: Duration = Duration::from_millis(250);
const PROGRESS_BAR_WIDTH: usize = 12;

#[derive(Clone, Copy, PartialEq, Eq)]
enum ModelChoice {
    Any,
    Full,
    Flash,
}

impl ModelChoice {
    fn label(self) -> &'static str {
        match self {
            ModelChoice::Any => "configured model",
            ModelChoice::Full => "Clef Q4",
            ModelChoice::Flash => "Clef Flash Q8",
        }
    }
}

#[derive(Default)]
struct Completed {
    exe: Option<PathBuf>,
    model: Option<PathBuf>,
}

#[derive(Default)]
struct InstallState {
    busy: bool,
    message: String,
    completed: Option<Completed>,
}

static STATE: OnceLock<Mutex<InstallState>> = OnceLock::new();
static CANCEL: AtomicBool = AtomicBool::new(false);

fn state() -> &'static Mutex<InstallState> {
    STATE.get_or_init(|| Mutex::new(InstallState::default()))
}

fn set_status(message: String) {
    if let Ok(mut state) = state().lock() {
        state.message = message;
    }
}

fn cancelled() -> bool {
    CANCEL.load(Ordering::Relaxed)
}

/// True when both configured dependency files exist. A healthy external server is handled by
/// the caller separately; this is only the on-disk check used by the first-run prompt.
pub fn dependencies_present(settings: &Settings) -> bool {
    Path::new(settings.server_exe.trim()).is_file()
        && Path::new(settings.server_model.trim()).is_file()
}

/// Human-readable status for the settings label. Cheap: no hashing and no network.
pub fn status(settings: &Settings) -> String {
    let state = match state().lock() {
        Ok(state) => state,
        Err(_) => return "installer state is unavailable".to_string(),
    };
    if state.busy {
        return state.message.clone();
    }

    let mut text = describe(settings);
    if !state.message.is_empty() {
        text.push_str(" | ");
        text.push_str(&state.message);
    }
    text
}

fn describe(settings: &Settings) -> String {
    let exe = Path::new(&settings.server_exe);
    let selected = Path::new(&settings.server_model);
    let managed = settings::install_root().join("models");
    let full = managed.join(CLEF_FILE);
    let flash = managed.join(CLEF_FLASH_FILE);
    let selected_name = selected
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or("none");
    format!(
        "llama.cpp ({LLAMA_BACKEND}): {} | selected: {} | full: {} | flash: {} | Apache-2.0, bartowski/Cloudflare_clef-GGUF",
        if exe.is_file() { "found" } else { "missing" },
        selected_name,
        if full.is_file() { "found" } else { "missing" },
        if flash.is_file() { "found" } else { "missing" }
    )
}

fn progress_bar(fraction: f64) -> String {
    let fraction = fraction.clamp(0.0, 1.0);
    let filled = (fraction * PROGRESS_BAR_WIDTH as f64).round() as usize;
    format!(
        "[{}{}]",
        "#".repeat(filled),
        "-".repeat(PROGRESS_BAR_WIDTH - filled)
    )
}

fn human_size(bytes: u64) -> String {
    let bytes = bytes as f64;
    if bytes >= 1_000_000_000.0 {
        format!("{:.2} GB", bytes / 1_000_000_000.0)
    } else if bytes >= 1_000_000.0 {
        format!("{:.0} MB", bytes / 1_000_000.0)
    } else {
        format!("{:.0} KB", bytes / 1_000.0)
    }
}

fn human_rate(bytes_per_second: f64) -> String {
    if bytes_per_second >= 1_000_000_000.0 {
        format!("{:.2} GB", bytes_per_second / 1_000_000_000.0)
    } else if bytes_per_second >= 1_000_000.0 {
        format!("{:.1} MB", bytes_per_second / 1_000_000.0)
    } else if bytes_per_second >= 1_000.0 {
        format!("{:.0} KB", bytes_per_second / 1_000.0)
    } else {
        format!("{:.0} B", bytes_per_second)
    }
}

fn human_eta(seconds: u64) -> String {
    if seconds >= 3600 {
        format!("{}h {:02}m", seconds / 3600, (seconds % 3600) / 60)
    } else if seconds >= 60 {
        format!("{}m {:02}s", seconds / 60, seconds % 60)
    } else {
        format!("{seconds}s")
    }
}

fn progress_line(label: &str, done: u64, total: u64, resumed: u64, started: Instant) -> String {
    let fraction = if total == 0 {
        0.0
    } else {
        done as f64 / total as f64
    };
    let elapsed = started.elapsed().as_secs_f64();
    let transferred = done.saturating_sub(resumed) as f64;
    let speed = if elapsed >= 0.5 {
        transferred / elapsed
    } else {
        0.0
    };
    let eta = if speed >= 1.0 {
        human_eta(((total.saturating_sub(done)) as f64 / speed) as u64)
    } else {
        "--".to_string()
    };
    format!(
        "{label}: {} {:.0}% | {} / {} | {}/s | ETA {eta}",
        progress_bar(fraction),
        fraction * 100.0,
        human_size(done),
        human_size(total),
        human_rate(speed),
    )
}

fn verify_line(label: &str, done: u64, total: u64) -> String {
    let fraction = if total == 0 {
        0.0
    } else {
        done as f64 / total as f64
    };
    format!(
        "{label}: verifying SHA256 {} {:.0}%",
        progress_bar(fraction),
        fraction * 100.0
    )
}

/// Applies paths produced by the worker. Called from the game thread while the settings window
/// polls the label, because `Context` is not `Send`.
pub fn take_completed() -> Option<(Option<PathBuf>, Option<PathBuf>)> {
    let mut state = state().lock().ok()?;
    state
        .completed
        .take()
        .map(|completed| (completed.exe, completed.model))
}

/// Handles `settings_action` clicks from the mod options window.
pub fn handle_action(ctx: &mut Context, settings: &Settings, id: &str) {
    if id == "deps_cancel" {
        let busy = state().lock().map(|state| state.busy).unwrap_or(false);
        if !busy {
            set_status("nothing is running".to_string());
            return;
        }
        CANCEL.store(true, Ordering::SeqCst);
        set_status("cancelling download... partial file will be kept".to_string());
        return;
    }

    let (install_llama, model) = match id {
        "deps_install" => (true, Some(ModelChoice::Any)),
        "deps_install_llama" => (true, None),
        "deps_download_clef" => (false, Some(ModelChoice::Full)),
        "deps_download_flash" => (false, Some(ModelChoice::Flash)),
        _ => return,
    };
    CANCEL.store(false, Ordering::SeqCst);

    let mut state = match state().lock() {
        Ok(state) => state,
        Err(_) => {
            ctx.error("[ColonistAutonomy] installer state is unavailable");
            return;
        }
    };
    if state.busy {
        state.message = "installer is already running".to_string();
        return;
    }
    state.busy = true;
    state.message = match (install_llama, model) {
        (true, Some(choice)) => {
            format!("starting llama.cpp + {} installation...", choice.label())
        }
        (true, None) => "starting llama.cpp installation...".to_string(),
        (false, Some(choice)) => format!("starting {} download...", choice.label()),
        (false, None) => return,
    };
    drop(state);

    ctx.log(format!(
        "[ColonistAutonomy] installer: requested {} (install_llama={install_llama}, model={})",
        id,
        model.map(ModelChoice::label).unwrap_or("none")
    ));

    let settings = settings.clone();
    if let Err(error) = thread::Builder::new()
        .name("clef-installer".to_string())
        .spawn(move || run_install(settings, install_llama, model))
    {
        if let Ok(mut state) = self::state().lock() {
            state.busy = false;
            state.message = format!("cannot start installer thread: {error}");
        }
    }
}

fn run_install(settings: Settings, install_llama: bool, model: Option<ModelChoice>) {
    let result = (|| -> Result<Completed, String> {
        let mut completed = Completed::default();
        if cancelled() {
            return Err("cancelled; partial files were kept for resume".to_string());
        }

        if install_llama {
            let configured = PathBuf::from(settings.server_exe.trim());
            let exe = if configured.is_file() {
                configured
            } else {
                install_llama_cpp()?
            };
            completed.exe = Some(exe);
        }

        if cancelled() {
            return Err("cancelled; partial files were kept for resume".to_string());
        }
        if let Some(choice) = model {
            completed.model = Some(install_model(&settings, choice)?);
        }

        Ok(completed)
    })();

    let mut state = match state().lock() {
        Ok(state) => state,
        Err(_) => return,
    };
    state.busy = false;
    match result {
        Ok(completed) => {
            let parts = [
                completed.exe.as_ref().map(|_| "llama.cpp ready"),
                completed.model.as_ref().map(|_| "Clef ready"),
            ]
            .into_iter()
            .flatten()
            .collect::<Vec<_>>();
            state.message = if parts.is_empty() {
                "nothing to install".to_string()
            } else {
                format!("{} — installed", parts.join(", "))
            };
            state.completed = Some(completed);
        }
        Err(error) => {
            state.message = format!("install failed: {error}");
        }
    }
}

fn install_llama_cpp() -> Result<PathBuf, String> {
    if LLAMA_PACKAGE.url.is_empty() {
        return Err(format!(
            "the dependency installer supports Windows x64, Linux x64 and macOS; this build targets {}-{}",
            std::env::consts::OS,
            std::env::consts::ARCH
        ));
    }

    let root = settings::install_root();
    let target = root.join("llama.cpp");
    let archive = root.join("downloads").join(LLAMA_PACKAGE.file);
    if let Some(parent) = archive.parent() {
        fs::create_dir_all(parent).map_err(|error| format!("cannot create downloads: {error}"))?;
    }
    download(
        LLAMA_PACKAGE.url,
        &archive,
        LLAMA_PACKAGE.sha256,
        LLAMA_PACKAGE.bytes,
        "llama.cpp",
    )?;
    if cancelled() {
        return Err("cancelled; the llama.cpp archive was kept".to_string());
    }

    set_status("llama.cpp: extracting...".to_string());
    fs::create_dir_all(&target).map_err(|error| format!("cannot create llama.cpp dir: {error}"))?;
    extract_archive(&archive, &target, LLAMA_PACKAGE.archive)?;
    let exe = find_file(&target, SERVER_EXE_NAME).ok_or_else(|| {
        format!("{SERVER_EXE_NAME} was not found after extracting the llama.cpp archive")
    })?;
    make_executable(&exe)?;
    Ok(exe)
}

#[cfg(unix)]
fn make_executable(path: &Path) -> Result<(), String> {
    use std::os::unix::fs::PermissionsExt;

    let mut permissions = fs::metadata(path)
        .map_err(|error| format!("cannot read {}: {error}", path.display()))?
        .permissions();
    let mode = permissions.mode() | 0o111;
    if permissions.mode() != mode {
        permissions.set_mode(mode);
        fs::set_permissions(path, permissions)
            .map_err(|error| format!("cannot make {} executable: {error}", path.display()))?;
    }
    Ok(())
}

#[cfg(not(unix))]
fn make_executable(_path: &Path) -> Result<(), String> {
    Ok(())
}

fn install_model(settings: &Settings, choice: ModelChoice) -> Result<PathBuf, String> {
    let configured = PathBuf::from(settings.server_model.trim());
    match choice {
        ModelChoice::Any => {
            if configured.is_file() {
                verify_known_model(&configured)?;
                Ok(configured)
            } else {
                install_clef_flash()
            }
        }
        ModelChoice::Full => {
            if configured.is_file() && !is_flash_path(&configured) {
                verify_known_model(&configured)?;
                Ok(configured)
            } else {
                install_clef_full()
            }
        }
        ModelChoice::Flash => {
            if configured.is_file() && is_flash_path(&configured) {
                verify_known_model(&configured)?;
                Ok(configured)
            } else {
                install_clef_flash()
            }
        }
    }
}

fn is_flash_path(path: &Path) -> bool {
    path.file_name()
        .and_then(|value| value.to_str())
        .map(|value| value.to_ascii_lowercase().contains("flash"))
        .unwrap_or(false)
}

fn install_clef_full() -> Result<PathBuf, String> {
    let root = settings::install_root();
    let model = root.join("models").join(CLEF_FILE);
    if let Some(parent) = model.parent() {
        fs::create_dir_all(parent).map_err(|error| format!("cannot create models dir: {error}"))?;
    }
    download(CLEF_URL, &model, CLEF_SHA256, CLEF_BYTES, "Clef Q4")?;
    Ok(model)
}

fn install_clef_flash() -> Result<PathBuf, String> {
    let root = settings::install_root();
    let model = root.join("models").join(CLEF_FLASH_FILE);
    if let Some(parent) = model.parent() {
        fs::create_dir_all(parent).map_err(|error| format!("cannot create models dir: {error}"))?;
    }
    download(
        CLEF_FLASH_URL,
        &model,
        CLEF_FLASH_SHA256,
        CLEF_FLASH_BYTES,
        "Clef Flash Q8",
    )?;
    Ok(model)
}

fn verify_known_model(path: &Path) -> Result<(), String> {
    let name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or("");
    if name.eq_ignore_ascii_case(CLEF_FILE) {
        download(CLEF_URL, path, CLEF_SHA256, CLEF_BYTES, "Clef Q4")
    } else if name.eq_ignore_ascii_case(CLEF_FLASH_FILE) {
        download(
            CLEF_FLASH_URL,
            path,
            CLEF_FLASH_SHA256,
            CLEF_FLASH_BYTES,
            "Clef Flash Q8",
        )
    } else {
        // A custom model path is explicitly user-selected and has no pinned upstream hash.
        Ok(())
    }
}

fn download(
    url: &str,
    target: &Path,
    expected_sha: &str,
    expected_bytes: u64,
    label: &str,
) -> Result<(), String> {
    if cancelled() {
        return Err(format!("{label}: cancelled; partial file was kept"));
    }
    if target.is_file() {
        let size = fs::metadata(target).map(|meta| meta.len()).unwrap_or(0);
        if size == expected_bytes {
            set_status(format!("{label}: verifying existing file..."));
            if verify_sha256(target, expected_sha, label).is_ok() {
                return Ok(());
            }
            if cancelled() {
                return Err(format!("{label}: cancelled while verifying"));
            }
        }
        fs::remove_file(target)
            .map_err(|error| format!("cannot remove partial {label}: {error}"))?;
    }

    let part = target.with_extension("part");
    let mut existing = fs::metadata(&part).map(|meta| meta.len()).unwrap_or(0);
    if existing > expected_bytes {
        fs::remove_file(&part)
            .map_err(|error| format!("cannot remove oversized {label} part: {error}"))?;
        existing = 0;
    }

    if existing == expected_bytes {
        set_status(format!("{label}: verifying existing download..."));
        verify_sha256(&part, expected_sha, label)?;
        fs::rename(&part, target).map_err(|error| format!("cannot finalize {label}: {error}"))?;
        return Ok(());
    }

    let agent = ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_secs(15))
        .timeout_read(Duration::from_secs(120))
        .timeout_write(Duration::from_secs(60))
        .build();
    let mut request = agent.get(url);
    if existing > 0 {
        request = request.set("Range", &format!("bytes={existing}-"));
    }
    let response = request
        .call()
        .map_err(|error| format!("{label}: download failed: {error}"))?;
    let append = existing > 0 && response.status() == 206;
    let mut file = if append {
        OpenOptions::new()
            .append(true)
            .open(&part)
            .map_err(|error| format!("cannot append {label}: {error}"))?
    } else {
        File::create(&part).map_err(|error| format!("cannot create {label} part: {error}"))?
    };

    let mut reader = response.into_reader();
    let mut buffer = vec![0u8; 1 << 20];
    let resumed = if append { existing } else { 0 };
    let started = Instant::now();
    let mut last_report = Instant::now();
    let mut written = resumed;
    loop {
        if cancelled() {
            let _ = file.flush();
            return Err(format!(
                "{label}: cancelled; partial file was kept for resume"
            ));
        }
        let count = reader
            .read(&mut buffer)
            .map_err(|error| format!("{label}: read failed: {error}"))?;
        if count == 0 {
            break;
        }
        file.write_all(&buffer[..count])
            .map_err(|error| format!("{label}: write failed: {error}"))?;
        written = written.saturating_add(count as u64);
        if last_report.elapsed() >= PROGRESS_INTERVAL {
            last_report = Instant::now();
            set_status(progress_line(
                label,
                written,
                expected_bytes,
                resumed,
                started,
            ));
        }
    }
    file.flush()
        .map_err(|error| format!("{label}: flush failed: {error}"))?;
    drop(file);

    if written != expected_bytes {
        return Err(format!(
            "{label}: incomplete download ({written}/{expected_bytes} bytes); retry to resume"
        ));
    }

    if cancelled() {
        return Err(format!(
            "{label}: cancelled; partial file was kept for resume"
        ));
    }
    set_status(format!("{label}: verifying SHA256..."));
    verify_sha256(&part, expected_sha, label)?;
    fs::rename(&part, target).map_err(|error| format!("cannot finalize {label}: {error}"))?;
    Ok(())
}

fn verify_sha256(path: &Path, expected: &str, label: &str) -> Result<(), String> {
    let mut file = File::open(path).map_err(|error| format!("cannot open {label}: {error}"))?;
    let total = file.metadata().map(|meta| meta.len()).unwrap_or(0);
    let mut hasher = Sha256::new();
    let mut buffer = vec![0u8; 1 << 20];
    let mut read: u64 = 0;
    let mut last_report = Instant::now();
    loop {
        if cancelled() {
            return Err(format!("{label}: cancelled while verifying"));
        }
        let count = file
            .read(&mut buffer)
            .map_err(|error| format!("cannot read {label}: {error}"))?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
        read = read.saturating_add(count as u64);
        if last_report.elapsed() >= PROGRESS_INTERVAL {
            last_report = Instant::now();
            set_status(verify_line(label, read, total));
        }
    }
    let digest = hasher.finalize();
    let actual = digest
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect::<String>();
    if actual.eq_ignore_ascii_case(expected) {
        Ok(())
    } else {
        Err(format!(
            "{label}: SHA256 mismatch (expected {expected}, got {actual})"
        ))
    }
}

fn extract_archive(archive: &Path, target: &Path, kind: ArchiveKind) -> Result<(), String> {
    match kind {
        ArchiveKind::Zip => extract_zip(archive, target),
        ArchiveKind::TarGz => extract_tar_gz(archive, target),
    }
}

fn extract_tar_gz(archive_path: &Path, target: &Path) -> Result<(), String> {
    let file = File::open(archive_path)
        .map_err(|error| format!("cannot open llama.cpp archive: {error}"))?;
    let decoder = flate2::read::GzDecoder::new(file);
    let mut archive = tar::Archive::new(decoder);
    archive
        .unpack(target)
        .map_err(|error| format!("cannot extract llama.cpp archive: {error}"))
}

fn extract_zip(zip_path: &Path, target: &Path) -> Result<(), String> {
    let file =
        File::open(zip_path).map_err(|error| format!("cannot open llama.cpp zip: {error}"))?;
    let mut archive = zip::ZipArchive::new(file)
        .map_err(|error| format!("cannot read llama.cpp zip: {error}"))?;
    for index in 0..archive.len() {
        if cancelled() {
            return Err("cancelled while extracting llama.cpp".to_string());
        }
        let mut entry = archive
            .by_index(index)
            .map_err(|error| format!("cannot read zip entry: {error}"))?;
        let Some(relative) = entry.enclosed_name() else {
            continue;
        };
        let unix_mode = entry.unix_mode();
        if let Some(mode) = unix_mode {
            if mode & 0o170000 == 0o120000 {
                continue;
            }
        }
        let output = target.join(&relative);
        if entry.is_dir() {
            fs::create_dir_all(&output)
                .map_err(|error| format!("cannot create zip dir: {error}"))?;
            continue;
        }
        if let Some(parent) = output.parent() {
            fs::create_dir_all(parent)
                .map_err(|error| format!("cannot create zip parent: {error}"))?;
        }
        let mut out = File::create(&output)
            .map_err(|error| format!("cannot create extracted file: {error}"))?;
        std::io::copy(&mut entry, &mut out)
            .map_err(|error| format!("cannot extract zip file: {error}"))?;
        #[cfg(unix)]
        if let Some(mode) = unix_mode {
            use std::os::unix::fs::PermissionsExt;
            let _ = fs::set_permissions(&output, fs::Permissions::from_mode(mode & 0o777));
        }
    }
    Ok(())
}

fn find_file(root: &Path, name: &str) -> Option<PathBuf> {
    let entries = fs::read_dir(root).ok()?;
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            if let Some(found) = find_file(&path, name) {
                return Some(found);
            }
        } else if path.file_name().and_then(|value| value.to_str()) == Some(name) {
            return Some(path);
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::{find_file, human_eta, human_size, is_flash_path, progress_bar};
    use crate::settings::SERVER_EXE_NAME;
    use std::fs;
    use std::path::Path;

    #[test]
    fn finds_a_file_in_a_nested_directory() {
        let root = std::env::temp_dir().join("smart-colonists-find-file-test");
        let nested = root.join("build").join("bin");
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&nested).expect("create test dir");
        let file = nested.join(SERVER_EXE_NAME);
        fs::write(&file, b"test").expect("write test file");
        assert_eq!(find_file(&root, SERVER_EXE_NAME), Some(file));
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn returns_none_for_a_missing_file() {
        let root = std::env::temp_dir();
        assert!(find_file(&root, "this-file-does-not-exist-42").is_none());
    }

    #[test]
    fn recognizes_the_flash_model_name() {
        assert!(is_flash_path(Path::new("Clef-Flash-Q8_0.gguf")));
        assert!(is_flash_path(Path::new("Cloudflare_clef-flash-Q8_0.gguf")));
        assert!(!is_flash_path(Path::new("Cloudflare_clef-Q4_K_M.gguf")));
    }

    #[test]
    fn draws_a_proportional_progress_bar() {
        assert_eq!(progress_bar(0.0), "[------------]");
        assert_eq!(progress_bar(0.5), "[######------]");
        assert_eq!(progress_bar(1.0), "[############]");
        assert_eq!(progress_bar(2.0), "[############]");
    }

    #[test]
    fn formats_sizes_and_eta() {
        assert_eq!(human_size(9_675_741_344), "9.68 GB");
        assert_eq!(human_size(37_554_286), "38 MB");
        assert_eq!(human_eta(42), "42s");
        assert_eq!(human_eta(125), "2m 05s");
        assert_eq!(human_eta(3_725), "1h 02m");
    }

    #[test]
    #[ignore = "downloads the pinned llama.cpp archive from GitHub"]
    fn downloads_extracts_and_runs_llama() {
        let root = std::env::temp_dir().join("smart-colonists-installer-download-test");
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).expect("create test root");

        let archive = root.join(super::LLAMA_PACKAGE.file);
        super::download(
            super::LLAMA_PACKAGE.url,
            &archive,
            super::LLAMA_PACKAGE.sha256,
            super::LLAMA_PACKAGE.bytes,
            "llama-test",
        )
        .expect("download llama.cpp");

        let out = root.join("out");
        super::extract_archive(&archive, &out, super::LLAMA_PACKAGE.archive)
            .expect("extract llama.cpp");
        let exe = super::find_file(&out, SERVER_EXE_NAME).expect(SERVER_EXE_NAME);
        assert!(exe.is_file());

        let output = std::process::Command::new(&exe)
            .arg("--version")
            .current_dir(exe.parent().expect("exe parent"))
            .output()
            .expect("run llama-server --version");
        assert!(
            output.status.success(),
            "llama-server --version failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    #[ignore = "downloads and installs llama.cpp into the managed install root"]
    fn installs_llama_into_managed_dir() {
        let exe = super::install_llama_cpp().expect("install llama.cpp");
        assert!(exe.is_file());
        let output = std::process::Command::new(&exe)
            .arg("--version")
            .current_dir(exe.parent().expect("exe parent"))
            .output()
            .expect("run installed llama-server");
        assert!(
            output.status.success(),
            "installed llama-server failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
    }

    #[test]
    #[ignore = "network: Hugging Face redirect + Range"]
    fn hf_range_follows_redirect() {
        use std::io::Read;
        use std::time::Duration;

        let agent = ureq::AgentBuilder::new()
            .timeout_connect(Duration::from_secs(15))
            .timeout_read(Duration::from_secs(30))
            .build();
        let response = agent
            .get(super::CLEF_FLASH_URL)
            .set("Range", "bytes=0-1023")
            .call()
            .expect("HF range request");
        assert_eq!(response.status(), 206);
        let mut bytes = Vec::new();
        response
            .into_reader()
            .take(4096)
            .read_to_end(&mut bytes)
            .expect("read range body");
        assert_eq!(bytes.len(), 1024);
    }
}
