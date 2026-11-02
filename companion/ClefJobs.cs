using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// Turns the chosen option id into a real job. Every game name used here is compiled against
    /// the installed Assembly-CSharp, so a renamed member fails the build instead of the game.
    /// </summary>
    internal static class ClefJobs
    {
        private const int MaxThingsPerGiver = 8;
        private const int MaxFallbackThings = 800;
        private const int FallbackRadius = 16;
        private const int FallbackThingRadius = 30;

        private static readonly MethodInfo TryGiveJob =
            typeof(ThinkNode_JobGiver).GetMethod("TryGiveJob", BindingFlags.Instance | BindingFlags.NonPublic);

        public static Job Build(Pawn pawn, string optionId)
        {
            if (string.IsNullOrEmpty(optionId) || optionId == "idle")
            {
                return null;
            }

            string[] parts = optionId.Split(':');
            switch (parts[0])
            {
                case "eat":
                    return Giver<JobGiver_GetFood>(pawn);
                case "sleep":
                    return Giver<JobGiver_GetRest>(pawn);
                case "joy":
                    return Giver<JobGiver_GetJoy>(pawn);
                case "social":
                    return Social(pawn, parts.Length > 1 ? parts[1] : null);
                case "flee":
                    return Flee(pawn, false);
                case "hide":
                    return Flee(pawn, true);
                case "work":
                    return Work(pawn, parts);
                default:
                    return null;
            }
        }

        private static Job Giver<T>(Pawn pawn) where T : ThinkNode_JobGiver
        {
            if (pawn.thinker == null)
            {
                return null;
            }
            T node;
            try
            {
                node = pawn.thinker.TryGetMainTreeThinkNode<T>();
            }
            catch (Exception error)
            {
                LogOnce(error);
                return null;
            }
            if (node == null || TryGiveJob == null)
            {
                return null;
            }
            try
            {
                return TryGiveJob.Invoke(node, new object[] { pawn }) as Job;
            }
            catch (Exception error)
            {
                LogOnce(error);
                return null;
            }
        }

        private static Job Work(Pawn pawn, string[] parts)
        {
            if (parts.Length < 2)
            {
                return Fail("malformed work option");
            }

            if (parts.Length == 2)
            {
                WorkTypeDef type = DefDatabase<WorkTypeDef>.GetNamedSilentFail(parts[1]);
                if (type == null)
                {
                    return Fail("unknown work type " + parts[1]);
                }
                return FirstWorkJob(pawn, type);
            }

            WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(parts[1]);
            if (def == null)
            {
                return Fail("unknown WorkGiverDef " + parts[1]);
            }
            WorkGiver_Scanner scanner = def.Worker as WorkGiver_Scanner;
            if (scanner == null)
            {
                return Fail("not a scanner: " + def.defName);
            }
            if (SafeShouldSkip(scanner, pawn))
            {
                return Fail("ShouldSkip: " + def.defName);
            }

            if (parts.Length == 3)
            {
                long id;
                if (!long.TryParse(parts[2], out id))
                {
                    return Fail("bad thing id " + parts[2]);
                }
                Thing thing = FindThing(pawn.Map, id);
                if (thing == null || !thing.Spawned)
                {
                    return Fail("thing " + id + " is gone");
                }
                if (!SafeHasJobOnThing(scanner, pawn, thing))
                {
                    return Fail("HasJobOnThing false: " + def.defName + " " + id);
                }
                Job job = scanner.JobOnThing(pawn, thing, false);
                if (job == null)
                {
                    return Fail("JobOnThing null: " + def.defName + " " + id);
                }
                return job;
            }

            if (parts.Length >= 4 && parts[2] == "cell")
            {
                int x;
                int z;
                if (parts.Length == 4)
                {
                    string[] coordinates = parts[3].Split(',');
                    if (coordinates.Length != 2
                        || !int.TryParse(coordinates[0], out x)
                        || !int.TryParse(coordinates[1], out z))
                    {
                        return Fail("bad cell " + parts[3]);
                    }
                }
                else if (parts.Length == 5)
                {
                    if (!int.TryParse(parts[3], out x) || !int.TryParse(parts[4], out z))
                    {
                        return Fail("bad cell");
                    }
                }
                else
                {
                    return Fail("malformed cell option");
                }
                IntVec3 cell = new IntVec3(x, 0, z);
                if (!SafeHasJobOnCell(scanner, pawn, cell))
                {
                    return Fail("HasJobOnCell false: " + def.defName + " " + cell);
                }
                Job job = scanner.JobOnCell(pawn, cell, false);
                if (job == null)
                {
                    return Fail("JobOnCell null: " + def.defName + " " + cell);
                }
                return job;
            }

            return Fail("malformed work option");
        }

        private static Job Fail(string message)
        {
            if (ClefRuntime.Debug)
            {
                Log.Message("[ColonistAutonomy] job build: " + message);
            }
            return null;
        }

        private static Job FirstWorkJob(Pawn pawn, WorkTypeDef type)
        {
            foreach (WorkGiverDef def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                if (def.workType != type)
                {
                    continue;
                }
                WorkGiver_Scanner scanner = def.Worker as WorkGiver_Scanner;
                if (scanner == null || SafeShouldSkip(scanner, pawn))
                {
                    continue;
                }

                // A scanner may support things, cells, or both; the unsupported one returns null.
                foreach (Thing thing in Things(scanner, pawn, MaxThingsPerGiver))
                {
                    if (thing == null || !thing.Spawned || !SafeHasJobOnThing(scanner, pawn, thing))
                    {
                        continue;
                    }
                    Job job = scanner.JobOnThing(pawn, thing, false);
                    if (job != null)
                    {
                        return job;
                    }
                }

                foreach (IntVec3 cell in Cells(scanner, pawn, MaxThingsPerGiver))
                {
                    if (!cell.IsValid || !SafeHasJobOnCell(scanner, pawn, cell))
                    {
                        continue;
                    }
                    Job job = scanner.JobOnCell(pawn, cell, false);
                    if (job != null)
                    {
                        return job;
                    }
                }
            }
            return null;
        }

        private static Job Social(Pawn pawn, string partnerId)
        {
            if (ClefDefs.SocialJob == null)
            {
                return null;
            }
            Pawn partner;
            if (!string.IsNullOrEmpty(partnerId))
            {
                long id;
                if (!long.TryParse(partnerId, out id))
                {
                    return null;
                }
                partner = ClefEligibility.FindPawn(id);
                if (!IsValidSocialPartner(pawn, partner))
                {
                    return null;
                }
            }
            else
            {
                partner = BestPartner(pawn);
                if (!IsValidSocialPartner(pawn, partner))
                {
                    return null;
                }
            }
            return JobMaker.MakeJob(ClefDefs.SocialJob, new LocalTargetInfo(partner));
        }

        private static bool IsValidSocialPartner(Pawn pawn, Pawn partner)
        {
            // The model response is untrusted; never let it target non-colonists.
            return partner != null
                && partner != pawn
                && partner.Spawned
                && partner.Map == pawn.Map
                && IsFreeColonistOnMap(pawn, partner)
                && !partner.Downed
                && !partner.InMentalState
                && partner.health != null
                && !partner.health.Dead;
        }

        private static bool IsFreeColonistOnMap(Pawn pawn, Pawn partner)
        {
            if (pawn.Map == null || pawn.Map.mapPawns == null)
            {
                return false;
            }
            foreach (Pawn other in pawn.Map.mapPawns.FreeColonistsSpawned)
            {
                if (other == partner)
                {
                    return true;
                }
            }
            return false;
        }

        private static Pawn BestPartner(Pawn pawn)
        {
            if (pawn.Map == null || pawn.Map.mapPawns == null || pawn.relations == null)
            {
                return null;
            }
            Pawn best = null;
            int bestScore = int.MinValue;
            foreach (Pawn other in new List<Pawn>(pawn.Map.mapPawns.FreeColonistsSpawned))
            {
                if (!IsValidSocialPartner(pawn, other))
                {
                    continue;
                }
                int score = pawn.relations.OpinionOf(other);
                if (other.relations != null)
                {
                    score += other.relations.OpinionOf(pawn);
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = other;
                }
            }
            return best;
        }

        private static Job Flee(Pawn pawn, bool inside)
        {
            if (!inside)
            {
                try
                {
                    var giver = new JobGiver_ConfigurableHostilityResponse();
                    Job job = TryGiveJob != null
                        ? TryGiveJob.Invoke(giver, new object[] { pawn }) as Job
                        : null;
                    if (job != null)
                    {
                        return job;
                    }
                }
                catch (Exception error)
                {
                    LogOnce(error);
                }
            }

            IntVec3 cell;
            if (!TryFindSafeCell(pawn, inside, out cell))
            {
                return null;
            }
            return JobMaker.MakeJob(JobDefOf.Flee, cell);
        }

        private static bool TryFindSafeCell(Pawn pawn, bool inside, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            Map map = pawn.Map;
            if (map == null)
            {
                return false;
            }
            for (int radius = 15; radius <= 30; radius += 3)
            {
                foreach (IntVec3 candidate in GenRadial.RadialCellsAround(pawn.Position, radius, true))
                {
                    if (!candidate.InBounds(map) || !candidate.Standable(map) || !candidate.Walkable(map))
                    {
                        continue;
                    }
                    if (inside && !map.roofGrid.Roofed(candidate))
                    {
                        continue;
                    }
                    if (map.fogGrid.IsFogged(candidate))
                    {
                        continue;
                    }
                    if (!pawn.CanReach(new LocalTargetInfo(candidate), PathEndMode.OnCell, Danger.Deadly))
                    {
                        continue;
                    }
                    cell = candidate;
                    return true;
                }
            }
            return false;
        }

        private static Thing FindThing(Map map, long id)
        {
            if (map == null)
            {
                return null;
            }
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing != null && thing.thingIDNumber == id)
                {
                    return thing;
                }
            }
            return null;
        }

        private static bool SafeShouldSkip(WorkGiver_Scanner scanner, Pawn pawn)
        {
            try
            {
                return scanner.ShouldSkip(pawn, false);
            }
            catch (Exception error)
            {
                LogOnce(error);
                return true;
            }
        }

        private static bool SafeHasJobOnThing(WorkGiver_Scanner scanner, Pawn pawn, Thing thing)
        {
            try
            {
                return scanner.HasJobOnThing(pawn, thing, false);
            }
            catch (Exception error)
            {
                LogOnce(error);
                return false;
            }
        }

        private static bool SafeHasJobOnCell(WorkGiver_Scanner scanner, Pawn pawn, IntVec3 cell)
        {
            try
            {
                return scanner.HasJobOnCell(pawn, cell, false);
            }
            catch (Exception error)
            {
                LogOnce(error);
                return false;
            }
        }

        private static IEnumerable<Thing> Things(WorkGiver_Scanner scanner, Pawn pawn, int limit)
        {
            var buffer = new List<Thing>(limit);
            try
            {
                IEnumerable<Thing> things = scanner.PotentialWorkThingsGlobal(pawn);
                if (things != null)
                {
                    foreach (Thing thing in things)
                    {
                        if (thing == null)
                        {
                            continue;
                        }
                        buffer.Add(thing);
                        if (buffer.Count >= limit)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                LogOnce(error);
            }

            if (buffer.Count == 0 && scanner.def != null && scanner.def.scanThings
                && scanner.def.scannerDef != null
                && pawn.Map != null && pawn.Map.listerThings != null)
            {
                int scanned = 0;
                foreach (Thing thing in pawn.Map.listerThings.ThingsOfDef(scanner.def.scannerDef))
                {
                    if (++scanned > MaxFallbackThings)
                    {
                        break;
                    }
                    if (thing == null || !thing.Spawned)
                    {
                        continue;
                    }
                    if ((thing.Position - pawn.Position).LengthHorizontalSquared
                        > FallbackThingRadius * FallbackThingRadius)
                    {
                        continue;
                    }
                    if (!SafeHasJobOnThing(scanner, pawn, thing))
                    {
                        continue;
                    }
                    buffer.Add(thing);
                    if (buffer.Count >= limit)
                    {
                        break;
                    }
                }
            }

            foreach (Thing thing in buffer)
            {
                yield return thing;
            }
        }

        private static IEnumerable<IntVec3> Cells(WorkGiver_Scanner scanner, Pawn pawn, int limit)
        {
            var buffer = new List<IntVec3>(limit);
            try
            {
                IEnumerable<IntVec3> cells = scanner.PotentialWorkCellsGlobal(pawn);
                if (cells != null)
                {
                    foreach (IntVec3 cell in cells)
                    {
                        buffer.Add(cell);
                        if (buffer.Count >= limit)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                LogOnce(error);
            }

            if (buffer.Count == 0 && scanner.def != null && scanner.def.scanCells && pawn.Map != null)
            {
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(pawn.Position, FallbackRadius, true))
                {
                    if (!cell.IsValid || !SafeHasJobOnCell(scanner, pawn, cell))
                    {
                        continue;
                    }
                    buffer.Add(cell);
                    if (buffer.Count >= limit)
                    {
                        break;
                    }
                }
            }

            foreach (IntVec3 cell in buffer)
            {
                yield return cell;
            }
        }

        private static void LogOnce(Exception error, string context = null)
        {
            string key = (string.IsNullOrEmpty(context) ? "job build" : context)
                + ": " + error.GetType().Name + ": " + error.Message;
            if (ClefRuntime.ShouldLogError(key))
            {
                Log.Warning("[ColonistAutonomy] job build: " + key);
            }
        }
    }
}
