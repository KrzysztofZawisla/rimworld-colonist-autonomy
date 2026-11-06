//! The mod's options and how they are read back from the bridge.

use std::path::PathBuf;

use rimworld_rs::prelude::*;

use crate::clef::installer::CLEF_FLASH_FILE;
use crate::clef::select::{ActionKind, Mode};

/// Name of the llama.cpp server binary on this platform.
pub const SERVER_EXE_NAME: &str = if cfg!(windows) {
    "llama-server.exe"
} else {
    "llama-server"
};

/// GPU backend the pinned llama.cpp build uses on this platform.
pub const LLAMA_BACKEND: &str = if cfg!(target_os = "macos") {
    "Metal"
} else {
    "Vulkan"
};

/// Per-user install root for the optional one-click dependencies.
///
/// `COLONIST_AUTONOMY_HOME` overrides it, which is useful for tests and portable installs.
pub fn install_root() -> PathBuf {
    if let Some(root) = std::env::var_os("COLONIST_AUTONOMY_HOME") {
        return PathBuf::from(root);
    }
    dirs::data_local_dir()
        .map(|dir| dir.join("ColonistAutonomy"))
        .unwrap_or_else(|| std::env::temp_dir().join("ColonistAutonomy"))
}

fn default_server_exe() -> String {
    install_root()
        .join("llama.cpp")
        .join(SERVER_EXE_NAME)
        .to_string_lossy()
        .into_owned()
}

fn default_server_model() -> String {
    install_root()
        .join("models")
        .join(CLEF_FLASH_FILE)
        .to_string_lossy()
        .into_owned()
}

/// The settings the player can change in the mod options window.
#[derive(Clone, Debug)]
pub struct Settings {
    pub enabled: bool,
    pub mode: Mode,
    pub server_url: String,
    pub refresh_seconds: u32,
    pub min_confidence: f64,
    pub interrupt_work: bool,
    pub respect_schedule: bool,
    pub dry_run: bool,
    pub debug: bool,
    pub model: String,
    pub timeout_ms: u64,
    pub auto_start_server: bool,
    pub server_exe: String,
    pub server_model: String,
    pub server_args: String,
}

impl Settings {
    /// The confidence a choice must reach to be applied. The base setting is deliberately not
    /// the same number for every action: work is cheap to try, interrupting a job is expensive.
    pub fn threshold_for(
        &self,
        chosen: &str,
        probabilities: &[(String, f64)],
        options: usize,
    ) -> f64 {
        let base = self.min_confidence;
        match ActionKind::of(chosen) {
            ActionKind::Work => self.work_threshold(options),
            ActionKind::Social => base,
            ActionKind::Need => (base - 0.05).max(0.05),
            ActionKind::Drug => base,
            ActionKind::Threat => (base - 0.20).max(0.05),
            ActionKind::Idle => {
                let alternatives = probabilities
                    .iter()
                    .any(|(key, value)| key != "idle" && *value > 0.05);
                (base + if alternatives { 0.20 } else { 0.10 }).min(1.0)
            }
            ActionKind::Other => base,
        }
    }

    /// Work is spread over many similar targets, so its threshold follows the size of the
    /// question: 2.5x uniform, never below 0.20 and never above the old work baseline.
    fn work_threshold(&self, options: usize) -> f64 {
        let count = options.max(2) as f64;
        let baseline = (self.min_confidence - 0.10).max(0.20);
        (2.5 / count).max(0.20).min(baseline)
    }

    pub fn interrupt_threshold(&self) -> f64 {
        (self.min_confidence + 0.20).min(1.0)
    }
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: true,
            mode: Mode::Action,
            server_url: "http://127.0.0.1:8081".to_string(),
            refresh_seconds: 30,
            min_confidence: 0.40,
            interrupt_work: true,
            respect_schedule: true,
            dry_run: false,
            debug: false,
            model: "clef".to_string(),
            timeout_ms: 60_000,
            auto_start_server: false,
            server_exe: default_server_exe(),
            server_model: default_server_model(),
            server_args: "-ngl 99 -c 16384 -b 8192 -ub 8192".to_string(),
        }
    }
}

/// Declares the fields shown in the mod options window.
pub fn register(ctx: &mut Context) -> rimworld_rs::Result<()> {
    let server_exe = default_server_exe();
    let server_model = default_server_model();
    let install_llama = format!("Install llama.cpp ({LLAMA_BACKEND})");
    ctx.settings_register(&[
        SettingSpec::bool("enabled", "Enabled", true),
        SettingSpec::choice(
            "mode",
            "Decision mode",
            "1",
            &[
                ("0", "Intent (game picks target)"),
                ("1", "Action (model picks target)"),
                ("2", "Intent + target (two steps)"),
                ("3", "Hybrid (auto: intent or action)"),
            ],
        ),
        SettingSpec::label("mode_status", "Mode details"),
        SettingSpec::string("server_url", "Model server URL", "http://127.0.0.1:8081"),
        SettingSpec::label("server_status", "Server status"),
        SettingSpec::action("server_test", "Test server"),
        SettingSpec::int("refresh_seconds", "Refresh seconds", 30, 5.0, 300.0),
        SettingSpec::float("min_confidence", "Base confidence", 0.40, 0.0, 1.0),
        SettingSpec::bool("interrupt_work", "Interrupt current work", true),
        SettingSpec::bool("respect_schedule", "Respect sleep schedule", true),
        SettingSpec::bool("dry_run", "Dry run (log only)", false),
        SettingSpec::bool("debug", "Debug log", false),
        SettingSpec::label("decisions_status", "Last decision"),
        SettingSpec::action("decisions_show", "Show recent decisions"),
        SettingSpec::bool("auto_start_server", "Auto-start llama-server", false),
        SettingSpec::string("server_exe", "llama-server executable", &server_exe),
        SettingSpec::string("server_model", "Model file (.gguf)", &server_model),
        SettingSpec::string(
            "server_args",
            "llama-server extra args",
            "-ngl 99 -c 16384 -b 8192 -ub 8192",
        ),
        SettingSpec::label("deps_status", "Dependencies"),
        SettingSpec::action("deps_install", "Install / check dependencies"),
        SettingSpec::action("deps_install_llama", &install_llama),
        SettingSpec::action(
            "deps_download_flash",
            "Download Clef Flash Q8 (9.7 GB) — recommended",
        ),
        SettingSpec::action(
            "deps_download_clef",
            "Download Clef Q4 (17.6 GB) — optional",
        ),
        SettingSpec::action("deps_cancel", "Cancel dependency download"),
    ])
}

/// Reads every setting, falling back to the defaults for missing or malformed values.
pub fn load(ctx: &Context) -> rimworld_rs::Result<Settings> {
    let mut settings = Settings::default();

    if let Some(value) = ctx.setting_get("enabled")? {
        settings.enabled = parse_bool(&value, settings.enabled);
    }
    if let Some(value) = ctx.setting_get("mode")? {
        settings.mode = Mode::from_i32(value.parse::<i32>().unwrap_or(settings.mode.as_i32()));
    }
    if let Some(value) = ctx.setting_get("server_url")? {
        let trimmed = value.trim();
        if !trimmed.is_empty() {
            settings.server_url = trimmed.to_string();
        }
    }
    if let Some(value) = ctx.setting_get("refresh_seconds")? {
        settings.refresh_seconds = value
            .parse::<i32>()
            .unwrap_or(settings.refresh_seconds as i32)
            .clamp(5, 300) as u32;
    }
    if let Some(value) = ctx.setting_get("min_confidence")? {
        settings.min_confidence = value
            .parse::<f64>()
            .unwrap_or(settings.min_confidence)
            .clamp(0.0, 1.0);
    }
    if let Some(value) = ctx.setting_get("interrupt_work")? {
        settings.interrupt_work = parse_bool(&value, settings.interrupt_work);
    }
    if let Some(value) = ctx.setting_get("respect_schedule")? {
        settings.respect_schedule = parse_bool(&value, settings.respect_schedule);
    }
    if let Some(value) = ctx.setting_get("dry_run")? {
        settings.dry_run = parse_bool(&value, settings.dry_run);
    }
    if let Some(value) = ctx.setting_get("debug")? {
        settings.debug = parse_bool(&value, settings.debug);
    }
    if let Some(value) = ctx.setting_get("auto_start_server")? {
        settings.auto_start_server = parse_bool(&value, settings.auto_start_server);
    }
    if let Some(value) = ctx.setting_get("server_exe")? {
        let trimmed = value.trim();
        if !trimmed.is_empty() {
            settings.server_exe = trimmed.to_string();
        }
    }
    if let Some(value) = ctx.setting_get("server_model")? {
        let trimmed = value.trim();
        if !trimmed.is_empty() {
            settings.server_model = trimmed.to_string();
        }
    }
    if let Some(value) = ctx.setting_get("server_args")? {
        settings.server_args = value.trim().to_string();
    }
    Ok(settings)
}

fn parse_bool(value: &str, fallback: bool) -> bool {
    match value.trim().to_ascii_lowercase().as_str() {
        "true" | "1" | "yes" | "on" => true,
        "false" | "0" | "no" | "off" => false,
        _ => fallback,
    }
}

#[cfg(test)]
mod tests {
    use super::{parse_bool, Settings, CLEF_FLASH_FILE};

    #[test]
    fn the_default_model_is_the_flash_one() {
        assert!(Settings::default().server_model.ends_with(CLEF_FLASH_FILE));
    }

    #[test]
    fn the_sleep_gate_is_on_by_default() {
        assert!(Settings::default().respect_schedule);
    }

    #[test]
    fn thresholds_follow_the_action_kind() {
        let settings = Settings::default();
        assert!((settings.threshold_for("work:HaulGeneral:1", &[], 16) - 0.20).abs() < 1e-9);
        assert!((settings.threshold_for("work:HaulGeneral:1", &[], 8) - 0.30).abs() < 1e-9);
        assert!((settings.threshold_for("work:HaulGeneral:1", &[], 6) - 0.30).abs() < 1e-9);
        assert!((settings.threshold_for("work:HaulGeneral:1", &[], 4) - 0.30).abs() < 1e-9);
        assert!((settings.threshold_for("social:1", &[], 16) - 0.40).abs() < 1e-9);
        assert!((settings.threshold_for("drug:1", &[], 16) - 0.40).abs() < 1e-9);
        assert!((settings.threshold_for("eat", &[], 16) - 0.35).abs() < 1e-9);
        assert!((settings.threshold_for("flee", &[], 16) - 0.20).abs() < 1e-9);
        assert!((settings.threshold_for("idle", &[], 16) - 0.50).abs() < 1e-9);
        let with_alternatives = vec![
            ("idle".to_string(), 0.60),
            ("work:HaulGeneral:1".to_string(), 0.20),
        ];
        assert!((settings.threshold_for("idle", &with_alternatives, 16) - 0.60).abs() < 1e-9);
        assert!((settings.interrupt_threshold() - 0.60).abs() < 1e-9);
    }

    #[test]
    fn parses_common_booleans() {
        assert!(parse_bool("true", false));
        assert!(parse_bool(" 1 ", false));
        assert!(parse_bool("yes", false));
        assert!(parse_bool("on", false));
        assert!(!parse_bool("false", true));
        assert!(!parse_bool("0", true));
        assert!(!parse_bool("no", true));
        assert!(!parse_bool("off", true));
    }

    #[test]
    fn keeps_the_fallback_for_garbage() {
        assert!(parse_bool("maybe", true));
        assert!(!parse_bool("", false));
    }
}
