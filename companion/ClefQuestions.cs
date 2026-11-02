using System.Collections.Generic;
using Verse;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// Builds the closed set of questions for the selected mode. The model never writes text;
    /// it only scores the option ids it gets here.
    /// </summary>
    internal static class ClefQuestions
    {
        public static Dictionary<string, object> Build(Pawn pawn, ClefCandidatePools pools)
        {
            switch (ClefRuntime.Mode)
            {
                case ClefRuntime.ModeIntent:
                    return BuildIntent(pawn, pools);
                case ClefRuntime.ModeIntentWithTarget:
                    return BuildIntentWithTarget(pawn, pools);
                case ClefRuntime.ModeHybrid:
                    return HasTargets(pools)
                        ? BuildAction(pawn, pools)
                        : BuildIntent(pawn, pools);
                default:
                    return BuildAction(pawn, pools);
            }
        }

        private static bool HasTargets(ClefCandidatePools pools)
        {
            if (pools.Social.Count > 0 || pools.Threat.Count > 0)
            {
                return true;
            }
            foreach (ClefOption option in pools.Work)
            {
                // work:<type> is a fallback where vanilla picks the target. Only a concrete
                // work:<WorkGiver>:<thing> or work:<WorkGiver>:cell:<x>,<z> counts as a target.
                if (option.Id.Split(':').Length >= 3)
                {
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<string, object> BuildIntent(Pawn pawn, ClefCandidatePools pools)
        {
            var criteria = new Dictionary<string, string>();
            Add(criteria, pools.Needs, 16);
            Add(criteria, pools.WorkTypes, 8);
            if (pools.Social.Count > 0)
            {
                criteria["social"] = "Socialize (" + pools.Social[0].Description + ")";
            }
            Add(criteria, pools.Threat, 2);
            Add(criteria, pools.Idle, 1);
            if (criteria.Count <= 1)
            {
                return null;
            }
            return new Dictionary<string, object>
            {
                ["intent"] = Question(
                    "You direct the RimWorld colonist " + pawn.LabelShort
                    + ". Choose one intention; the game will pick the exact target."
                    + " Prefer useful work or an urgent need over idling."
                    + " Use the history field to avoid repeating an action that failed."
                    + " If history.idle_streak is high, prefer offered work over idling."
                    + " Use the colony block: bills, zones, research, construction, designations and threats.",
                    criteria)
            };
        }

        private static Dictionary<string, object> BuildAction(Pawn pawn, ClefCandidatePools pools)
        {
            var criteria = new Dictionary<string, string>();
            Add(criteria, pools.Needs, 16);
            Add(criteria, pools.Work, 16);
            Add(criteria, pools.Social, 16);
            Add(criteria, pools.Threat, 16);
            Add(criteria, pools.Idle, 16);
            if (criteria.Count <= 1)
            {
                return null;
            }
            return new Dictionary<string, object>
            {
                ["action"] = Question(
                    "You direct the RimWorld colonist " + pawn.LabelShort
                    + ". Choose the single best action for the colony right now."
                    + " Prefer useful work or an urgent need over idling;"
                    + " choose idle only when nothing useful is available."
                    + " Use the history field to avoid repeating an action that failed."
                    + " If history.idle_streak is high, prefer offered work over idling."
                    + " Use the colony block: bills, zones, research, construction, designations and threats.",
                    criteria)
            };
        }

        private static Dictionary<string, object> BuildIntentWithTarget(Pawn pawn, ClefCandidatePools pools)
        {
            var intent = new Dictionary<string, string>();
            foreach (ClefOption option in pools.Needs)
            {
                intent[option.Id] = option.Description;
            }
            if (pools.Work.Count > 0)
            {
                string top = pools.WorkTypes.Count > 0
                    ? pools.WorkTypes[0].Description
                    : "work";
                intent["work"] = "Do work (" + top + ")";
            }
            if (pools.Social.Count > 0)
            {
                intent["social"] = "Socialize (" + pools.Social[0].Description + ")";
            }
            if (pools.Threat.Count > 0)
            {
                intent["flee"] = "Respond to danger (" + pools.Threat[0].Description + ")";
            }
            intent["idle"] = "Wander / do nothing";
            if (intent.Count <= 1)
            {
                return null;
            }

            var questions = new Dictionary<string, object>
            {
                ["intent"] = Question(
                    "You direct the RimWorld colonist " + pawn.LabelShort
                    + ". Choose one intention for the next few minutes."
                    + " Prefer useful work or an urgent need over idling."
                    + " Use the history field to avoid repeating an action that failed."
                    + " If history.idle_streak is high, prefer offered work over idling."
                    + " Use the colony block: bills, zones, research, construction, designations and threats.",
                    intent)
            };
            if (pools.Work.Count > 0)
            {
                questions["work_target"] = Question(
                    "Which work action should " + pawn.LabelShort + " do?",
                    Criteria(pools.Work, 16));
            }
            if (pools.Social.Count > 0)
            {
                questions["social_target"] = Question(
                    "Who should " + pawn.LabelShort + " spend time with?",
                    Criteria(pools.Social, 3));
            }
            if (pools.Threat.Count > 0)
            {
                questions["threat_target"] = Question(
                    "How should " + pawn.LabelShort + " respond to the danger?",
                    Criteria(pools.Threat, 2));
            }
            return questions;
        }

        private static Dictionary<string, object> Question(string instructions, Dictionary<string, string> criteria)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "choice",
                ["instructions"] = instructions,
                ["criteria"] = criteria
            };
        }

        private static Dictionary<string, string> Criteria(IEnumerable<ClefOption> options, int limit)
        {
            var criteria = new Dictionary<string, string>();
            Add(criteria, options, limit);
            return criteria;
        }

        private static void Add(Dictionary<string, string> criteria, IEnumerable<ClefOption> options, int limit)
        {
            foreach (ClefOption option in options)
            {
                if (criteria.Count >= limit)
                {
                    return;
                }
                if (!criteria.ContainsKey(option.Id))
                {
                    criteria[option.Id] = option.Description;
                }
            }
        }

    }
}
