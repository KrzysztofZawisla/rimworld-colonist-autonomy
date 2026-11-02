//! Colonist Autonomy - colonists decide with a local decision model.

mod clef;
mod log;
mod settings;
mod state;

use rimworld_rs::prelude::*;
use serde_json::Value;

use clef::installer;
use clef::server::{self, StartOutcome};
use clef::worker::Worker;
use settings::Settings;
use state::PluginState;

const BRIDGE_TYPE: &str = "SmartColonistsCompanion.ClefBridge";

#[derive(Default)]
struct Plugin {
    settings: Settings,
    state: PluginState,
    prompted_dependencies: bool,
    pending_dependency_prompt: bool,
}

impl Mod for Plugin {
    fn on_init(&mut self, ctx: &mut Context) {
        if let Err(error) = settings::register(ctx) {
            ctx.error(format!(
                "[ColonistAutonomy] cannot register settings: {error}"
            ));
        }
        self.settings = settings::load(ctx).unwrap_or_default();
        self.state.worker = Some(Worker::start());
        self.state.configure(ctx, &self.settings);
        ctx.log("[ColonistAutonomy] initialized");
    }

    fn on_defs_loaded(&mut self, ctx: &mut Context) {
        match ctx.companion_call(BRIDGE_TYPE, "Init", &[]) {
            Ok(value) => {
                let ready = value.as_str() == Some("ready");
                self.state.companion_ready = ready;
                if ready {
                    ctx.log("[ColonistAutonomy] companion ready");
                } else {
                    ctx.error(format!("[ColonistAutonomy] companion not ready: {value}"));
                }
            }
            Err(error) => {
                self.state.companion_ready = false;
                ctx.error(format!("[ColonistAutonomy] companion init failed: {error}"));
            }
        }
    }

    fn on_game_started(&mut self, ctx: &mut Context) {
        self.start(ctx, "new game");
    }

    fn on_game_loaded(&mut self, ctx: &mut Context) {
        self.start(ctx, "save loaded");
    }

    fn on_tick(&mut self, ctx: &mut Context) {
        self.state.tick(ctx, &self.settings);
        self.flush_dependency_prompt(ctx);
    }

    fn on_settings_changed(&mut self, ctx: &mut Context, key: &str, _value: &str) {
        self.reload(ctx);
        if matches!(
            key,
            "enabled"
                | "auto_start_server"
                | "server_url"
                | "server_exe"
                | "server_model"
                | "server_args"
        ) {
            self.ensure_server(ctx);
        }
    }

    fn on_companion_event(
        &mut self,
        ctx: &mut Context,
        _source: &str,
        name: &str,
        payload: &Value,
    ) -> Option<String> {
        if name == "settings_label" {
            if payload.get("id").and_then(Value::as_str) == Some("mode_status") {
                return Some(self.settings.mode.description().to_string());
            }
            if payload.get("id").and_then(Value::as_str) == Some("decisions_status") {
                return Some(self.state.last_decision_summary());
            }
            if let Some((exe, model)) = installer::take_completed() {
                if let Some(path) = exe {
                    let value = path.to_string_lossy().into_owned();
                    if let Err(error) = ctx.setting_set("server_exe", Some(value.as_str())) {
                        ctx.error(format!(
                            "[ColonistAutonomy] cannot store server_exe: {error}"
                        ));
                    }
                    self.settings.server_exe = value;
                }
                if let Some(path) = model {
                    let value = path.to_string_lossy().into_owned();
                    if let Err(error) = ctx.setting_set("server_model", Some(value.as_str())) {
                        ctx.error(format!(
                            "[ColonistAutonomy] cannot store server_model: {error}"
                        ));
                    }
                    self.settings.server_model = value;
                }
            }
            return Some(installer::status(&self.settings));
        }
        self.state
            .on_companion_event(ctx, &self.settings, name, payload)
    }

    fn on_ui_action(&mut self, ctx: &mut Context, action: &UiAction) {
        if action.handler == "settings_action" {
            if action.id == "decisions_show" {
                self.state.show_decisions(ctx);
                return;
            }
            installer::handle_action(ctx, &self.settings, &action.id);
        }
    }
}

impl Plugin {
    fn start(&mut self, ctx: &mut Context, why: &str) {
        self.state.reset();
        self.reload(ctx);
        self.schedule_dependency_prompt();
        self.ensure_server(ctx);
        if let Err(error) = ctx.set_tick_interval(30) {
            ctx.error(format!(
                "[ColonistAutonomy] cannot set tick interval: {error}"
            ));
        }
        ctx.log(format!(
            "[ColonistAutonomy] game ready ({why}) mode={} refresh={}s",
            self.settings.mode.label(),
            self.settings.refresh_seconds
        ));
    }

    fn schedule_dependency_prompt(&mut self) {
        if self.prompted_dependencies || self.pending_dependency_prompt {
            return;
        }
        if installer::dependencies_present(&self.settings) {
            return;
        }
        if clef::client::healthy(&self.settings.server_url) {
            return;
        }
        self.pending_dependency_prompt = true;
    }

    fn flush_dependency_prompt(&mut self, ctx: &mut Context) {
        if !self.pending_dependency_prompt || self.prompted_dependencies {
            return;
        }
        self.pending_dependency_prompt = false;
        if installer::dependencies_present(&self.settings) {
            return;
        }
        if clef::client::healthy(&self.settings.server_url) {
            return;
        }
        let prompt = format!(
            "{} or the model file is missing. Open Mod settings → Colonist Autonomy → Install / check dependencies. Server auto-start is skipped until the dependencies are installed.",
            crate::settings::SERVER_EXE_NAME
        );
        match ctx.ui_dialog(
            Some("Colonist Autonomy"),
            &prompt,
            "OK",
            Some("clef_deps_prompt"),
        ) {
            Ok(()) => {
                self.prompted_dependencies = true;
                ctx.log("[ColonistAutonomy] dependency prompt shown");
            }
            Err(error) => {
                ctx.error(format!(
                    "[ColonistAutonomy] cannot show the dependency prompt: {error}"
                ));
            }
        }
    }

    fn ensure_server(&self, ctx: &mut Context) {
        if !installer::dependencies_present(&self.settings)
            && !clef::client::healthy(&self.settings.server_url)
        {
            crate::log::debug(
                ctx,
                &self.settings,
                "auto-start skipped: dependencies missing".to_string(),
            );
            return;
        }
        match server::ensure_started(&self.settings) {
            StartOutcome::Started { pid, log } => {
                let log_suffix = log
                    .map(|path| format!(", log: {}", path.display()))
                    .unwrap_or_default();
                ctx.log(format!(
                    "[ColonistAutonomy] auto-start: started llama-server (pid {pid}{log_suffix})"
                ));
            }
            StartOutcome::AlreadyRunning => crate::log::debug(
                ctx,
                &self.settings,
                "auto-start: server already running".to_string(),
            ),
            StartOutcome::AlreadyStarted => crate::log::debug(
                ctx,
                &self.settings,
                "auto-start: server already starting".to_string(),
            ),
            StartOutcome::Disabled => {
                crate::log::debug(ctx, &self.settings, "auto-start: disabled".to_string())
            }
            StartOutcome::Failed(error) => {
                ctx.error(format!("[ColonistAutonomy] auto-start failed: {error}"));
            }
        }
    }

    fn reload(&mut self, ctx: &mut Context) {
        let previous_mode = self.settings.mode;
        match settings::load(ctx) {
            Ok(settings) => {
                self.settings = settings;
                if self.settings.mode != previous_mode {
                    self.state.cache.clear();
                }
                self.state.configure(ctx, &self.settings);
                ctx.log(format!(
                    "[ColonistAutonomy] settings: enabled={} mode={} refresh={}s min_conf={:.2} interrupt={} dry_run={} debug={} auto_start={} server={}",
                    self.settings.enabled,
                    self.settings.mode.label(),
                    self.settings.refresh_seconds,
                    self.settings.min_confidence,
                    self.settings.interrupt_work,
                    self.settings.dry_run,
                    self.settings.debug,
                    self.settings.auto_start_server,
                    self.settings.server_url
                ));
            }
            Err(error) => ctx.error(format!("[ColonistAutonomy] cannot read settings: {error}")),
        }
    }
}

rimworld_rs::export_mod!(Plugin);
