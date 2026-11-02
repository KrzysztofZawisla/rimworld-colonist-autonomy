using System;
using System.Collections.Generic;
using Verse;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// Settings the Rust side pushes into the companion. The companion cannot read the mod's
    /// options file itself, so Rust calls <see cref="ClefBridge.Configure"/> on every change.
    /// </summary>
    internal static class ClefRuntime
    {
        /// <summary>Wire values shared with Rust; see <c>Mode::as_i32</c>.</summary>
        public const int ModeIntent = 0;
        public const int ModeAction = 1;
        public const int ModeIntentWithTarget = 2;
        public const int ModeHybrid = 3;

        public sealed class DecisionRecord
        {
            public string Choice;
            public float Confidence;
            public string Outcome;
            public int IdleStreak;
            public int Tick;
        }

        public static bool Ready;
        public static bool Enabled = true;
        public static int Mode = ModeAction;
        public static int RefreshSeconds = 30;
        public static float MinConfidence = 0.40f;
        public static float InterruptConfidence = 0.60f;
        public static bool DryRun;
        public static bool Debug;

        private static readonly Dictionary<long, int> LastBuildTick = new Dictionary<long, int>();
        private static readonly Dictionary<long, List<DecisionRecord>> RecentDecisions =
            new Dictionary<long, List<DecisionRecord>>();
        private static readonly HashSet<string> LoggedErrors = new HashSet<string>();

        public static void ResetDecisions()
        {
            RecentDecisions.Clear();
        }

        public static void RecordDecision(long pawnId, string choice, float confidence, string outcome)
        {
            List<DecisionRecord> records;
            if (!RecentDecisions.TryGetValue(pawnId, out records))
            {
                records = new List<DecisionRecord>();
                RecentDecisions[pawnId] = records;
            }
            int idleStreak = 0;
            if (outcome == "idle")
            {
                idleStreak = records.Count > 0 ? records[0].IdleStreak + 1 : 1;
            }
            records.Insert(0, new DecisionRecord
            {
                Choice = string.IsNullOrEmpty(choice) ? "?" : choice,
                Confidence = confidence,
                Outcome = outcome,
                IdleStreak = idleStreak,
                Tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0
            });
            if (records.Count > 5)
            {
                records.RemoveAt(5);
            }
        }

        /// <summary>Choices that failed within the last thirty seconds.</summary>
        public static HashSet<string> RecentlyFailedChoices(long pawnId)
        {
            var failed = new HashSet<string>();
            List<DecisionRecord> records;
            if (!RecentDecisions.TryGetValue(pawnId, out records))
            {
                return failed;
            }
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            foreach (DecisionRecord record in records)
            {
                if (record.Outcome != "stale" && record.Outcome != "refused")
                {
                    continue;
                }
                if (record.Tick > 0 && now - record.Tick > 1800)
                {
                    continue;
                }
                failed.Add(record.Choice);
            }
            return failed;
        }

        public static Dictionary<string, object> DescribeDecision(long pawnId)
        {
            List<DecisionRecord> records;
            if (!RecentDecisions.TryGetValue(pawnId, out records) || records.Count == 0)
            {
                return null;
            }
            DecisionRecord last = records[0];
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            var failed = new List<string>();
            foreach (DecisionRecord record in records)
            {
                if ((record.Outcome == "stale" || record.Outcome == "refused")
                    && (record.Tick <= 0 || now - record.Tick <= 1800))
                {
                    failed.Add(record.Choice);
                }
                if (failed.Count >= 3)
                {
                    break;
                }
            }
            return new Dictionary<string, object>
            {
                ["last"] = last.Choice,
                ["confidence"] = Math.Round(last.Confidence, 2),
                ["outcome"] = last.Outcome,
                ["idle_streak"] = last.IdleStreak,
                ["ticks_ago"] = last.Tick > 0 ? now - last.Tick : null,
                ["failed"] = failed
            };
        }

        /// <summary>True once per distinct error message, so a broken scanner says why.</summary>
        public static bool ShouldLogError(string message)
        {
            return LoggedErrors.Add(message);
        }

        /// <summary>True when this pawn has not had a state built within the refresh window.</summary>
        public static bool BuildDue(long pawnId)
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int last;
            if (LastBuildTick.TryGetValue(pawnId, out last) && now - last < RefreshSeconds * 60)
            {
                return false;
            }
            LastBuildTick[pawnId] = now;
            return true;
        }
    }
}
