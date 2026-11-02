using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>Game facts that are cheap to read once per pawn and change the decision.</summary>
    internal sealed class ClefSignalContext
    {
        public float FoodPct;
        public string FoodCategory = "?";
        public float RestPct;
        public string RestCategory = "?";
        public float JoyPct;
        public string JoyCategory = "?";
        public float MoodPct;
        public float BreakMinor;
        public float BreakMajor;
        public float BreakExtreme;

        public float Pain;
        public float Consciousness = 1f;
        public float Manipulation = 1f;
        public float Moving = 1f;
        public float Sight = 1f;
        public bool Tendable;

        public bool Armed;
        public float ArmorSharp;

        public int Hour;
        public string TimeAssignment = "?";
        public int HoursUntilNight;
        public bool ScheduledSleep;

        public int ThreatCount;
        public float ThreatNearest = -1f;
        public string ThreatDirection;
        public float CoverChance;
        public bool HostilesNear;
        public string Danger = "None";

        public bool Indoors;
        public float AmbientTemp;
        public float Light;
        public float? RoomTemp;
        public float? RoomBeauty;
        public float? RoomImpressiveness;
        public float? RoomCleanliness;
    }

    /// <summary>Colony-wide facts shared by every pawn in one request.</summary>
    internal sealed class ClefColonySignals
    {
        private static readonly FieldInfo CurrentProjectField = typeof(ResearchManager).GetField(
            "currentProj",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public int Colonists;
        public int Idle;
        public int Meals;
        public int Medicine;
        public int Steel;
        public int Components;
        public int Silver;
        public int Wood;
        public float FoodNutrition;
        public float PowerStored;
        public float PowerGain;
        public int Bills;
        public int GrowingZones;
        public int Stockpiles;
        public int Construction;
        public int Designations;
        public int Hostiles;
        public string Research;
        public float ResearchProgress;

        public static ClefColonySignals Build(Map map)
        {
            var signals = new ClefColonySignals();
            if (map == null)
            {
                return signals;
            }

            if (map.mapPawns != null)
            {
                signals.Colonists = map.mapPawns.FreeColonistsSpawnedCount;
                foreach (Pawn pawn in new List<Pawn>(map.mapPawns.FreeColonistsSpawned))
                {
                    if (pawn.jobs == null || pawn.jobs.curJob == null)
                    {
                        signals.Idle++;
                    }
                }
            }

            ResourceCounter resources = map.resourceCounter;
            if (resources != null)
            {
                signals.Meals = resources.GetCount(ThingDefOf.MealSimple)
                    + resources.GetCount(ThingDefOf.MealFine)
                    + resources.GetCount(ThingDefOf.MealNutrientPaste)
                    + resources.GetCount(ThingDefOf.MealSurvivalPack);
                signals.Medicine = resources.GetCount(ThingDefOf.MedicineHerbal)
                    + resources.GetCount(ThingDefOf.MedicineIndustrial)
                    + resources.GetCount(ThingDefOf.MedicineUltratech);
                signals.Steel = resources.GetCount(ThingDefOf.Steel);
                signals.Components = resources.GetCount(ThingDefOf.ComponentIndustrial)
                    + resources.GetCount(ThingDefOf.ComponentSpacer);
                signals.Silver = resources.GetCount(ThingDefOf.Silver);
                signals.Wood = resources.GetCount(ThingDefOf.WoodLog);
                signals.FoodNutrition = resources.TotalHumanEdibleNutrition;
            }

            if (map.powerNetManager != null)
            {
                foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
                {
                    if (net == null)
                    {
                        continue;
                    }
                    signals.PowerStored += net.CurrentStoredEnergy();
                    signals.PowerGain += net.CurrentEnergyGainRate();
                }
            }

            Safe("bills", () =>
            {
                if (map.listerBuildings == null)
                {
                    return;
                }
                foreach (Building_WorkTable table in map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>())
                {
                    if (table == null || table.billStack == null || table.billStack.Bills == null)
                    {
                        continue;
                    }
                    foreach (Bill bill in table.billStack.Bills)
                    {
                        if (bill != null && bill.ShouldDoNow())
                        {
                            signals.Bills++;
                        }
                    }
                }
            });
            Safe("zones", () =>
            {
                if (map.zoneManager == null || map.zoneManager.AllZones == null)
                {
                    return;
                }
                foreach (Zone zone in map.zoneManager.AllZones)
                {
                    if (zone is Zone_Growing)
                    {
                        signals.GrowingZones++;
                    }
                    else if (zone is Zone_Stockpile)
                    {
                        signals.Stockpiles++;
                    }
                }
            });
            Safe("construction", () =>
            {
                if (map.listerThings == null)
                {
                    return;
                }
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing is Blueprint || thing is Frame)
                    {
                        signals.Construction++;
                    }
                }
            });
            Safe("designations", () =>
            {
                if (map.designationManager == null)
                {
                    return;
                }
                foreach (Designation designation in map.designationManager.AllDesignations)
                {
                    signals.Designations++;
                }
            });
            Safe("research", () =>
            {
                if (Find.ResearchManager == null || CurrentProjectField == null)
                {
                    return;
                }
                ResearchProjectDef project = CurrentProjectField.GetValue(Find.ResearchManager) as ResearchProjectDef;
                if (project == null)
                {
                    return;
                }
                signals.Research = project.defName;
                float progress = Find.ResearchManager.GetProgress(project);
                signals.ResearchProgress = project.baseCost > 0f ? progress / project.baseCost : 0f;
            });
            Safe("threats", () =>
            {
                if (map.attackTargetsCache == null)
                {
                    return;
                }
                foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToColony)
                {
                    Thing thing = target != null ? target.Thing : null;
                    if (thing != null && thing.Spawned && !thing.Destroyed)
                    {
                        signals.Hostiles++;
                    }
                }
            });

            return signals;
        }

        private static void Safe(string section, Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                string message = "colony signals " + section + ": " + error.GetType().Name + ": " + error.Message;
                if (ClefRuntime.Debug || ClefRuntime.ShouldLogError(message))
                {
                    Log.Warning("[ColonistAutonomy] " + message);
                }
            }
        }
    }

    internal static class ClefSignals
    {
        public static ClefSignalContext Build(Pawn pawn, ClefColonySignals colony)
        {
            var signals = new ClefSignalContext();
            Map map = pawn.Map;

            Safe(pawn, "needs", () => Needs(pawn, signals));
            Safe(pawn, "health", () => Health(pawn, signals));
            Safe(pawn, "equipment", () => Equipment(pawn, signals));
            Safe(pawn, "schedule", () => Schedule(pawn, signals));
            Safe(pawn, "environment", () => Environment(pawn, map, signals));
            Safe(pawn, "threats", () => Threats(pawn, map, signals));
            return signals;
        }

        private static void Safe(Pawn pawn, string section, Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                string detail = section + ": " + error.GetType().Name + ": " + error.Message;
                if (ClefRuntime.Debug || ClefRuntime.ShouldLogError(detail))
                {
                    Log.Warning("[ColonistAutonomy] signals " + section + " for " + pawn.LabelShort + ": " + detail);
                }
            }
        }

        private static void Needs(Pawn pawn, ClefSignalContext signals)
        {
            if (pawn.needs == null)
            {
                return;
            }
            if (pawn.needs.food != null)
            {
                signals.FoodPct = pawn.needs.food.CurLevelPercentage;
                signals.FoodCategory = pawn.needs.food.CurCategory.ToString();
            }
            if (pawn.needs.rest != null)
            {
                signals.RestPct = pawn.needs.rest.CurLevelPercentage;
                signals.RestCategory = pawn.needs.rest.CurCategory.ToString();
            }
            if (pawn.needs.joy != null)
            {
                signals.JoyPct = pawn.needs.joy.CurLevelPercentage;
                signals.JoyCategory = pawn.needs.joy.CurCategory.ToString();
            }
            if (pawn.needs.mood != null)
            {
                signals.MoodPct = pawn.needs.mood.CurLevelPercentage;
            }
            if (pawn.mindState != null && pawn.mindState.mentalBreaker != null)
            {
                signals.BreakMinor = pawn.mindState.mentalBreaker.BreakThresholdMinor;
                signals.BreakMajor = pawn.mindState.mentalBreaker.BreakThresholdMajor;
                signals.BreakExtreme = pawn.mindState.mentalBreaker.BreakThresholdExtreme;
            }
        }

        private static void Health(Pawn pawn, ClefSignalContext signals)
        {
            if (pawn.health == null)
            {
                return;
            }
            if (pawn.health.hediffSet != null)
            {
                signals.Pain = pawn.health.hediffSet.PainTotal;
                signals.Tendable = pawn.health.hediffSet.HasTendableHediff(false);
            }
            if (pawn.health.capacities != null)
            {
                signals.Consciousness = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness);
                signals.Manipulation = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
                signals.Moving = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
                signals.Sight = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Sight);
            }
        }

        private static void Equipment(Pawn pawn, ClefSignalContext signals)
        {
            signals.Armed = pawn.equipment != null && pawn.equipment.Primary != null;
            signals.ArmorSharp = StatExtension.GetStatValue(
                pawn, StatDefOf.ArmorRating_Sharp, true, -1);
        }

        private static void Schedule(Pawn pawn, ClefSignalContext signals)
        {
            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }
            signals.Hour = GenLocalDate.HourInteger(map);
            signals.HoursUntilNight = signals.Hour >= 22 || signals.Hour < 6
                ? 0
                : 22 - signals.Hour;
            if (pawn.timetable != null && pawn.timetable.CurrentAssignment != null)
            {
                signals.TimeAssignment = pawn.timetable.CurrentAssignment.defName;
                signals.ScheduledSleep = pawn.timetable.CurrentAssignment == TimeAssignmentDefOf.Sleep;
            }
        }

        private static void Environment(Pawn pawn, Map map, ClefSignalContext signals)
        {
            if (map == null)
            {
                return;
            }
            signals.AmbientTemp = pawn.AmbientTemperature;
            signals.Indoors = map.roofGrid != null && map.roofGrid.Roofed(pawn.Position);
            if (map.glowGrid != null)
            {
                signals.Light = map.glowGrid.GroundGlowAt(pawn.Position, false, false);
            }

            Room room = RegionAndRoomQuery.GetRoom(pawn, RegionType.Set_Passable);
            if (room == null)
            {
                return;
            }
            signals.RoomTemp = room.Temperature;
            signals.RoomBeauty = room.GetStat(RoomStatDefOf.Beauty);
            signals.RoomImpressiveness = room.GetStat(RoomStatDefOf.Impressiveness);
            signals.RoomCleanliness = room.GetStat(RoomStatDefOf.Cleanliness);
        }

        private static void Threats(Pawn pawn, Map map, ClefSignalContext signals)
        {
            if (map == null)
            {
                return;
            }
            signals.HostilesNear = pawn.mindState != null && pawn.mindState.anyCloseHostilesRecently;
            signals.Danger = map.dangerWatcher != null
                ? map.dangerWatcher.DangerRating.ToString()
                : "None";
            if (map.attackTargetsCache == null)
            {
                return;
            }

            float nearest = float.MaxValue;
            IntVec3 nearestPosition = IntVec3.Invalid;
            foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToColony)
            {
                Thing thing = target.Thing;
                if (thing == null || !thing.Spawned || thing.Destroyed)
                {
                    continue;
                }
                Pawn hostile = thing as Pawn;
                if (hostile != null && hostile.Dead)
                {
                    continue;
                }
                float distance = (thing.Position - pawn.Position).LengthHorizontal;
                if (distance <= 40f)
                {
                    signals.ThreatCount++;
                }
                if (distance < nearest)
                {
                    nearest = distance;
                    nearestPosition = thing.Position;
                }
            }

            if (!nearestPosition.IsValid)
            {
                return;
            }
            signals.ThreatNearest = nearest;
            signals.ThreatDirection = Direction(nearestPosition - pawn.Position);
            if (nearest <= 40f)
            {
                signals.CoverChance = CoverUtility.CalculateOverallBlockChance(
                    new LocalTargetInfo(pawn), nearestPosition, map);
            }
        }

        private static string Direction(IntVec3 delta)
        {
            if (delta.x == 0 && delta.z == 0)
            {
                return "here";
            }
            string northSouth = delta.z > 0 ? "N" : delta.z < 0 ? "S" : "";
            string eastWest = delta.x > 0 ? "E" : delta.x < 0 ? "W" : "";
            return northSouth + eastWest;
        }
    }
}
