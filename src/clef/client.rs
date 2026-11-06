//! A blocking HTTP client for llama-server's `/v1/systemone`.
//!
//! Only plain HTTP is needed (the default server is `http://127.0.0.1:8081`), so the mod does not
//! pull in a TLS stack. The call runs on a worker thread; the game thread never waits for it.

use serde_json::Value;
use std::time::Duration;

use crate::clef::types::{SystemOneRequest, SystemOneResponse};

#[derive(Debug, Clone)]
pub struct Config {
    pub server_url: String,
    pub model: String,
    pub timeout_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct AgentKey {
    server_url: String,
    timeout_ms: u64,
}

/// Reuses one `ureq::Agent` across requests so its connection pool and keep-alive survive.
/// The agent is rebuilt only when the URL or the timeout changes.
#[derive(Default)]
pub struct AgentPool {
    key: Option<AgentKey>,
    agent: Option<ureq::Agent>,
}

impl AgentPool {
    pub fn get(&mut self, config: &Config) -> &ureq::Agent {
        let key = AgentKey {
            server_url: config.server_url.clone(),
            timeout_ms: config.timeout_ms,
        };
        if self.key.as_ref() != Some(&key) || self.agent.is_none() {
            self.agent = Some(build_agent(config));
            self.key = Some(key);
        }
        self.agent.as_ref().expect("the agent was just built")
    }
}

/// Sends one decision request, retrying once with a truncated state when llama-server reports
/// that the state is too large for its batch.
pub fn ask(
    config: &Config,
    agent: &ureq::Agent,
    state: &str,
    questions: &Value,
) -> Result<SystemOneResponse, String> {
    let url = format!("{}/v1/systemone", config.server_url.trim_end_matches('/'));
    let body = request_body(config, state, questions)?;

    match post(agent, &url, &body) {
        Ok(text) => parse(&text),
        Err(error) if error.contains("too large to process") && state.len() > 4_000 => {
            let shorter = truncate_state(state);
            let retry = request_body(config, &shorter, questions)?;
            post(agent, &url, &retry).and_then(|text| parse(&text))
        }
        Err(error) => Err(error),
    }
}

fn build_agent(config: &Config) -> ureq::Agent {
    let timeout = config.timeout_ms.max(1_000);
    ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_millis(timeout.clamp(500, 5_000)))
        .timeout_read(Duration::from_millis(timeout))
        .timeout_write(Duration::from_millis(timeout))
        .build()
}

fn request_body(config: &Config, state: &str, questions: &Value) -> Result<String, String> {
    serde_json::to_string(&SystemOneRequest {
        model: &config.model,
        state,
        questions,
    })
    .map_err(|error| format!("cannot encode the request: {error}"))
}

fn post(agent: &ureq::Agent, url: &str, body: &str) -> Result<String, String> {
    match agent
        .post(url)
        .set("Content-Type", "application/json")
        .send_string(body)
    {
        Ok(response) => response
            .into_string()
            .map_err(|error| format!("cannot read the response: {error}")),
        Err(ureq::Error::Status(code, response)) => {
            let text = response.into_string().unwrap_or_default();
            Err(format!("HTTP {code}: {}", snippet(&text)))
        }
        Err(ureq::Error::Transport(error)) => Err(format!("transport: {error}")),
    }
}

fn parse(text: &str) -> Result<SystemOneResponse, String> {
    serde_json::from_str::<SystemOneResponse>(text)
        .map_err(|error| format!("cannot parse the response: {error}: {}", snippet(text)))
}

fn snippet(text: &str) -> String {
    let trimmed = text.trim();
    let mut out: String = trimmed.chars().take(400).collect();
    if trimmed.chars().count() > 400 {
        out.push_str("...");
    }
    out
}

/// Fast probe used before auto-starting a local server. Deliberately does not use the long
/// request timeout: at game load we only want to know whether something is already listening.
pub fn healthy(server_url: &str) -> bool {
    let url = format!("{}/health", server_url.trim_end_matches('/'));
    let agent = ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_millis(400))
        .timeout_read(Duration::from_millis(600))
        .timeout_write(Duration::from_millis(600))
        .build();
    matches!(
        agent.get(&url).call(),
        Ok(response) if response.status() == 200
    )
}

/// Keeps the head and the tail of a state that the server refused as too large.
fn truncate_state(state: &str) -> String {
    let size = (state.len() / 2).max(2_000);
    let head_size = size * 7 / 10;
    let tail_size = size.saturating_sub(head_size);
    let mut head: String = state.chars().take(head_size).collect();
    let tail: String = state
        .chars()
        .rev()
        .take(tail_size)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .collect();
    head.push_str("\n...[state truncated by Colonist Autonomy]...\n");
    head.push_str(&tail);
    head
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn truncation_keeps_both_ends() {
        let state = "a".repeat(10_000);
        let truncated = truncate_state(&state);
        assert!(truncated.len() < state.len());
        assert!(truncated.contains("[state truncated by Colonist Autonomy]"));
        assert!(truncated.starts_with('a'));
        assert!(truncated.ends_with('a'));
    }

    #[test]
    fn parsing_rejects_garbage() {
        assert!(parse("not json").is_err());
    }

    #[test]
    fn parsing_accepts_an_empty_answer_map() {
        let response = parse(r#"{"answers":{}}"#).expect("valid response");
        assert!(response.answers.is_empty());
    }

    #[test]
    fn snippets_are_capped() {
        let text = snippet(&"x".repeat(1_000));
        assert!(text.len() <= 403);
        assert!(text.ends_with("..."));
    }

    #[test]
    fn healthy_accepts_a_local_server() {
        let mut server = mockito::Server::new();
        let health = server.mock("GET", "/health").with_status(200).create();
        assert!(healthy(&server.url()));
        health.assert();
    }

    #[test]
    fn the_agent_pool_rebuilds_only_when_the_url_or_timeout_changes() {
        let mut pool = AgentPool::default();
        let config = Config {
            server_url: "http://127.0.0.1:8081".to_string(),
            model: "clef".to_string(),
            timeout_ms: 20_000,
        };
        pool.get(&config);
        let first_key = pool.key.clone();
        pool.get(&config);
        assert_eq!(pool.key, first_key);

        let mut changed = config.clone();
        changed.timeout_ms = 30_000;
        pool.get(&changed);
        assert_ne!(pool.key, first_key);
    }
}
