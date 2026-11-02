//! The background thread that talks to llama-server.
//!
//! `Context` is not `Send`, so this thread never touches the game: it receives plain JSON and
//! sends plain results back. The game thread drains the result channel in `on_tick`.

use serde_json::Value;
use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use crate::clef::client::{self, AgentPool, Config};
use crate::clef::select::{self, PawnDecision};
use crate::clef::types::PawnRef;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum JobKind {
    /// One request covering every pawn in a colony refresh.
    Colony,
    /// One request for the think node's single pawn.
    Single,
}

pub struct Job {
    pub kind: JobKind,
    pub pawns: Vec<PawnRef>,
    pub state: String,
    pub questions: Value,
    pub mode: i32,
}

pub struct JobResult {
    pub pawns: Vec<PawnDecision>,
    pub error: Option<String>,
    pub elapsed_ms: u64,
    pub input_tokens: Option<i64>,
}

pub struct Worker {
    sender: mpsc::Sender<Job>,
    results: mpsc::Receiver<JobResult>,
    in_flight: Arc<Mutex<HashSet<i64>>>,
    colony_in_flight: Arc<AtomicBool>,
    config: Arc<Mutex<Config>>,
}

impl Worker {
    pub fn start() -> Self {
        let (job_tx, job_rx) = mpsc::channel::<Job>();
        let (result_tx, result_rx) = mpsc::channel::<JobResult>();
        let in_flight = Arc::new(Mutex::new(HashSet::new()));
        let colony_in_flight = Arc::new(AtomicBool::new(false));
        let config = Arc::new(Mutex::new(default_config()));
        let failures = Arc::new(AtomicUsize::new(0));
        let next_attempt = Arc::new(Mutex::new(None::<Instant>));

        let thread_in_flight = Arc::clone(&in_flight);
        let thread_colony = Arc::clone(&colony_in_flight);
        let thread_config = Arc::clone(&config);

        thread::Builder::new()
            .name("clef-worker".to_string())
            .spawn(move || {
                run(
                    job_rx,
                    result_tx,
                    thread_in_flight,
                    thread_colony,
                    thread_config,
                    failures,
                    next_attempt,
                );
            })
            .expect("cannot start the Clef worker thread");

        Self {
            sender: job_tx,
            results: result_rx,
            in_flight,
            colony_in_flight,
            config,
        }
    }

    pub fn configure(&self, config: Config) {
        if let Ok(mut current) = self.config.lock() {
            *current = config;
        }
    }

    /// Queues a request unless the same kind of work is already in flight.
    pub fn submit(&self, job: Job) -> bool {
        match job.kind {
            JobKind::Colony => {
                if self.colony_in_flight.swap(true, Ordering::SeqCst) {
                    return false;
                }
                if self.sender.send(job).is_err() {
                    self.colony_in_flight.store(false, Ordering::SeqCst);
                    return false;
                }
                true
            }
            JobKind::Single => {
                if self.colony_in_flight.load(Ordering::SeqCst) {
                    return false;
                }
                let ids: Vec<i64> = job.pawns.iter().map(|pawn| pawn.id).collect();
                let mut in_flight = match self.in_flight.lock() {
                    Ok(value) => value,
                    Err(error) => {
                        log_error(format!("in-flight lock poisoned: {error}"));
                        return false;
                    }
                };
                if ids.iter().any(|id| in_flight.contains(id)) {
                    return false;
                }
                for id in &ids {
                    in_flight.insert(*id);
                }
                if self.sender.send(job).is_err() {
                    for id in &ids {
                        in_flight.remove(id);
                    }
                    return false;
                }
                true
            }
        }
    }

    pub fn try_recv(&self) -> Option<JobResult> {
        self.results.try_recv().ok()
    }
}

fn default_config() -> Config {
    Config {
        server_url: "http://127.0.0.1:8081".to_string(),
        model: "clef".to_string(),
        timeout_ms: 60_000,
    }
}

fn run(
    jobs: mpsc::Receiver<Job>,
    results: mpsc::Sender<JobResult>,
    in_flight: Arc<Mutex<HashSet<i64>>>,
    colony_in_flight: Arc<AtomicBool>,
    config: Arc<Mutex<Config>>,
    failures: Arc<AtomicUsize>,
    next_attempt: Arc<Mutex<Option<Instant>>>,
) {
    let mut agents = AgentPool::default();
    loop {
        let job = match jobs.recv_timeout(Duration::from_millis(250)) {
            Ok(job) => job,
            Err(mpsc::RecvTimeoutError::Timeout) => continue,
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        };

        let started = Instant::now();
        let blocked = next_attempt
            .lock()
            .ok()
            .and_then(|value| *value)
            .map(|at| at > Instant::now())
            .unwrap_or(false);

        let (pawns, error, input_tokens) = if blocked {
            (
                Vec::new(),
                Some("model backoff is active".to_string()),
                None,
            )
        } else {
            let snapshot = config
                .lock()
                .map(|value| value.clone())
                .unwrap_or_else(|_| default_config());
            let agent = agents.get(&snapshot);
            let response = client::ask(&snapshot, agent, &job.state, &job.questions);
            match response {
                Ok(response) => {
                    failures.store(0, Ordering::Relaxed);
                    if let Ok(mut next) = next_attempt.lock() {
                        *next = None;
                    }
                    let tokens = response.usage.as_ref().and_then(|usage| usage.input_tokens);
                    (
                        select::decide_colony(&response, job.mode, &job.pawns),
                        None,
                        tokens,
                    )
                }
                Err(error) => {
                    let count = failures.fetch_add(1, Ordering::Relaxed) + 1;
                    if count >= 3 {
                        if let Ok(mut next) = next_attempt.lock() {
                            *next = Some(Instant::now() + Duration::from_secs(60));
                        }
                    }
                    (Vec::new(), Some(error), None)
                }
            }
        };
        let elapsed_ms = started.elapsed().as_millis() as u64;

        match job.kind {
            JobKind::Colony => colony_in_flight.store(false, Ordering::SeqCst),
            JobKind::Single => {
                if let Ok(mut set) = in_flight.lock() {
                    for pawn in &job.pawns {
                        set.remove(&pawn.id);
                    }
                }
            }
        }

        let _ = results.send(JobResult {
            pawns,
            error,
            elapsed_ms,
            input_tokens,
        });
    }
}

fn log_error(message: String) {
    // The worker cannot reach the game log; the message is only a last-resort diagnostic.
    eprintln!("[ColonistAutonomy] {message}");
}
