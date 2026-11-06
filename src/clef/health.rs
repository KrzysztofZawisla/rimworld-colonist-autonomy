//! Asynchronous server probe behind the settings window's "Test server" button.

use std::sync::{Mutex, OnceLock};
use std::thread;

static STATUS: OnceLock<Mutex<String>> = OnceLock::new();

fn status_cell() -> &'static Mutex<String> {
    STATUS.get_or_init(|| Mutex::new("not tested".to_string()))
}

fn set_status(message: String) {
    if let Ok(mut value) = status_cell().lock() {
        *value = message;
    }
}

pub fn status() -> String {
    status_cell()
        .lock()
        .map(|value| value.clone())
        .unwrap_or_else(|_| "status unavailable".to_string())
}

pub fn test(server_url: String) {
    let url = server_url.trim().to_string();
    set_status("testing...".to_string());
    if let Err(error) = thread::Builder::new()
        .name("clef-health".to_string())
        .spawn(move || {
            let ok = crate::clef::client::healthy(&url);
            set_status(if ok {
                format!("{url}: ok")
            } else {
                format!("{url}: no response")
            });
        })
    {
        set_status(format!("cannot start the probe: {error}"));
    }
}
