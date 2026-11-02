//! Wire types for the systemone decision API.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;

/// One pawn named in a decision request.
#[derive(Debug, Clone, Deserialize)]
pub struct PawnRef {
    pub id: i64,
    pub label: String,
    #[serde(default)]
    pub options: usize,
}

/// The companion's payload for a colony: one state, per-pawn questions, and the mode.
#[derive(Debug, Clone, Deserialize)]
pub struct DecisionRequest {
    pub pawns: Vec<PawnRef>,
    pub state: String,
    pub questions: Value,
    pub mode: i32,
}

#[derive(Serialize)]
pub struct SystemOneRequest<'a> {
    pub model: &'a str,
    pub state: &'a str,
    pub questions: &'a Value,
}

#[derive(Debug, Deserialize)]
pub struct SystemOneResponse {
    #[serde(default)]
    pub answers: HashMap<String, Answer>,
    #[serde(default)]
    pub usage: Option<Usage>,
}

#[derive(Debug, Deserialize)]
pub struct Usage {
    pub input_tokens: Option<i64>,
}

#[derive(Debug, Default, Deserialize)]
pub struct Answer {
    pub choice: Option<String>,
    pub confidence: Option<f64>,
    #[serde(default)]
    pub probabilities: HashMap<String, f64>,
}

/// The one action the mod will try to apply, plus the numbers behind it.
#[derive(Debug, Clone)]
pub struct Decision {
    pub chosen: String,
    pub confidence: f64,
    pub probabilities: Vec<(String, f64)>,
    pub mode: i32,
    pub options: usize,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_colony_request() {
        let json = r#"{
            "pawns":[{"id":1,"label":"A"},{"id":2,"label":"B"}],
            "state":"{\"pawns\":[]}",
            "questions":{"p1_action":{"type":"choice","instructions":"?","criteria":{"eat":"Eat"}}},
            "mode":1
        }"#;
        let request: DecisionRequest = serde_json::from_str(json).expect("a request");
        assert_eq!(request.pawns.len(), 2);
        assert_eq!(request.pawns[1].id, 2);
        assert_eq!(request.mode, 1);
        assert!(request.questions.get("p1_action").is_some());
    }

    #[test]
    fn parses_usage_tokens() {
        let json = r#"{"answers":{},"usage":{"input_tokens":1840,"output_tokens":0}}"#;
        let response: SystemOneResponse = serde_json::from_str(json).expect("a response");
        assert_eq!(
            response.usage.and_then(|usage| usage.input_tokens),
            Some(1840)
        );
    }
}
