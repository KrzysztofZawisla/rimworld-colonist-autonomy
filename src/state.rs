//! The mod's runtime state: the decision cache and the tick loop that drains the worker.

use std::collections::{HashMap, HashSet, VecDeque};
use std::time::{Duration, Instant};

use rimworld_rs::prelude::*;
use serde_json::{json, Value};

use crate::clef::client;
use crate::clef::installer;
use crate::clef::select::{ActionKind, PawnDecision};
use crate::clef::server;
use crate::clef::types::{Decision, DecisionRequest};
use crate::clef::worker::{Job, JobKind, JobResult, Worker};
use crate::settings::Settings;
use crate::{log, BRIDGE_TYPE};

const RECENT_DECISIONS: usize = 20;
const DECISION_COOLDOWN: Duration = Duration::from_secs(5);
const DECISION_RETENTION: Duration = Duration::from_secs(120);

#[derive(Default)]
pub struct PluginState {
    pub worker: Option<Worker>,
    pub cache: HashMap<i64, CachedDecision>,
    pub recent: VecDeque<DecisionRecord>,
    pub last_decided: HashMap<i64, Instant>,
    pub companion_ready: bool,
    pub ticks_since_refresh: u32,
    pub last_error: Option<String>,
    pub decisions: u64,
    pub applied: u64,
    pub skipped: u64,
}

pub struct CachedDecision {
    pub chosen: String,
    pub confidence: f64,
    pub mode: i32,
    pub built_at: Instant,
}

#[derive(Clone)]
pub struct DecisionRecord {
    pub label: String,
    pub chosen: String,
    pub confidence: f64,
    pub threshold: f64,
    pub options: usize,
    pub outcome: String,
    pub reason: String,
    pub probabilities: Vec<(String, f64)>,
}

impl PluginState {
    /// Clears everything that belongs to one game, keeping the worker thread and readiness.
    pub fn reset(&mut self) {
        self.cache.clear();
        self.recent.clear();
        self.last_decided.clear();
        self.ticks_since_refresh = 0;
        self.last_error = None;
        self.decisions = 0;
        self.applied = 0;
        self.skipped = 0;
    }

    /// Pushes the settings into the worker and the companion.
    pub fn configure(&self, ctx: &Context, settings: &Settings) {
        if let Some(worker) = &self.worker {
            worker.configure(crate::clef::client::Config {
                server_url: settings.server_url.clone(),
                model: settings.model.clone(),
                timeout_ms: settings.timeout_ms,
            });
        }
        if let Err(error) = ctx.companion_call(
            BRIDGE_TYPE,
            "Configure",
            &[
                json!(settings.enabled),
                json!(settings.mode.as_i32()),
                json!(settings.refresh_seconds),
                json!(settings.min_confidence),
                json!(settings.dry_run),
                json!(settings.debug),
                json!(settings.interrupt_threshold()),
                json!(settings.respect_schedule),
            ],
        ) {
            log::warn(ctx, format!("cannot configure the companion: {error}"));
        }
    }

    pub fn last_decision_summary(&self) -> String {
        match self.recent.front() {
            Some(record) => format!(
                "{}: {} {:.2}/{:.2} -> {}",
                record.label, record.chosen, record.confidence, record.threshold, record.outcome
            ),
            None => "no decisions yet".to_string(),
        }
    }

    pub fn show_decisions(&self, ctx: &mut Context) {
        let mut text = String::from("Recent model decisions\n\n");
        if self.recent.is_empty() {
            text.push_str("No decisions yet.");
        } else {
            for record in &self.recent {
                let top: Vec<String> = record
                    .probabilities
                    .iter()
                    .take(3)
                    .map(|(key, value)| format!("{key} {value:.2}"))
                    .collect();
                text.push_str(&format!(
                    "{}: {} (confidence {:.2}, threshold {:.2}, options {}) -> {}{}{}\n",
                    record.label,
                    record.chosen,
                    record.confidence,
                    record.threshold,
                    record.options,
                    record.outcome,
                    if record.reason.is_empty() {
                        String::new()
                    } else {
                        format!(", {}", record.reason)
                    },
                    if top.is_empty() {
                        String::new()
                    } else {
                        format!("\n  top: {}", top.join(", "))
                    }
                ));
            }
        }
        if let Err(error) = ctx.ui_dialog(
            Some("Colonist Autonomy"),
            &text,
            "Close",
            Some("clef_decisions"),
        ) {
            ctx.error(format!("[ColonistAutonomy] cannot show decisions: {error}"));
        }
    }

    fn record_decision(&mut self, label: &str, decision: &Decision, threshold: f64, outcome: &str) {
        let reason = if decision.chosen == "idle" {
            if decision
                .probabilities
                .iter()
                .any(|(key, value)| key != "idle" && *value > 0.05)
            {
                "model preferred idle over alternatives"
            } else {
                "no alternative actions offered"
            }
        } else {
            ""
        };
        self.recent.push_front(DecisionRecord {
            label: label.to_string(),
            chosen: decision.chosen.clone(),
            confidence: decision.confidence,
            threshold,
            options: decision.options,
            outcome: outcome.to_string(),
            reason: reason.to_string(),
            probabilities: decision.probabilities.clone(),
        });
        while self.recent.len() > RECENT_DECISIONS {
            self.recent.pop_back();
        }
    }

    fn cooldown_active(&self, pawn_id: i64) -> bool {
        self.last_decided
            .get(&pawn_id)
            .map(|at| at.elapsed() < DECISION_COOLDOWN)
            .unwrap_or(false)
    }

    pub fn tick(&mut self, ctx: &mut Context, settings: &Settings) {
        let results = self.drain_results();
        for result in results {
            self.handle_result(ctx, settings, result);
        }

        if !settings.enabled {
            return;
        }

        self.ticks_since_refresh = self.ticks_since_refresh.saturating_add(1);
        let intervals = ((settings.refresh_seconds as u64) * 2).max(2) as u32;
        if self.ticks_since_refresh >= intervals {
            self.ticks_since_refresh = 0;
            if self.companion_ready {
                if !installer::dependencies_present(settings)
                    && !client::healthy(&settings.server_url)
                {
                    return;
                }
                self.collect(ctx, settings);
            }
        }
    }

    pub fn on_companion_event(
        &mut self,
        ctx: &mut Context,
        settings: &Settings,
        name: &str,
        payload: &Value,
    ) -> Option<String> {
        match name {
            "clef.state" => {
                let request: DecisionRequest = match serde_json::from_value(payload.clone()) {
                    Ok(request) => request,
                    Err(error) => {
                        self.report_error(ctx, format!("bad clef.state payload: {error}"));
                        return Some("bad-payload".to_string());
                    }
                };
                if request
                    .pawns
                    .iter()
                    .any(|pawn| self.cooldown_active(pawn.id))
                {
                    return Some("cooldown".to_string());
                }
                let accepted = self
                    .worker
                    .as_ref()
                    .map(|worker| {
                        worker.submit(Job {
                            kind: JobKind::Single,
                            pawns: request.pawns,
                            state: request.state,
                            questions: request.questions,
                            mode: request.mode,
                        })
                    })
                    .unwrap_or(false);
                Some(if accepted {
                    "queued".to_string()
                } else {
                    "busy".to_string()
                })
            }
            "clef.decision" => {
                let Some(id) = payload.get("id").and_then(Value::as_i64) else {
                    return Some(String::new());
                };
                let consume = payload
                    .get("consume")
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                if self.cooldown_active(id) {
                    return Some(String::new());
                }
                let max_age = Duration::from_secs((settings.refresh_seconds as u64 * 2).max(10));
                let cached = self.cache.get(&id).map(|decision| {
                    (
                        decision.chosen.clone(),
                        decision.confidence,
                        decision.mode,
                        decision.built_at,
                    )
                });
                if let Some((chosen, confidence, mode, built_at)) = cached {
                    if built_at.elapsed() <= max_age {
                        let text = serde_json::to_string(&json!({
                            "chosen": chosen,
                            "confidence": confidence,
                            "mode": mode
                        }))
                        .unwrap_or_default();
                        if consume {
                            self.cache.remove(&id);
                        }
                        return Some(text);
                    }
                }
                Some(String::new())
            }
            "clef.log" => {
                if let Some(message) = payload.get("message").and_then(Value::as_str) {
                    log::info(ctx, format!("[ColonistAutonomy] {message}"));
                }
                None
            }
            "clef.stop_server" => {
                if server::stop() {
                    log::info(
                        ctx,
                        "[ColonistAutonomy] auto-start: stopped llama-server".to_string(),
                    );
                }
                None
            }
            _ => None,
        }
    }

    fn drain_results(&self) -> Vec<JobResult> {
        let mut results = Vec::new();
        if let Some(worker) = &self.worker {
            while let Some(result) = worker.try_recv() {
                results.push(result);
            }
        }
        results
    }

    fn collect(&mut self, ctx: &Context, settings: &Settings) {
        let started = Instant::now();
        let value = match ctx.companion_call(BRIDGE_TYPE, "CollectStates", &[]) {
            Ok(value) => value,
            Err(error) => {
                self.report_error(ctx, format!("CollectStates failed: {error}"));
                return;
            }
        };
        let collect_ms = started.elapsed().as_millis();

        let Some(text) = value.as_str() else {
            return;
        };
        if text.is_empty() || text == "null" {
            return;
        }
        let request: DecisionRequest = match serde_json::from_str(text) {
            Ok(request) => request,
            Err(error) => {
                self.report_error(ctx, format!("bad CollectStates payload: {error}"));
                return;
            }
        };

        log::info(
            ctx,
            format!(
                "[ColonistAutonomy] collect: {} pawn(s) in {} ms",
                request.pawns.len(),
                collect_ms
            ),
        );
        if settings.debug {
            if let Ok(stats) = ctx.stats(false) {
                log::debug(ctx, settings, format!("host stats: {stats}"));
            }
        }

        let accepted = self
            .worker
            .as_ref()
            .map(|worker| {
                worker.submit(Job {
                    kind: JobKind::Colony,
                    pawns: request.pawns,
                    state: request.state,
                    questions: request.questions,
                    mode: request.mode,
                })
            })
            .unwrap_or(false);
        if !accepted {
            log::debug(
                ctx,
                settings,
                "a colony request is already in flight".to_string(),
            );
        }
    }

    fn handle_result(&mut self, ctx: &mut Context, settings: &Settings, result: JobResult) {
        if let Some(error) = result.error {
            self.report_error(ctx, error);
            return;
        }

        let tokens = result
            .input_tokens
            .map(|count| format!(" (input {count} tokens)"))
            .unwrap_or_default();
        log::info(
            ctx,
            format!(
                "[ColonistAutonomy] request: {} pawn(s) in {} ms{}",
                result.pawns.len(),
                result.elapsed_ms,
                tokens
            ),
        );
        let mut pawns = result.pawns;
        pawns.sort_by(|left, right| {
            confidence_of(right)
                .partial_cmp(&confidence_of(left))
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| left.id.cmp(&right.id))
        });
        let mut claimed = HashSet::new();
        for pawn in pawns {
            self.handle_pawn(ctx, settings, pawn, &mut claimed);
        }
        self.last_decided
            .retain(|_, at| at.elapsed() < DECISION_RETENTION);
    }

    fn handle_pawn(
        &mut self,
        ctx: &mut Context,
        settings: &Settings,
        pawn: PawnDecision,
        claimed: &mut HashSet<String>,
    ) {
        self.decisions = self.decisions.saturating_add(1);
        match pawn.result {
            Ok(decision) => {
                if self.cooldown_active(pawn.id) {
                    self.skipped = self.skipped.saturating_add(1);
                    log::info(
                        ctx,
                        format!("[ColonistAutonomy] {}: cooldown -> vanilla", pawn.label),
                    );
                    return;
                }

                let threshold = settings.threshold_for(
                    &decision.chosen,
                    &decision.probabilities,
                    decision.options,
                );
                let kind = ActionKind::of(&decision.chosen);
                if decision.confidence < threshold {
                    self.skipped = self.skipped.saturating_add(1);
                    self.record_decision(&pawn.label, &decision, threshold, "below threshold");
                    log::info(
                        ctx,
                        format!(
                            "[ColonistAutonomy] {}: {} (confidence {:.2} < {:.2} {}, {} options) -> vanilla",
                            pawn.label,
                            decision.chosen,
                            decision.confidence,
                            threshold,
                            kind.label(),
                            decision.options
                        ),
                    );
                    return;
                }

                if let Some(key) = target_key(&decision.chosen) {
                    if claimed.contains(&key) {
                        self.skipped = self.skipped.saturating_add(1);
                        self.cache.remove(&pawn.id);
                        self.record_decision(&pawn.label, &decision, threshold, "duplicate target");
                        log::info(
                            ctx,
                            format!(
                                "[ColonistAutonomy] {}: {} target already claimed -> vanilla",
                                pawn.label, decision.chosen
                            ),
                        );
                        return;
                    }
                    claimed.insert(key);
                }

                let top: Vec<String> = decision
                    .probabilities
                    .iter()
                    .take(3)
                    .map(|(key, value)| format!("{key}={value:.2}"))
                    .collect();
                log::info(
                    ctx,
                    format!(
                        "[ColonistAutonomy] {}: {} (confidence {:.2} >= {:.2} {}, {} options, mode {}){}",
                        pawn.label,
                        decision.chosen,
                        decision.confidence,
                        threshold,
                        kind.label(),
                        decision.options,
                        settings.mode.label(),
                        if top.is_empty() {
                            String::new()
                        } else {
                            format!(" [{}]", top.join(", "))
                        }
                    ),
                );

                self.cache
                    .retain(|_, cached| cached.built_at.elapsed() < Duration::from_secs(600));
                self.cache.insert(
                    pawn.id,
                    CachedDecision {
                        chosen: decision.chosen.clone(),
                        confidence: decision.confidence,
                        mode: decision.mode,
                        built_at: Instant::now(),
                    },
                );
                self.last_decided.insert(pawn.id, Instant::now());

                if !settings.enabled {
                    self.record_decision(&pawn.label, &decision, threshold, "disabled");
                    return;
                }
                if settings.dry_run {
                    self.record_decision(&pawn.label, &decision, threshold, "dry run");
                    return;
                }

                let decision_json = serde_json::to_string(&json!({
                    "chosen": decision.chosen.clone(),
                    "confidence": decision.confidence,
                    "mode": decision.mode,
                }))
                .unwrap_or_default();

                match ctx.companion_call(
                    BRIDGE_TYPE,
                    "ApplyDecision",
                    &[
                        json!(pawn.id),
                        json!(decision_json),
                        json!(settings.interrupt_work),
                    ],
                ) {
                    Ok(value) => {
                        let outcome = value.as_str().unwrap_or("?");
                        if outcome == "applied" {
                            self.applied = self.applied.saturating_add(1);
                        }
                        if outcome != "deferred" && outcome != "busy" {
                            self.cache.remove(&pawn.id);
                        }
                        self.record_decision(&pawn.label, &decision, threshold, outcome);
                        log::debug(
                            ctx,
                            settings,
                            format!(
                                "{} -> {} (decisions={} applied={} skipped={})",
                                pawn.label, outcome, self.decisions, self.applied, self.skipped
                            ),
                        );
                    }
                    Err(error) => {
                        self.record_decision(&pawn.label, &decision, threshold, "error");
                        self.report_error(ctx, format!("ApplyDecision failed: {error}"));
                    }
                }
            }
            Err(error) => self.report_error(ctx, error),
        }
    }

    fn report_error(&mut self, ctx: &Context, message: String) {
        if self.last_error.as_deref() != Some(message.as_str()) {
            log::warn(ctx, message.clone());
            self.last_error = Some(message);
        }
    }
}

fn confidence_of(pawn: &PawnDecision) -> f64 {
    pawn.result
        .as_ref()
        .map(|decision| decision.confidence)
        .unwrap_or(-1.0)
}

fn target_key(chosen: &str) -> Option<String> {
    if let Some(id) = chosen.strip_prefix("social:") {
        return Some(format!("social:{id}"));
    }
    let rest = chosen.strip_prefix("work:")?;
    let parts: Vec<&str> = rest.split(':').collect();
    if parts.len() == 2 {
        return Some(format!("work:{}", parts[1]));
    }
    if parts.len() == 3 && parts[1] == "cell" {
        return Some(format!("cell:{}", parts[2]));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::target_key;

    #[test]
    fn target_keys_cover_social_work_and_cells() {
        assert_eq!(target_key("social:42"), Some("social:42".to_string()));
        assert_eq!(
            target_key("work:HaulGeneral:123"),
            Some("work:123".to_string())
        );
        assert_eq!(
            target_key("work:GrowerSow:cell:12,34"),
            Some("cell:12,34".to_string())
        );
        assert_eq!(target_key("work:Growing"), None);
        assert_eq!(target_key("eat"), None);
        assert_eq!(target_key("idle"), None);
    }
}
