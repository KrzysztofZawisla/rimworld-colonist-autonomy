using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    internal static class ClefEligibility
    {
        /// <summary>The passive path: the pawn has no job, so vanilla has nothing to say.</summary>
        public static bool CanDecide(Pawn pawn)
        {
            return CanDecideEvenIfBusy(pawn) && pawn.jobs != null && pawn.jobs.curJob == null;
        }

        /// <summary>The active path may look at a working pawn, but never at a drafted or broken one.</summary>
        public static bool CanDecideEvenIfBusy(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || !pawn.IsColonist)
            {
                return false;
            }
            if (pawn.Drafted || pawn.Downed || pawn.InMentalState)
            {
                return false;
            }
            if (pawn.health == null || pawn.health.Dead)
            {
                return false;
            }
            Map map = pawn.Map;
            return map != null && map.IsPlayerHome && Find.TickManager != null;
        }

        /// <summary>Whether the active refresh may replace the current job.</summary>
        public static bool CanInterrupt(Pawn pawn)
        {
            Job job = pawn.jobs != null ? pawn.jobs.curJob : null;
            if (job == null)
            {
                return true;
            }
            if (job.playerForced)
            {
                return false;
            }
            string def = job.def != null ? job.def.defName : null;
            if (def == null)
            {
                return true;
            }
            switch (def)
            {
                case "Ingest":
                case "LayDown":
                case "LayDownResting":
                case "LayDownAwake":
                case "TendPatient":
                case "TendEntity":
                case "Flee":
                case "FleeAndCower":
                case "FleeAndCowerShort":
                case "Wait_Downed":
                    return false;
            }
            if (pawn.health != null && pawn.health.hediffSet != null && pawn.health.hediffSet.BleedRateTotal > 0.5f)
            {
                return false;
            }
            return true;
        }

        /// <summary>Wandering has no work to protect, so it is replaced without the work threshold.</summary>
        public static bool IsIdleJob(Job job)
        {
            string def = job != null && job.def != null ? job.def.defName : null;
            return def == "Wait_Wander"
                || def == "GotoWander"
                || def == "Wander"
                || def == "Wait"
                || def == "Idle";
        }

        /// <summary>Whether the current job has run long enough to be replaced without twitching.</summary>
        public static bool JobOldEnough(Pawn pawn)
        {
            Job job = pawn.jobs != null ? pawn.jobs.curJob : null;
            if (job == null || Find.TickManager == null)
            {
                return true;
            }
            return Find.TickManager.TicksGame - job.startTick >= 5 * 60;
        }

        public static Pawn FindPawn(long id)
        {
            foreach (Map map in new List<Map>(Find.Maps))
            {
                if (map == null || map.mapPawns == null)
                {
                    continue;
                }
                foreach (Pawn pawn in new List<Pawn>(map.mapPawns.AllPawnsSpawned))
                {
                    if (pawn != null && pawn.thingIDNumber == id)
                    {
                        return pawn;
                    }
                }
            }
            return null;
        }
    }
}
