using System.Collections.Generic;
using RustBridge;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// Inserted at the vanilla `Humanlike_PostMain` hook: the game reaches it only after the normal
    /// work/needs logic found nothing. It is the passive "what next?" path.
    /// </summary>
    public class ClefThinkNode : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!ClefRuntime.Ready || !ClefRuntime.Enabled || !ClefEligibility.CanDecide(pawn))
            {
                return null;
            }

            if (ClefRuntime.DryRun)
            {
                ClefStateBuilder.BuildAndSend(pawn);
                return null;
            }

            string answer = Companion.Request(
                "ColonistAutonomy",
                "clef.decision",
                new Dictionary<string, object>
                {
                    ["id"] = pawn.thingIDNumber,
                    ["consume"] = true
                });

            if (!string.IsNullOrEmpty(answer))
            {
                string chosen = ClefDecision.Chosen(answer);
                float confidence = ClefDecision.Confidence(answer);
                if (string.IsNullOrEmpty(chosen) || chosen == "idle")
                {
                    ClefRuntime.RecordDecision(pawn.thingIDNumber, chosen, confidence, "idle");
                    return null;
                }
                Job job = ClefJobs.Build(pawn, chosen);
                if (job != null)
                {
                    ClefRuntime.RecordDecision(pawn.thingIDNumber, chosen, confidence, "applied");
                    Log.Message("[ColonistAutonomy] think node: " + pawn.LabelShort + " -> " + job.def.defName
                        + " (" + chosen + ")");
                    return job;
                }
                ClefRuntime.RecordDecision(pawn.thingIDNumber, chosen, confidence, "stale");
            }

            ClefStateBuilder.BuildAndSend(pawn);
            return null;
        }
    }
}
