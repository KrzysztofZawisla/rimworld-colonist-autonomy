//! Turns a systemone answer into the one option the mod should apply.

use crate::clef::types::{Answer, Decision, PawnRef, SystemOneResponse};

/// The decision shapes the player can switch between.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Mode {
    /// One intent; the target comes from the vanilla game.
    Intent,
    /// One flat list of concrete actions.
    Action,
    /// An intent plus a target question, all in one request.
    IntentWithTarget,
    /// Intent when there is no concrete target, Action when there is one.
    Hybrid,
}

impl Mode {
    pub fn from_i32(value: i32) -> Self {
        match value {
            0 => Self::Intent,
            2 => Self::IntentWithTarget,
            3 => Self::Hybrid,
            _ => Self::Action,
        }
    }

    pub fn as_i32(self) -> i32 {
        match self {
            Self::Intent => 0,
            Self::Action => 1,
            Self::IntentWithTarget => 2,
            Self::Hybrid => 3,
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Intent => "intent",
            Self::Action => "action",
            Self::IntentWithTarget => "intent + target",
            Self::Hybrid => "hybrid",
        }
    }

    pub fn description(self) -> &'static str {
        match self {
            Self::Intent => "The model picks the goal; the game picks the exact target.",
            Self::Action => "The model picks the job and its target; needs use the game's target.",
            Self::IntentWithTarget => "The model picks the goal, then the target inside it.",
            Self::Hybrid => {
                "The model picks the goal when there is no target, or the action when there is one."
            }
        }
    }
}

/// The kind of action a model choice belongs to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ActionKind {
    Work,
    Social,
    Need,
    Drug,
    Threat,
    Idle,
    Other,
}

impl ActionKind {
    pub fn of(chosen: &str) -> Self {
        if chosen.starts_with("work:") {
            Self::Work
        } else if chosen.starts_with("social:") {
            Self::Social
        } else if matches!(chosen, "eat" | "sleep" | "joy") {
            Self::Need
        } else if chosen.starts_with("drug:") {
            Self::Drug
        } else if matches!(chosen, "flee" | "hide") {
            Self::Threat
        } else if chosen == "idle" {
            Self::Idle
        } else {
            Self::Other
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Work => "work",
            Self::Social => "social",
            Self::Need => "need",
            Self::Drug => "drug",
            Self::Threat => "threat",
            Self::Idle => "idle",
            Self::Other => "other",
        }
    }
}

/// One pawn's slice of a colony response.
pub struct PawnDecision {
    pub id: i64,
    pub label: String,
    pub result: Result<Decision, String>,
}

/// Extracts one decision per pawn from the single colony response. Each pawn's questions are
/// prefixed with `p<id>_` by the companion.
pub fn decide_colony(
    response: &SystemOneResponse,
    mode: i32,
    pawns: &[PawnRef],
) -> Vec<PawnDecision> {
    pawns
        .iter()
        .map(|pawn| {
            let mut result = decide_for(response, mode, &format!("p{}", pawn.id));
            if let Ok(decision) = &mut result {
                if pawn.options > 0 {
                    decision.options = pawn.options;
                }
            }
            PawnDecision {
                id: pawn.id,
                label: pawn.label.clone(),
                result,
            }
        })
        .collect()
}

/// Selects one pawn's answer. `min_confidence` is enforced by the caller.
pub fn decide_for(
    response: &SystemOneResponse,
    mode: i32,
    prefix: &str,
) -> Result<Decision, String> {
    let key = |name: &str| format!("{prefix}_{name}");
    match Mode::from_i32(mode) {
        Mode::Intent => {
            let answer = answer(response, &key("intent"))?;
            let chosen = choice(answer)?;
            Ok(decision(chosen, answer, 0))
        }
        Mode::Action => {
            let answer = answer(response, &key("action"))?;
            let chosen = choice(answer)?;
            Ok(decision(chosen, answer, 1))
        }
        Mode::IntentWithTarget => intent_with_target(response, &key, 2),
        Mode::Hybrid => {
            if let Ok(action_answer) = answer(response, &key("action")) {
                if let Ok(chosen) = choice(action_answer) {
                    return Ok(decision(chosen, action_answer, 3));
                }
            }
            intent_with_target(response, &key, 3)
        }
    }
}

fn intent_with_target(
    response: &SystemOneResponse,
    key: &impl Fn(&str) -> String,
    mode: i32,
) -> Result<Decision, String> {
    let intent_answer = answer(response, &key("intent"))?;
    let intent = choice(intent_answer)?;
    let target_key = match intent.as_str() {
        "work" => Some(key("work_target")),
        "social" => Some(key("social_target")),
        "flee" => Some(key("threat_target")),
        _ => None,
    };
    if let Some(target_key) = target_key {
        if let Ok(target_answer) = answer(response, &target_key) {
            if let Ok(target) = choice(target_answer) {
                return Ok(decision(target, target_answer, mode));
            }
        }
    }
    Ok(decision(intent, intent_answer, mode))
}

fn answer<'a>(response: &'a SystemOneResponse, key: &str) -> Result<&'a Answer, String> {
    response
        .answers
        .get(key)
        .ok_or_else(|| format!("the model did not answer '{key}'"))
}

fn choice(answer: &Answer) -> Result<String, String> {
    answer
        .choice
        .clone()
        .filter(|value| !value.is_empty())
        .ok_or_else(|| "the model returned an empty choice".to_string())
}

fn decision(chosen: String, answer: &Answer, mode: i32) -> Decision {
    let options = answer.probabilities.len();
    let mut probabilities: Vec<(String, f64)> = answer
        .probabilities
        .iter()
        .map(|(key, value)| (key.clone(), *value))
        .collect();
    probabilities.sort_by(|left, right| {
        right
            .1
            .partial_cmp(&left.1)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    probabilities.truncate(5);
    Decision {
        chosen,
        confidence: answer.confidence.unwrap_or(0.0),
        probabilities,
        mode,
        options,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn response(json: &str) -> SystemOneResponse {
        serde_json::from_str(json).expect("test response")
    }

    #[test]
    fn mode_values_round_trip() {
        for mode in [
            Mode::Intent,
            Mode::Action,
            Mode::IntentWithTarget,
            Mode::Hybrid,
        ] {
            assert_eq!(Mode::from_i32(mode.as_i32()), mode);
        }
    }

    #[test]
    fn drug_options_have_their_own_kind() {
        assert_eq!(ActionKind::of("drug:123"), ActionKind::Drug);
        assert_eq!(ActionKind::Drug.label(), "drug");
    }

    #[test]
    fn hybrid_mode_uses_the_action_when_the_model_answered_one() {
        let parsed = response(
            r#"{"answers":{
                "p1_action":{"choice":"work:HaulGeneral:1","confidence":0.7,"probabilities":{"work:HaulGeneral:1":0.7}},
                "p1_intent":{"choice":"work","confidence":0.5,"probabilities":{"work":0.5}}}}"#,
        );
        let decision = decide_for(&parsed, 3, "p1").expect("a decision");
        assert_eq!(decision.chosen, "work:HaulGeneral:1");
        assert_eq!(decision.mode, 3);
    }

    #[test]
    fn hybrid_mode_falls_back_to_the_intent_target() {
        let parsed = response(
            r#"{"answers":{
                "p1_intent":{"choice":"work","confidence":0.6,"probabilities":{"work":0.6}},
                "p1_work_target":{"choice":"work:DoBillsCook:9","confidence":0.8,
                    "probabilities":{"work:DoBillsCook:9":0.8}}}}"#,
        );
        let decision = decide_for(&parsed, 3, "p1").expect("a decision");
        assert_eq!(decision.chosen, "work:DoBillsCook:9");
        assert_eq!(decision.mode, 3);
    }

    #[test]
    fn all_modes_pick_their_expected_answer_from_the_same_response() {
        let parsed = response(
            r#"{"answers":{
                "p1_intent":{"choice":"work","confidence":0.6,"probabilities":{"work":0.6}},
                "p1_work_target":{"choice":"work:DoBillsCook:9","confidence":0.8,
                    "probabilities":{"work:DoBillsCook:9":0.8}},
                "p1_action":{"choice":"social:2","confidence":0.7,
                    "probabilities":{"social:2":0.7}}}}"#,
        );
        assert_eq!(decide_for(&parsed, 0, "p1").expect("intent").chosen, "work");
        assert_eq!(
            decide_for(&parsed, 1, "p1").expect("action").chosen,
            "social:2"
        );
        assert_eq!(
            decide_for(&parsed, 2, "p1")
                .expect("intent + target")
                .chosen,
            "work:DoBillsCook:9"
        );
        assert_eq!(
            decide_for(&parsed, 3, "p1").expect("hybrid").chosen,
            "social:2"
        );
    }

    #[test]
    fn hybrid_mode_falls_back_to_the_intent_without_a_target() {
        let parsed = response(
            r#"{"answers":{"p1_intent":{"choice":"idle","confidence":0.5,
                "probabilities":{"idle":0.5}}}}"#,
        );
        let decision = decide_for(&parsed, 3, "p1").expect("a decision");
        assert_eq!(decision.chosen, "idle");
        assert_eq!(decision.mode, 3);
    }

    #[test]
    fn action_mode_uses_the_action_choice() {
        let parsed = response(
            r#"{"answers":{"p1_action":{"type":"choice","choice":"work:HaulGeneral:1","confidence":0.7,
                "probabilities":{"work:HaulGeneral:1":0.7,"eat":0.3}}}}"#,
        );
        let decision = decide_for(&parsed, 1, "p1").expect("a decision");
        assert_eq!(decision.chosen, "work:HaulGeneral:1");
        assert!((decision.confidence - 0.7).abs() < 1e-9);
        assert_eq!(decision.mode, 1);
        assert_eq!(decision.options, 2);
        assert_eq!(decision.probabilities[0].0, "work:HaulGeneral:1");
        assert_eq!(decision.probabilities[0].1, 0.7);
    }

    #[test]
    fn intent_with_target_mode_prefers_the_chosen_intent_target() {
        let parsed = response(
            r#"{"answers":{
                "p1_intent":{"choice":"work","confidence":0.6,"probabilities":{"work":0.6}},
                "p1_work_target":{"choice":"work:DoBillsCook:9","confidence":0.8,
                    "probabilities":{"work:DoBillsCook:9":0.8}}}}"#,
        );
        let decision = decide_for(&parsed, 2, "p1").expect("a decision");
        assert_eq!(decision.chosen, "work:DoBillsCook:9");
        assert!((decision.confidence - 0.8).abs() < 1e-9);
        assert_eq!(decision.mode, 2);
    }

    #[test]
    fn intent_with_target_mode_falls_back_to_the_intent_without_a_target() {
        let parsed = response(
            r#"{"answers":{"p1_intent":{"choice":"eat","confidence":0.5,
                "probabilities":{"eat":0.5}}}}"#,
        );
        let decision = decide_for(&parsed, 2, "p1").expect("a decision");
        assert_eq!(decision.chosen, "eat");
        assert_eq!(decision.mode, 2);
    }

    #[test]
    fn intent_mode_uses_the_intent() {
        let parsed = response(
            r#"{"answers":{"p1_intent":{"choice":"work:Hauling","confidence":0.4,
                "probabilities":{"work:Hauling":0.4}}}}"#,
        );
        let decision = decide_for(&parsed, 0, "p1").expect("a decision");
        assert_eq!(decision.chosen, "work:Hauling");
        assert_eq!(decision.mode, 0);
    }

    #[test]
    fn a_missing_answer_is_an_error() {
        let parsed = response(r#"{"answers":{}}"#);
        assert!(decide_for(&parsed, 1, "p1").is_err());
    }

    #[test]
    fn an_empty_choice_is_an_error() {
        let parsed = response(r#"{"answers":{"p1_action":{"choice":"","confidence":0.5}}}"#);
        assert!(decide_for(&parsed, 1, "p1").is_err());
    }

    #[test]
    fn colony_decisions_are_grouped_per_pawn() {
        let parsed = response(
            r#"{"answers":{
                "p1_action":{"choice":"eat","confidence":0.6,"probabilities":{"eat":0.6}},
                "p2_action":{"choice":"idle","confidence":0.8,"probabilities":{"idle":0.8}}}}"#,
        );
        let pawns = vec![
            PawnRef {
                id: 1,
                label: "A".to_string(),
                options: 2,
            },
            PawnRef {
                id: 2,
                label: "B".to_string(),
                options: 2,
            },
        ];
        let decisions = decide_colony(&parsed, 1, &pawns);
        assert_eq!(decisions.len(), 2);
        assert_eq!(decisions[0].result.as_ref().expect("p1").chosen, "eat");
        assert_eq!(decisions[1].result.as_ref().expect("p2").chosen, "idle");
    }
}
