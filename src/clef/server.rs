//! Starting the local llama-server process when a colony loads.
//!
//! The game never blocks on model loading: this only checks `/health`, spawns the process once,
//! and keeps its handle so repeated game loads do not start duplicates. The companion stops the
//! managed process when the game leaves the colony or quits; a force-quit leaves it running, so
//! the next session can reuse it.

use std::fs::OpenOptions;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::{Mutex, OnceLock};

use crate::clef::client;
use crate::settings::Settings;

#[derive(Debug)]
pub enum StartOutcome {
    Disabled,
    AlreadyRunning,
    AlreadyStarted,
    Started { pid: u32, log: Option<PathBuf> },
    Failed(String),
}

struct ManagedServer {
    child: Child,
    url: String,
}

static SERVER: OnceLock<Mutex<Option<ManagedServer>>> = OnceLock::new();

/// Checks the configured endpoint first. Spawns llama-server only when the endpoint is dead and
/// auto-start is enabled. Only local URLs are started; a remote endpoint is never spawned.
pub fn ensure_started(settings: &Settings) -> StartOutcome {
    if !settings.enabled || !settings.auto_start_server {
        return StartOutcome::Disabled;
    }

    let slot = SERVER.get_or_init(|| Mutex::new(None));
    let mut guard = match slot.lock() {
        Ok(guard) => guard,
        Err(error) => return StartOutcome::Failed(format!("server lock poisoned: {error}")),
    };

    if let Some(server) = guard.as_mut() {
        match server.child.try_wait() {
            Ok(None) => {
                if server.url == settings.server_url {
                    return StartOutcome::AlreadyStarted;
                }
                return StartOutcome::Failed(format!(
                    "an auto-started llama-server is still running for {}; restart the game after changing the URL",
                    server.url
                ));
            }
            Ok(Some(_)) => *guard = None,
            Err(error) => {
                *guard = None;
                return StartOutcome::Failed(format!("cannot inspect llama-server: {error}"));
            }
        }
    }

    if client::healthy(&settings.server_url) {
        return StartOutcome::AlreadyRunning;
    }

    let (host, port) = match local_host_port(&settings.server_url) {
        Ok(value) => value,
        Err(error) => return StartOutcome::Failed(error),
    };
    let exe = settings.server_exe.trim();
    let model = settings.server_model.trim();
    if exe.is_empty() {
        return StartOutcome::Failed("server executable is empty".to_string());
    }
    if model.is_empty() {
        return StartOutcome::Failed("server model path is empty".to_string());
    }
    if !Path::new(exe).is_file() {
        return StartOutcome::Failed(format!("server executable not found: {exe}"));
    }
    if !Path::new(model).is_file() {
        return StartOutcome::Failed(format!("server model not found: {model}"));
    }

    let log_path = crate::settings::install_root().join("llama-server.log");
    let _ = std::fs::create_dir_all(crate::settings::install_root());
    let (stdout, stderr) = log_streams(&log_path);
    let mut command = Command::new(exe);
    command
        .arg("-m")
        .arg(model)
        .arg("--host")
        .arg(&host)
        .arg("--port")
        .arg(port.to_string())
        .args(settings.server_args.split_whitespace())
        .stdin(Stdio::null())
        .stdout(stdout)
        .stderr(stderr);
    if let Some(dir) = Path::new(exe).parent() {
        if dir.is_dir() {
            command.current_dir(dir);
        }
    }
    hide_console(&mut command);

    match command.spawn() {
        Ok(child) => {
            let pid = child.id();
            *guard = Some(ManagedServer {
                child,
                url: settings.server_url.clone(),
            });
            StartOutcome::Started {
                pid,
                log: Some(log_path),
            }
        }
        Err(error) => StartOutcome::Failed(format!("cannot start {exe}: {error}")),
    }
}

/// Stops the server this mod started. A server that was already running (for example started
/// manually) has no managed child and is deliberately left alone.
pub fn stop() -> bool {
    let Some(slot) = SERVER.get() else {
        return false;
    };
    let mut guard = match slot.lock() {
        Ok(guard) => guard,
        Err(_) => return false,
    };
    let Some(mut server) = guard.take() else {
        return false;
    };
    if server.child.kill().is_ok() {
        let _ = server.child.wait();
        true
    } else {
        false
    }
}

fn local_host_port(server_url: &str) -> Result<(String, u16), String> {
    let rest = server_url.trim().trim_end_matches('/');
    let rest = rest
        .strip_prefix("http://")
        .ok_or_else(|| "auto-start supports only plain http:// URLs".to_string())?;
    let authority = rest.split('/').next().unwrap_or(rest);
    let (host, port) = authority
        .rsplit_once(':')
        .ok_or_else(|| format!("server URL has no port: {server_url}"))?;
    let host = match host {
        "localhost" => "127.0.0.1",
        other => other,
    };
    if host != "127.0.0.1" {
        return Err(format!(
            "auto-start supports only 127.0.0.1/localhost, got {host}"
        ));
    }
    let port = port
        .parse::<u16>()
        .map_err(|_| format!("invalid server port: {port}"))?;
    Ok((host.to_string(), port))
}

fn log_streams(path: &Path) -> (Stdio, Stdio) {
    let file = match OpenOptions::new().create(true).append(true).open(path) {
        Ok(file) => file,
        Err(_) => return (Stdio::null(), Stdio::null()),
    };
    let stderr = file
        .try_clone()
        .map(Stdio::from)
        .unwrap_or_else(|_| Stdio::null());
    (Stdio::from(file), stderr)
}

#[cfg(windows)]
fn hide_console(command: &mut Command) {
    use std::os::windows::process::CommandExt;

    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    command.creation_flags(CREATE_NO_WINDOW);
}

#[cfg(not(windows))]
fn hide_console(_command: &mut Command) {}

#[cfg(test)]
mod tests {
    use super::local_host_port;

    #[test]
    fn parses_local_server_urls() {
        assert_eq!(
            local_host_port("http://127.0.0.1:8081").expect("valid"),
            ("127.0.0.1".to_string(), 8081)
        );
        assert_eq!(
            local_host_port("http://localhost:8080/").expect("valid"),
            ("127.0.0.1".to_string(), 8080)
        );
    }

    #[test]
    fn rejects_remote_and_malformed_urls() {
        assert!(local_host_port("https://127.0.0.1:8081").is_err());
        assert!(local_host_port("http://192.168.0.2:8081").is_err());
        assert!(local_host_port("http://127.0.0.1").is_err());
        assert!(local_host_port("http://127.0.0.1:not-a-port").is_err());
    }
}
