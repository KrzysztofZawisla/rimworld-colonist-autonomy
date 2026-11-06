using System;
using System.Collections.Generic;
using System.Text.Json;
using RimWorld;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// The static entry points Rust calls through `companion_call`. Everything here runs on the
    /// game's main thread; no method performs I/O or waits for the model.
    /// </summary>
    public static class ClefBridge
    {
        public static string Init()
        {
            ClefDefs.Validate();
            ClefRuntime.Ready = true;
            ClefRuntime.ResetDecisions();
            ClefLifecycle.Install();
            return "ready";
        }

        public static string Configure(
            bool enabled,
            int mode,
            int refreshSeconds,
            float minConfidence,
            bool dryRun,
            bool debug,
            float interruptConfidence,
            bool respectSchedule)
        {
            ClefRuntime.Enabled = enabled;
            ClefRuntime.Mode = Math.Max(0, Math.Min(3, mode));
            ClefRuntime.RefreshSeconds = Math.Max(5, Math.Min(300, refreshSeconds));
            ClefRuntime.MinConfidence = Math.Max(0f, Math.Min(1f, minConfidence));
            ClefRuntime.InterruptConfidence = Math.Max(0f, Math.Min(1f, interruptConfidence));
            ClefRuntime.DryRun = dryRun;
            ClefRuntime.Debug = debug;
            ClefRuntime.RespectSchedule = respectSchedule;
            return "configured";
        }

        public static string ResetGate()
        {
            ClefScheduleGate.Reset();
            return "reset";
        }

        /// <summary>Builds a payload for every eligible colonist whose refresh window elapsed.</summary>
        public static string CollectStates()
        {
            if (!ClefRuntime.Ready || !ClefRuntime.Enabled || Find.Maps == null)
            {
                return "null";
            }

            try
            {
                var colonists = new List<Pawn>();
                // A pocket map or a spawned pawn can change these live lists while we walk them
                // (the API self-test generates maps); snapshot them first.
                foreach (Map map in new List<Map>(Find.Maps))
                {
                    if (map == null || !map.IsPlayerHome || map.mapPawns == null)
                    {
                        continue;
                    }
                    foreach (Pawn pawn in new List<Pawn>(map.mapPawns.FreeColonistsSpawned))
                    {
                        colonists.Add(pawn);
                    }
                }

                Dictionary<string, object> request = ClefStateBuilder.BuildColony(colonists, false);
                return request == null
                    ? "null"
                    : JsonSerializer.Serialize(request, ClefJson.Options);
            }
            catch (Exception error)
            {
                string key = error.GetType().Name + ": " + error.Message;
                if (ClefRuntime.Debug || ClefRuntime.ShouldLogError(key))
                {
                    Log.Error("[ColonistAutonomy] CollectStates failed: " + error);
                }
                return "null";
            }
        }

        /// <summary>Builds and applies one chosen option; never blocks on the model.</summary>
        public static string ApplyDecision(long pawnId, string decisionJson, bool interrupt)
        {
            if (!ClefRuntime.Ready || !ClefRuntime.Enabled)
            {
                return "not-ready";
            }

            string chosen = ClefDecision.Chosen(decisionJson);
            float confidence = ClefDecision.Confidence(decisionJson);

            Pawn pawn = ClefEligibility.FindPawn(pawnId);
            if (pawn == null)
            {
                ClefRuntime.RecordDecision(pawnId, chosen, confidence, "gone");
                return "gone";
            }
            if (!ClefEligibility.CanDecideEvenIfBusy(pawn))
            {
                ClefRuntime.RecordDecision(pawnId, chosen, confidence, "ineligible");
                if (ClefRuntime.Debug)
                {
                    Log.Message("[ColonistAutonomy] " + pawn.LabelShort + " -> ineligible");
                }
                return "ineligible";
            }
            if (ClefRuntime.DryRun)
            {
                ClefRuntime.RecordDecision(pawnId, chosen, confidence, "dry");
                return "dry";
            }

            if (string.IsNullOrEmpty(chosen) || chosen == "idle")
            {
                ClefRuntime.RecordDecision(pawnId, chosen, confidence, "idle");
                return "idle";
            }
            if (pawn.jobs == null)
            {
                ClefRuntime.RecordDecision(pawnId, chosen, confidence, "ineligible");
                return "ineligible";
            }
            if (pawn.jobs.curJob != null)
            {
                bool idleJob = ClefEligibility.IsIdleJob(pawn.jobs.curJob);
                if (!interrupt)
                {
                    ClefRuntime.RecordDecision(pawnId, chosen, confidence, "deferred");
                    if (ClefRuntime.Debug)
                    {
                        Log.Message("[ColonistAutonomy] " + pawn.LabelShort + " -> deferred (" + chosen + ")");
                    }
                    return "deferred";
                }
                if (!idleJob && confidence < ClefRuntime.InterruptConfidence)
                {
                    ClefRuntime.RecordDecision(pawnId, chosen, confidence, "deferred");
                    if (ClefRuntime.Debug)
                    {
                        Log.Message("[ColonistAutonomy] " + pawn.LabelShort
                            + " -> low confidence to interrupt (" + chosen + ")");
                    }
                    return "deferred";
                }
                if (!ClefEligibility.CanInterrupt(pawn))
                {
                    ClefRuntime.RecordDecision(pawnId, chosen, confidence, "busy");
                    if (ClefRuntime.Debug)
                    {
                        Log.Message("[ColonistAutonomy] " + pawn.LabelShort + " -> busy (" + chosen + ")");
                    }
                    return "busy";
                }
                if (!idleJob && !ClefEligibility.JobOldEnough(pawn))
                {
                    ClefRuntime.RecordDecision(pawnId, chosen, confidence, "deferred");
                    if (ClefRuntime.Debug)
                    {
                        Log.Message("[ColonistAutonomy] " + pawn.LabelShort
                            + " -> young job (" + chosen + ")");
                    }
                    return "deferred";
                }
            }

            Job job = ClefJobs.Build(pawn, chosen);
            if (job == null)
            {
                ClefRuntime.RecordDecision(pawnId, chosen, confidence, "stale");
                if (ClefRuntime.Debug)
                {
                    Log.Message("[ColonistAutonomy] " + pawn.LabelShort + " -> stale (" + chosen + ")");
                }
                return "stale";
            }
            if (!pawn.jobs.TryTakeOrderedJob(job, null, false))
            {
                ClefRuntime.RecordDecision(pawnId, chosen, confidence, "refused");
                Log.Warning("[ColonistAutonomy] " + pawn.LabelShort + " refused " + job.def.defName);
                return "refused";
            }

            ClefRuntime.RecordDecision(pawnId, chosen, confidence, "applied");
            string report = job.GetReport(pawn);
            Log.Message("[ColonistAutonomy] applied " + job.def.defName + " to " + pawn.LabelShort
                + (string.IsNullOrEmpty(report) ? string.Empty : " (" + report + ")"));
            return "applied";
        }
    }

    internal static class ClefDecision
    {
        /// <summary>Reads the `chosen` field from the Rust decision JSON, or null.</summary>
        public static string Chosen(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement chosen;
                    return document.RootElement.TryGetProperty("chosen", out chosen)
                        ? chosen.GetString()
                        : null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Reads the confidence from the Rust decision JSON, or zero.</summary>
        public static float Confidence(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return 0f;
            }
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement confidence;
                    return document.RootElement.TryGetProperty("confidence", out confidence)
                        && confidence.TryGetDouble(out double value)
                        ? (float)value
                        : 0f;
                }
            }
            catch (JsonException)
            {
                return 0f;
            }
        }
    }
}
