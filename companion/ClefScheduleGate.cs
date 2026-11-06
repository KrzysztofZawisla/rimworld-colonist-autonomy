using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// Refuses a work job that cannot be reached and finished before the colonist's next Sleep
    /// block. Rust installs this as a Harmony prefix on Pawn_JobTracker.StartJob, so the job never
    /// starts and no reservations are taken.
    /// </summary>
    public static class ClefScheduleGate
    {
        private const int HourTicks = 2500;
        private const int CacheTicks = 300;
        private const int GateTicks = 600;
        private const float TravelSafety = 1.35f;
        private const float BudgetUse = 0.90f;

        private static readonly HashSet<string> NeverGate = new HashSet<string>
        {
            "Firefighter",
            "Patient",
            "Doctor",
        };

        private struct Cached
        {
            public int Signature;
            public int Tick;
            public bool Allow;
        }

        private static readonly Dictionary<int, Cached> Cache = new Dictionary<int, Cached>();
        private static readonly Dictionary<int, int> Gated = new Dictionary<int, int>();

        public static void Reset()
        {
            Cache.Clear();
            Gated.Clear();
        }

        private static readonly FieldInfo PawnField = typeof(Pawn_JobTracker).GetField(
            "pawn",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static bool Allow(Pawn_JobTracker tracker, Job job)
        {
            try
            {
                Pawn pawn = tracker != null && PawnField != null ? PawnField.GetValue(tracker) as Pawn : null;
                bool allow = Allows(pawn, job);
                if (pawn != null)
                {
                    if (allow)
                    {
                        Clear(pawn);
                    }
                    else
                    {
                        Mark(pawn);
                    }
                }
                return allow;
            }
            catch (Exception error)
            {
                if (ClefRuntime.Debug)
                {
                    Log.Warning("[ColonistAutonomy] schedule gate: " + error);
                }
                return true;
            }
        }

        public static bool IsGated(Pawn pawn)
        {
            if (pawn == null || Gated.Count == 0 || Find.TickManager == null)
            {
                return false;
            }
            int until;
            return Gated.TryGetValue(pawn.thingIDNumber, out until)
                && Find.TickManager.TicksGame < until;
        }

        public static void Mark(Pawn pawn)
        {
            if (pawn == null || Find.TickManager == null)
            {
                return;
            }
            Gated[pawn.thingIDNumber] = Find.TickManager.TicksGame + GateTicks;
        }

        public static void Clear(Pawn pawn)
        {
            if (pawn != null)
            {
                Gated.Remove(pawn.thingIDNumber);
            }
        }

        /// <summary>Same decision as the StartJob prefix, but without marking or caching.</summary>
        public static bool AllowsJob(Pawn pawn, Job job)
        {
            return Allows(pawn, job);
        }

        /// <summary>Cheap schedule check for candidate generation (travel + work fits before sleep).</summary>
        public static bool FitsSchedule(
            Pawn pawn,
            LocalTargetInfo targetA,
            LocalTargetInfo targetB,
            string workType)
        {
            if (!ClefRuntime.Ready || !ClefRuntime.Enabled || !ClefRuntime.RespectSchedule)
            {
                return true;
            }
            if (pawn == null || !pawn.Spawned || pawn.Map == null)
            {
                return true;
            }
            if (!pawn.IsColonistPlayerControlled || pawn.Dead || pawn.Downed || pawn.InMentalState
                || pawn.Drafted)
            {
                return true;
            }
            int budget = TicksUntilSleep(pawn);
            if (budget <= 0 || budget == int.MaxValue)
            {
                return true;
            }
            float speed = Math.Max(0.5f, pawn.GetStatValue(StatDefOf.MoveSpeed, true));
            int travel = Math.Max(TravelTicks(pawn, targetA, speed), TravelTicks(pawn, targetB, speed));
            return travel + WorkTicks(workType) <= budget * BudgetUse;
        }

        private static bool Allows(Pawn pawn, Job job)
        {
            if (!ClefRuntime.Ready || !ClefRuntime.Enabled || !ClefRuntime.RespectSchedule)
            {
                return true;
            }
            if (pawn == null || job == null || !pawn.Spawned || pawn.Map == null)
            {
                return true;
            }
            if (!pawn.IsColonistPlayerControlled || pawn.Dead || pawn.Downed || pawn.InMentalState
                || pawn.Drafted)
            {
                return true;
            }
            if (job.playerForced || job.def == null)
            {
                return true;
            }

            WorkGiverDef giver = job.workGiverDef;
            if (giver == null || giver.workType == null || NeverGate.Contains(giver.workType.defName))
            {
                return true;
            }

            int budget = TicksUntilSleep(pawn);
            if (budget <= 0 || budget == int.MaxValue)
            {
                return true;
            }

            int signature = Signature(job, giver);
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int pawnId = pawn.thingIDNumber;
            Cached cached;
            if (Cache.TryGetValue(pawnId, out cached) && cached.Signature == signature
                && now >= cached.Tick && now - cached.Tick < CacheTicks)
            {
                return cached.Allow;
            }

            int required;
            bool allow = Fits(pawn, job, giver, budget, out required);
            Cache[pawnId] = new Cached { Signature = signature, Tick = now, Allow = allow };
            if (ClefRuntime.Debug)
            {
                Log.Message("[ColonistAutonomy] schedule gate: " + pawn.LabelShort + " " + job.def.defName
                    + (allow ? " allowed (" : " refused (") + required + " vs " + budget + " ticks)");
            }
            return allow;
        }

        private static bool Fits(Pawn pawn, Job job, WorkGiverDef giver, int budget, out int required)
        {
            float speed = Math.Max(0.5f, pawn.GetStatValue(StatDefOf.MoveSpeed, true));
            int travel = Math.Max(TravelTicks(pawn, job.targetA, speed), TravelTicks(pawn, job.targetB, speed));
            required = travel + WorkTicks(giver.workType.defName);
            return required <= budget * BudgetUse;
        }

        private static int TravelTicks(Pawn pawn, LocalTargetInfo target, float speed)
        {
            if (!target.IsValid)
            {
                return 0;
            }
            IntVec3 cell = target.Cell;
            if (!cell.IsValid)
            {
                return 0;
            }
            float cells = pawn.Position.DistanceTo(cell);
            return (int)Math.Ceiling(cells / speed * 60f * TravelSafety);
        }

        private static int WorkTicks(string workType)
        {
            switch (workType)
            {
                case "Hauling":
                    return 240;
                case "Construction":
                case "Mining":
                    return 1200;
                case "PlantCutting":
                case "Growing":
                case "Crafting":
                case "Smithing":
                case "Tailoring":
                case "Art":
                    return 900;
                case "Cooking":
                case "Repair":
                    return 600;
                case "Research":
                case "Cleaning":
                    return 300;
                default:
                    return 600;
            }
        }

        private static int TicksUntilSleep(Pawn pawn)
        {
            if (pawn.timetable == null || pawn.Map == null || Find.TickManager == null)
            {
                return int.MaxValue;
            }
            int hour = GenLocalDate.HourOfDay(pawn.Map);
            if (pawn.timetable.GetAssignment(hour) == TimeAssignmentDefOf.Sleep)
            {
                return int.MaxValue;
            }
            int intoHour = Find.TickManager.TicksGame % HourTicks;
            for (int step = 1; step <= 24; step++)
            {
                int next = (hour + step) % 24;
                if (pawn.timetable.GetAssignment(next) != TimeAssignmentDefOf.Sleep)
                {
                    continue;
                }
                return step * HourTicks - intoHour;
            }
            return int.MaxValue;
        }

        private static int Signature(Job job, WorkGiverDef giver)
        {
            unchecked
            {
                int hash = job.def.defName.GetHashCode();
                hash = hash * 397 ^ job.targetA.GetHashCode();
                hash = hash * 397 ^ job.targetB.GetHashCode();
                hash = hash * 397 ^ giver.defName.GetHashCode();
                return hash;
            }
        }
    }
}
