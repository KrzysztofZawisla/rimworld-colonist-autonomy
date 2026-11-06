using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    internal sealed class ClefOption
    {
        public readonly string Id;
        public readonly string Description;

        public ClefOption(string id, string description)
        {
            Id = id;
            Description = description;
        }
    }

    internal sealed class ClefCandidatePools
    {
        public readonly List<ClefOption> Needs = new List<ClefOption>();
        public readonly List<string> WorkTypeNames = new List<string>();
        public readonly List<ClefOption> WorkTypes = new List<ClefOption>();
        public readonly List<ClefOption> Work = new List<ClefOption>();
        public readonly List<ClefOption> Social = new List<ClefOption>();
        public readonly List<ClefOption> Threat = new List<ClefOption>();
        public readonly List<ClefOption> Idle = new List<ClefOption>();
    }

    internal static class ClefCandidates
    {
        private const int MaxWorkOptions = 16;
        private const int MaxWorkTypes = 16;
        private const int MaxGiversPerType = 8;
        private const int MaxThingsPerGiver = 4;
        private const int MaxCellsPerGiver = 4;
        private const int MaxFallbackThings = 800;
        private const int FallbackRadius = 16;
        private const int FallbackThingRadius = 30;
        private const int MaxDrugOptions = 4;

        public static ClefCandidatePools Build(
            Pawn pawn,
            bool scanTargets,
            ClefSignalContext signals,
            ClefColonySignals colony)
        {
            var pools = new ClefCandidatePools();
            AddNeeds(pawn, pools, signals, colony);
            AddDrugs(pawn, pools);
            AddWork(pawn, pools, scanTargets, HostilePositions(pawn.Map));
            AddSocial(pawn, pools);
            AddThreat(pawn, pools, signals);
            pools.Idle.Add(new ClefOption("idle", "Wander / do nothing"));
            FilterFailedChoice(pawn, pools);
            return pools;
        }

        private static void FilterFailedChoice(Pawn pawn, ClefCandidatePools pools)
        {
            HashSet<string> failed = ClefRuntime.RecentlyFailedChoices(pawn.thingIDNumber);
            if (failed.Count == 0)
            {
                return;
            }
            pools.Needs.RemoveAll(option => failed.Contains(option.Id));
            pools.Work.RemoveAll(option => failed.Contains(option.Id));
            pools.Social.RemoveAll(option => failed.Contains(option.Id));
            pools.Threat.RemoveAll(option => failed.Contains(option.Id));
            pools.WorkTypes.RemoveAll(option => failed.Contains(option.Id));
            pools.WorkTypeNames.RemoveAll(name => failed.Contains("work:" + name));
        }

        private static void AddNeeds(
            Pawn pawn,
            ClefCandidatePools pools,
            ClefSignalContext signals,
            ClefColonySignals colony)
        {
            if (pawn.needs == null)
            {
                return;
            }
            if (pawn.needs.food != null && pawn.needs.food.CurLevelPercentage < 0.55f)
            {
                bool foodAvailable = colony.Meals > 0
                    || colony.FoodNutrition > 0.05f
                    || CarriesFood(pawn);
                if (foodAvailable)
                {
                    pools.Needs.Add(new ClefOption(
                        "eat",
                        "Eat a meal (food: " + signals.FoodCategory
                        + ", colony meals: " + colony.Meals + ")"));
                }
            }
            if (pawn.needs.rest != null && pawn.needs.rest.CurLevelPercentage < 0.55f)
            {
                pools.Needs.Add(new ClefOption(
                    "sleep",
                    "Sleep (rest: " + signals.RestCategory
                    + ", night in " + signals.HoursUntilNight + "h"
                    + (signals.ScheduledSleep ? ", scheduled" : "") + ")"));
            }
            if (pawn.needs.joy != null && pawn.needs.joy.CurLevelPercentage < 0.55f && JoyAvailable(pawn))
            {
                pools.Needs.Add(new ClefOption(
                    "joy",
                    "Recreation (joy: " + signals.JoyCategory + ")"));
            }
        }

        private static void AddDrugs(Pawn pawn, ClefCandidatePools pools)
        {
            if (pawn.drugs == null || pawn.drugs.CurrentPolicy == null || pawn.Map == null
                || pawn.needs == null)
            {
                return;
            }
            List<Thing> drugs;
            try
            {
                drugs = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Drug);
            }
            catch (Exception error)
            {
                LogOnce(error, "AddDrugs");
                return;
            }

            int added = 0;
            foreach (Thing thing in drugs)
            {
                if (added >= MaxDrugOptions)
                {
                    return;
                }
                if (thing == null || !thing.Spawned || thing.def == null)
                {
                    continue;
                }
                DrugPolicyEntry entry = pawn.drugs.CurrentPolicy[thing.def];
                if (entry == null || !entry.allowedForJoy)
                {
                    continue;
                }
                if (entry.onlyIfMoodBelow < 1f && pawn.needs.mood != null
                    && pawn.needs.mood.CurLevelPercentage >= entry.onlyIfMoodBelow)
                {
                    continue;
                }
                if (entry.onlyIfJoyBelow < 1f && pawn.needs.joy != null
                    && pawn.needs.joy.CurLevelPercentage >= entry.onlyIfJoyBelow)
                {
                    continue;
                }
                if (thing.IsForbidden(pawn)
                    || !pawn.CanReach(new LocalTargetInfo(thing), PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                CompProperties_Drug props = thing.def.GetCompProperties<CompProperties_Drug>();
                string risk = props != null && props.Addictive ? ", addictive" : "";
                pools.Needs.Add(new ClefOption(
                    "drug:" + thing.thingIDNumber,
                    "Take " + thing.LabelShort + " (drug x" + thing.stackCount + risk + ")"));
                added++;
            }
        }

        private static bool CarriesFood(Pawn pawn)
        {
            Thing carried = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            if (carried != null && carried.def != null && carried.def.IsNutritionGivingIngestible)
            {
                return true;
            }
            if (pawn.inventory == null || pawn.inventory.innerContainer == null)
            {
                return false;
            }
            foreach (Thing thing in pawn.inventory.innerContainer)
            {
                if (thing != null && thing.def != null && thing.def.IsNutritionGivingIngestible)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool JoyAvailable(Pawn pawn)
        {
            Map map = pawn.Map;
            if (map == null)
            {
                return false;
            }
            if (JoyUtility.JoyKindsOnMapCount(map) > 0)
            {
                return true;
            }
            try
            {
                return JoyUtility.EnjoyableOutsideNow(pawn, new StringBuilder());
            }
            catch (Exception error)
            {
                LogOnce(error, "JoyAvailable");
                return false;
            }
        }

        private static void AddWork(
            Pawn pawn,
            ClefCandidatePools pools,
            bool scanTargets,
            List<IntVec3> hostiles)
        {
            if (pawn.workSettings == null)
            {
                return;
            }

            var active = new List<WorkTypeDef>();
            foreach (WorkTypeDef type in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (pawn.WorkTypeIsDisabled(type))
                {
                    continue;
                }
                if (!pawn.workSettings.WorkIsActive(type))
                {
                    continue;
                }
                if (pawn.workSettings.GetPriority(type) <= 0)
                {
                    continue;
                }
                active.Add(type);
            }
            active.Sort((left, right) =>
            {
                int byPriority = pawn.workSettings.GetPriority(left).CompareTo(pawn.workSettings.GetPriority(right));
                return byPriority != 0 ? byPriority : left.naturalPriority.CompareTo(right.naturalPriority);
            });

            int typeLimit = Math.Min(MaxWorkTypes, active.Count);
            for (int typeIndex = 0; typeIndex < typeLimit; typeIndex++)
            {
                WorkTypeDef type = active[typeIndex];
                pools.WorkTypeNames.Add(type.defName);
                pools.WorkTypes.Add(new ClefOption(
                    "work:" + type.defName,
                    type.labelShort + " (prio " + pawn.workSettings.GetPriority(type)
                    + ", " + SkillText(pawn, type) + ")"));
            }

            // Intent mode only needs the work type; the concrete target comes from vanilla.
            if (!scanTargets)
            {
                return;
            }

            for (int typeIndex = 0; typeIndex < typeLimit; typeIndex++)
            {
                WorkTypeDef type = active[typeIndex];
                int givers = 0;
                foreach (WorkGiverDef def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    if (def.workType != type)
                    {
                        continue;
                    }
                    WorkGiver_Scanner scanner = def.Worker as WorkGiver_Scanner;
                    if (scanner == null)
                    {
                        continue;
                    }
                    if (givers++ >= MaxGiversPerType)
                    {
                        break;
                    }
                    if (SafeShouldSkip(scanner, pawn))
                    {
                        continue;
                    }

                    // A scanner may support things, cells, or both; the unsupported one returns null.
                    int added = 0;
                    foreach (Thing thing in SafeThings(scanner, pawn, MaxThingsPerGiver, def.scanThings))
                    {
                        if (thing == null || !thing.Spawned)
                        {
                            continue;
                        }
                        if (!SafeHasJobOnThing(scanner, pawn, thing))
                        {
                            continue;
                        }
                        if (!ClefScheduleGate.FitsSchedule(
                            pawn,
                            new LocalTargetInfo(thing),
                            LocalTargetInfo.Invalid,
                            def.workType != null ? def.workType.defName : null))
                        {
                            continue;
                        }
                        AddWork(
                            pools,
                            "work:" + def.defName + ":" + thing.thingIDNumber,
                            WorkThingDescription(def, thing, pawn, hostiles));
                        added++;
                        if (added >= MaxThingsPerGiver)
                        {
                            break;
                        }
                    }

                    foreach (IntVec3 cell in SafeCells(scanner, pawn, MaxCellsPerGiver, def.scanCells))
                    {
                        if (!cell.IsValid)
                        {
                            continue;
                        }
                        if (!SafeHasJobOnCell(scanner, pawn, cell))
                        {
                            continue;
                        }
                        if (!ClefScheduleGate.FitsSchedule(
                            pawn,
                            new LocalTargetInfo(cell),
                            LocalTargetInfo.Invalid,
                            def.workType != null ? def.workType.defName : null))
                        {
                            continue;
                        }
                        AddWork(
                            pools,
                            "work:" + def.defName + ":cell:" + cell.x + "," + cell.z,
                            WorkCellDescription(def, cell, pawn, hostiles));
                        added++;
                        if (added >= MaxThingsPerGiver + MaxCellsPerGiver)
                        {
                            break;
                        }
                    }

                    if (pools.Work.Count >= MaxWorkOptions)
                    {
                        break;
                    }
                }
                if (pools.Work.Count >= MaxWorkOptions)
                {
                    break;
                }
            }

            if (pools.Work.Count < 4)
            {
                int typeFallback = Math.Min(2, pools.WorkTypes.Count);
                for (int index = 0; index < typeFallback; index++)
                {
                    ClefOption option = pools.WorkTypes[index];
                    AddWork(pools, option.Id, option.Description + " (type)");
                }
            }
        }

        private static void AddSocial(Pawn pawn, ClefCandidatePools pools)
        {
            if (ClefDefs.SocialJob == null || InteractionDefOf.Chitchat == null)
            {
                return;
            }
            if (pawn.relations == null || pawn.Map == null || pawn.Map.mapPawns == null)
            {
                return;
            }
            var partners = new List<KeyValuePair<Pawn, int>>();
            foreach (Pawn other in new List<Pawn>(pawn.Map.mapPawns.FreeColonistsSpawned))
            {
                if (other == null || other == pawn || !other.Spawned)
                {
                    continue;
                }
                if (other.Downed || other.InMentalState || other.health == null || other.health.Dead)
                {
                    continue;
                }
                if (pawn.interactions != null
                    && !pawn.interactions.CanInteractNowWith(other, InteractionDefOf.Chitchat))
                {
                    continue;
                }
                int own = pawn.relations.OpinionOf(other);
                int score = own;
                if (other.relations != null)
                {
                    score += other.relations.OpinionOf(pawn);
                }
                partners.Add(new KeyValuePair<Pawn, int>(other, score));
            }
            partners.Sort((left, right) => right.Value.CompareTo(left.Value));
            int limit = Math.Min(3, partners.Count);
            for (int index = 0; index < limit; index++)
            {
                Pawn partner = partners[index].Key;
                int own = pawn.relations.OpinionOf(partner);
                pools.Social.Add(new ClefOption(
                    "social:" + partner.thingIDNumber,
                    "Talk with " + partner.LabelShort + " (" + Relation(pawn, partner, own)
                    + ", opinion " + own.ToString("+0;-0;0") + ")"));
            }
        }

        private static string Relation(Pawn pawn, Pawn other, int opinion)
        {
            if (pawn.relations != null)
            {
                if (pawn.relations.DirectRelationExists(PawnRelationDefOf.Spouse, other))
                {
                    return "spouse";
                }
                if (pawn.relations.DirectRelationExists(PawnRelationDefOf.Lover, other))
                {
                    return "lover";
                }
                if (pawn.relations.DirectRelationExists(PawnRelationDefOf.Fiance, other))
                {
                    return "fiance";
                }
                if (pawn.relations.DirectRelationExists(PawnRelationDefOf.ExLover, other))
                {
                    return "ex-lover";
                }
                if (pawn.relations.DirectRelationExists(PawnRelationDefOf.ExSpouse, other))
                {
                    return "ex-spouse";
                }
            }
            if (opinion <= -20)
            {
                return "rival";
            }
            if (opinion >= 40)
            {
                return "friend";
            }
            return "neutral";
        }

        private static void AddThreat(Pawn pawn, ClefCandidatePools pools, ClefSignalContext signals)
        {
            if (pawn.Map == null)
            {
                return;
            }
            if (signals.ThreatCount <= 0 && !signals.HostilesNear && signals.Danger == "None")
            {
                return;
            }

            string where;
            if (signals.ThreatCount > 0 && signals.ThreatNearest >= 0f)
            {
                where = "from " + signals.ThreatCount + " hostiles, nearest "
                    + (int)Math.Round(signals.ThreatNearest) + "m"
                    + (string.IsNullOrEmpty(signals.ThreatDirection)
                        ? ""
                        : " " + signals.ThreatDirection);
            }
            else
            {
                where = "danger: " + signals.Danger;
            }

            string armed = signals.Armed ? "armed" : "unarmed";
            string cover = signals.ThreatCount > 0
                ? ", cover " + (int)Math.Round(signals.CoverChance * 100f) + "%"
                : "";
            pools.Threat.Add(new ClefOption("flee", "Flee " + where + " (" + armed + cover + ")"));
            pools.Threat.Add(new ClefOption(
                "hide",
                (signals.Indoors ? "Stay indoors" : "Get indoors") + " (" + where + ")"));
        }

        private static string WorkThingDescription(
            WorkGiverDef def,
            Thing thing,
            Pawn pawn,
            List<IntVec3> hostiles)
        {
            var tags = new List<string>();
            if (thing.stackCount > 1)
            {
                tags.Add("x" + thing.stackCount);
            }
            if (thing.def.IsNutritionGivingIngestible)
            {
                tags.Add("food");
            }
            if (thing.def.IsMedicine)
            {
                tags.Add("medicine");
            }
            if (thing.def.useHitPoints && thing.MaxHitPoints > 0 && thing.HitPoints < thing.MaxHitPoints)
            {
                tags.Add("dmg " + (int)Math.Round(100f * thing.HitPoints / thing.MaxHitPoints) + "%");
            }
            if (Deteriorating(thing))
            {
                tags.Add("deteriorating");
            }
            if (thing is Plant plant)
            {
                tags.Add("growth " + (int)Math.Round(plant.Growth * 100f) + "%");
            }
            if (thing is Blueprint)
            {
                tags.Add("blueprint");
            }
            else if (thing is Frame)
            {
                tags.Add("frame");
            }

            string detail = tags.Count > 0 ? ", " + string.Join(", ", tags.ToArray()) : "";
            return def.label + " " + thing.LabelShort + " ("
                + Distance(pawn.Position, thing.Position) + "m" + detail
                + EnemyTag(thing.Position, hostiles) + ", "
                + WorkMeta(def, pawn) + ")";
        }

        private static string WorkCellDescription(
            WorkGiverDef def,
            IntVec3 cell,
            Pawn pawn,
            List<IntVec3> hostiles)
        {
            return def.label + " at (" + cell.x + "," + cell.z + ") ("
                + Distance(pawn.Position, cell) + "m" + EnemyTag(cell, hostiles) + ", "
                + WorkMeta(def, pawn) + ")";
        }

        private static List<IntVec3> HostilePositions(Map map)
        {
            var cells = new List<IntVec3>();
            if (map == null || map.mapPawns == null)
            {
                return cells;
            }
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null)
            {
                return cells;
            }
            foreach (Pawn other in map.mapPawns.AllPawnsSpawned)
            {
                if (other != null && other.Spawned && other.Faction != null
                    && other.Faction.HostileTo(player))
                {
                    cells.Add(other.Position);
                }
            }
            return cells;
        }

        private static string EnemyTag(IntVec3 cell, List<IntVec3> hostiles)
        {
            if (hostiles == null || hostiles.Count == 0)
            {
                return "";
            }
            float best = float.MaxValue;
            foreach (IntVec3 hostile in hostiles)
            {
                float distance = hostile.DistanceTo(cell);
                if (distance < best)
                {
                    best = distance;
                }
            }
            return best <= 15f ? ", enemy " + (int)Math.Round(best) + "m" : "";
        }

        private static string WorkMeta(WorkGiverDef def, Pawn pawn)
        {
            WorkTypeDef type = def.workType;
            int priority = type != null && pawn.workSettings != null
                ? pawn.workSettings.GetPriority(type)
                : 0;
            return "prio " + priority + ", " + SkillText(pawn, type);
        }

        private static string SkillText(Pawn pawn, WorkTypeDef type)
        {
            if (type == null || type.relevantSkills == null || type.relevantSkills.Count == 0)
            {
                return "no skill";
            }
            string bestName = null;
            int best = -1;
            foreach (SkillDef skill in type.relevantSkills)
            {
                if (skill == null)
                {
                    continue;
                }
                SkillRecord record = pawn.skills != null ? pawn.skills.GetSkill(skill) : null;
                if (record == null)
                {
                    continue;
                }
                if (record.Level > best)
                {
                    best = record.Level;
                    bestName = skill.defName;
                }
            }
            return bestName != null ? bestName + " " + best : "no skill";
        }

        private static bool Deteriorating(Thing thing)
        {
            Map map = thing.Map;
            if (map == null || map.roofGrid == null)
            {
                return false;
            }
            float rate = StatExtension.GetStatValue(thing, StatDefOf.DeteriorationRate, true, -1);
            return rate > 0f && !map.roofGrid.Roofed(thing.Position);
        }

        private static void AddWork(ClefCandidatePools pools, string id, string description)
        {
            if (pools.Work.Count >= MaxWorkOptions)
            {
                return;
            }
            foreach (ClefOption existing in pools.Work)
            {
                if (existing.Id == id)
                {
                    return;
                }
            }
            pools.Work.Add(new ClefOption(id, description));
        }

        private static bool SafeShouldSkip(WorkGiver_Scanner scanner, Pawn pawn)
        {
            try
            {
                return scanner.ShouldSkip(pawn, false);
            }
            catch (Exception error)
            {
                LogOnce(error, "ShouldSkip " + ScannerName(scanner));
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
                LogOnce(error, "HasJobOnThing " + ScannerName(scanner) + " "
                    + (thing != null && thing.def != null ? thing.def.defName : "?"));
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
                LogOnce(error, "HasJobOnCell " + ScannerName(scanner) + " " + cell);
                return false;
            }
        }

        private static IEnumerable<Thing> SafeThings(
            WorkGiver_Scanner scanner,
            Pawn pawn,
            int limit,
            bool scanThings)
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
                LogOnce(error, "PotentialWorkThings " + ScannerName(scanner));
            }

            if (buffer.Count == 0 && scanThings && pawn.Map != null && pawn.Map.listerThings != null
                && scanner.def != null && scanner.def.scannerDef != null)
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

        private static IEnumerable<IntVec3> SafeCells(
            WorkGiver_Scanner scanner,
            Pawn pawn,
            int limit,
            bool scanCells)
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
                LogOnce(error, "PotentialWorkCells " + ScannerName(scanner));
            }

            if (buffer.Count == 0 && scanCells && pawn.Map != null)
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

        private static int Distance(IntVec3 from, IntVec3 to)
        {
            return (int)Math.Round((from - to).LengthHorizontal);
        }

        private static void LogOnce(Exception error, string context = null)
        {
            string key = (string.IsNullOrEmpty(context) ? "candidate scan" : context)
                + ": " + error.GetType().Name + ": " + error.Message;
            if (ClefRuntime.ShouldLogError(key))
            {
                Log.Warning("[ColonistAutonomy] candidate scan: " + key);
            }
        }

        private static string ScannerName(WorkGiver_Scanner scanner)
        {
            return scanner != null && scanner.def != null ? scanner.def.defName : "?";
        }
    }
}
