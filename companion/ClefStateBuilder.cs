using System;
using System.Collections.Generic;
using RustBridge;
using Verse;

namespace SmartColonistsCompanion
{
    internal static class ClefStateBuilder
    {
        /// <summary>
        /// Builds one colony request: one state containing every eligible colonist, with questions
        /// keyed as <c>p&lt;id&gt;_&lt;question&gt;</c> so one model call answers the whole colony.
        /// </summary>
        public static Dictionary<string, object> BuildColony(List<Pawn> candidates, bool respectCooldown)
        {
            var eligible = new List<Pawn>();
            var signals = new List<ClefSignalContext>();
            var questions = new Dictionary<string, object>();
            var pawns = new List<Dictionary<string, object>>();
            ClefColonySignals colony;
            try
            {
                colony = ClefColonySignals.Build(candidates.Count > 0 ? candidates[0].Map : null);
            }
            catch (Exception error)
            {
                colony = new ClefColonySignals();
                Warn("colony signals: " + error.GetType().Name + ": " + error.Message);
            }

            foreach (Pawn pawn in candidates)
            {
                if (!ClefEligibility.CanDecideEvenIfBusy(pawn))
                {
                    continue;
                }
                bool due = ClefRuntime.BuildDue(pawn.thingIDNumber);
                if (respectCooldown && !due)
                {
                    continue;
                }

                ClefSignalContext pawnSignals;
                try
                {
                    pawnSignals = ClefSignals.Build(pawn, colony);
                }
                catch (Exception error)
                {
                    pawnSignals = new ClefSignalContext();
                    Warn("pawn signals for " + pawn.LabelShort + ": "
                        + error.GetType().Name + ": " + error.Message);
                }

                ClefCandidatePools pools;
                Dictionary<string, object> perPawn;
                try
                {
                    pools = ClefCandidates.Build(
                        pawn, ClefRuntime.Mode != ClefRuntime.ModeIntent, pawnSignals, colony);
                    perPawn = ClefQuestions.Build(pawn, pools);
                }
                catch (Exception error)
                {
                    Warn("candidates for " + pawn.LabelShort + ": "
                        + error.GetType().Name + ": " + error.Message);
                    continue;
                }
                if (perPawn == null)
                {
                    continue;
                }

                string prefix = "p" + pawn.thingIDNumber;
                int optionCount = 0;
                foreach (KeyValuePair<string, object> entry in perPawn)
                {
                    questions[prefix + "_" + entry.Key] = entry.Value;
                    var question = entry.Value as Dictionary<string, object>;
                    object criteriaValue;
                    if (question == null || !question.TryGetValue("criteria", out criteriaValue))
                    {
                        continue;
                    }
                    var criteria = criteriaValue as Dictionary<string, string>;
                    if (criteria != null && criteria.Count > optionCount)
                    {
                        optionCount = criteria.Count;
                    }
                }
                eligible.Add(pawn);
                signals.Add(pawnSignals);
                pawns.Add(new Dictionary<string, object>
                {
                    ["id"] = pawn.thingIDNumber,
                    ["label"] = pawn.LabelShort,
                    ["options"] = optionCount
                });
            }

            if (eligible.Count == 0)
            {
                return null;
            }

            string state = ClefState.BuildColony(eligible, signals, colony);
            if (ClefRuntime.Debug)
            {
                Log.Message("[ColonistAutonomy] colony: " + eligible.Count + " pawn(s), "
                    + questions.Count + " question(s), state " + state.Length + " chars");
                Log.Message("[ColonistAutonomy] questions: "
                    + System.Text.Json.JsonSerializer.Serialize(questions, ClefJson.Options));
                Log.Message("[ColonistAutonomy] state: " + state);
            }

            return new Dictionary<string, object>
            {
                ["pawns"] = pawns,
                ["state"] = state,
                ["questions"] = questions,
                ["mode"] = ClefRuntime.Mode
            };
        }

        private static void Warn(string message)
        {
            if (ClefRuntime.Debug || ClefRuntime.ShouldLogError(message))
            {
                Log.Warning("[ColonistAutonomy] " + message);
            }
        }

        /// <summary>The think node's path: ask Rust to build a decision for this pawn.</summary>
        public static void BuildAndSend(Pawn pawn)
        {
            Dictionary<string, object> payload = BuildColony(new List<Pawn> { pawn }, true);
            if (payload != null)
            {
                Companion.Send("ColonistAutonomy", "clef.state", payload);
            }
        }
    }
}
