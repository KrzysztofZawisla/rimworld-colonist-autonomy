using System;
using System.Collections.Generic;
using System.Text.Json;
using RimWorld;
using Verse;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// Builds the compact JSON state the decision model reads. Keep this small: the model is a
    /// decision model, not a chat model, and llama-server has a finite batch.
    /// </summary>
    internal static class ClefState
    {
        public static string BuildColony(
            List<Pawn> pawns,
            List<ClefSignalContext> signals,
            ClefColonySignals colony)
        {
            var states = new List<Dictionary<string, object>>(pawns.Count);
            for (int index = 0; index < pawns.Count; index++)
            {
                states.Add(PawnState(pawns[index], signals[index]));
            }
            var state = new Dictionary<string, object>
            {
                ["pawns"] = states,
                ["colony"] = ColonyState(colony)
            };
            return JsonSerializer.Serialize(state, ClefJson.Options);
        }

        private static Dictionary<string, object> PawnState(Pawn pawn, ClefSignalContext signals)
        {
            return new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = pawn.LabelShort,
                ["signals"] = Signals(signals),
                ["map"] = MapState(pawn),
                ["job"] = pawn.jobs != null && pawn.jobs.curJob != null && pawn.jobs.curJob.def != null
                    ? pawn.jobs.curJob.def.defName
                    : null,
                ["drafted"] = pawn.Drafted,
                ["downed"] = pawn.Downed,
                ["mental"] = pawn.InMentalState,
                ["health"] = Round(pawn.health != null && pawn.health.summaryHealth != null
                    ? pawn.health.summaryHealth.SummaryHealthPercent
                    : 1f, 2),
                ["bleed"] = Round(pawn.health != null && pawn.health.hediffSet != null
                    ? pawn.health.hediffSet.BleedRateTotal
                    : 0f, 2),
                ["needs"] = Needs(pawn),
                ["skills"] = Skills(pawn),
                ["work"] = Work(pawn),
                ["traits"] = Traits(pawn),
                ["history"] = ClefRuntime.DescribeDecision(pawn.thingIDNumber)
            };
        }

        private static Dictionary<string, object> Needs(Pawn pawn)
        {
            var needs = new Dictionary<string, object>();
            AddNeed(needs, "food", pawn.needs != null ? pawn.needs.food : null);
            AddNeed(needs, "rest", pawn.needs != null ? pawn.needs.rest : null);
            AddNeed(needs, "joy", pawn.needs != null ? pawn.needs.joy : null);
            AddNeed(needs, "mood", pawn.needs != null ? pawn.needs.mood : null);
            AddNeed(needs, "comfort", pawn.needs != null ? pawn.needs.comfort : null);
            return needs;
        }

        private static void AddNeed(Dictionary<string, object> target, string key, Need need)
        {
            if (need != null)
            {
                target[key] = Round(need.CurLevelPercentage, 2);
            }
        }

        private static Dictionary<string, object> Skills(Pawn pawn)
        {
            var skills = new Dictionary<string, object>();
            List<SkillRecord> records = pawn.skills != null ? pawn.skills.skills : null;
            if (records == null)
            {
                return skills;
            }
            foreach (SkillRecord record in records)
            {
                if (record == null || record.def == null || record.TotallyDisabled || record.Level < 4)
                {
                    continue;
                }
                skills[record.def.defName] = record.Level;
                if (skills.Count >= 5)
                {
                    break;
                }
            }
            return skills;
        }

        private static Dictionary<string, object> Work(Pawn pawn)
        {
            var work = new Dictionary<string, object>();
            if (pawn.workSettings == null)
            {
                return work;
            }
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
                int priority = pawn.workSettings.GetPriority(type);
                if (priority <= 0)
                {
                    continue;
                }
                work[type.defName] = priority;
                if (work.Count >= 8)
                {
                    break;
                }
            }
            return work;
        }

        private static List<string> Traits(Pawn pawn)
        {
            var traits = new List<string>();
            List<Trait> records = pawn.story != null && pawn.story.traits != null
                ? pawn.story.traits.allTraits
                : null;
            if (records == null)
            {
                return traits;
            }
            foreach (Trait trait in records)
            {
                if (trait == null || trait.def == null || string.IsNullOrEmpty(trait.def.label))
                {
                    continue;
                }
                traits.Add(trait.def.label);
                if (traits.Count >= 3)
                {
                    break;
                }
            }
            return traits;
        }

        private static Dictionary<string, object> MapState(Pawn pawn)
        {
            var result = new Dictionary<string, object>();
            Map map = pawn.Map;
            if (map == null)
            {
                return result;
            }
            result["hour"] = GenLocalDate.HourInteger(map);
            result["weather"] = map.weatherManager != null && map.weatherManager.curWeather != null
                ? map.weatherManager.curWeather.label
                : null;
            result["outdoor_temp"] = Round(map.mapTemperature != null ? map.mapTemperature.OutdoorTemp : 0f, 1);
            result["danger"] = map.dangerWatcher != null ? map.dangerWatcher.DangerRating.ToString() : null;
            result["hostiles_near"] = pawn.mindState != null && pawn.mindState.anyCloseHostilesRecently;
            result["indoors"] = map.roofGrid != null && map.roofGrid.Roofed(pawn.Position);
            result["x"] = pawn.Position.x;
            result["z"] = pawn.Position.z;
            return result;
        }

        private static Dictionary<string, object> Signals(ClefSignalContext signals)
        {
            return new Dictionary<string, object>
            {
                ["food"] = signals.FoodCategory,
                ["rest"] = signals.RestCategory,
                ["joy"] = signals.JoyCategory,
                ["mood"] = Round(signals.MoodPct, 2),
                ["break"] = new Dictionary<string, object>
                {
                    ["minor"] = Round(signals.BreakMinor, 2),
                    ["major"] = Round(signals.BreakMajor, 2),
                    ["extreme"] = Round(signals.BreakExtreme, 2)
                },
                ["health"] = new Dictionary<string, object>
                {
                    ["pain"] = Round(signals.Pain, 2),
                    ["consciousness"] = Round(signals.Consciousness, 2),
                    ["manipulation"] = Round(signals.Manipulation, 2),
                    ["moving"] = Round(signals.Moving, 2),
                    ["sight"] = Round(signals.Sight, 2),
                    ["tendable"] = signals.Tendable
                },
                ["threat"] = new Dictionary<string, object>
                {
                    ["count"] = signals.ThreatCount,
                    ["nearest"] = signals.ThreatNearest >= 0f
                        ? (object)Round(signals.ThreatNearest, 0)
                        : null,
                    ["dir"] = signals.ThreatDirection,
                    ["armed"] = signals.Armed,
                    ["armor"] = Round(signals.ArmorSharp, 2),
                    ["cover"] = Round(signals.CoverChance, 2),
                    ["near"] = signals.HostilesNear,
                    ["danger"] = signals.Danger
                },
                ["time"] = new Dictionary<string, object>
                {
                    ["hour"] = signals.Hour,
                    ["assignment"] = signals.TimeAssignment,
                    ["night_in"] = signals.HoursUntilNight,
                    ["sleep"] = signals.ScheduledSleep
                },
                ["env"] = new Dictionary<string, object>
                {
                    ["temp"] = Round(signals.AmbientTemp, 1),
                    ["light"] = Round(signals.Light, 2),
                    ["indoors"] = signals.Indoors,
                    ["room_temp"] = signals.RoomTemp.HasValue
                        ? (object)Round(signals.RoomTemp.Value, 1)
                        : null,
                    ["beauty"] = signals.RoomBeauty.HasValue
                        ? (object)Round(signals.RoomBeauty.Value, 1)
                        : null,
                    ["impressive"] = signals.RoomImpressiveness.HasValue
                        ? (object)Round(signals.RoomImpressiveness.Value, 1)
                        : null,
                    ["clean"] = signals.RoomCleanliness.HasValue
                        ? (object)Round(signals.RoomCleanliness.Value, 2)
                        : null
                }
            };
        }

        private static Dictionary<string, object> ColonyState(ClefColonySignals colony)
        {
            return new Dictionary<string, object>
            {
                ["colonists"] = colony.Colonists,
                ["idle"] = colony.Idle,
                ["meals"] = colony.Meals,
                ["medicine"] = colony.Medicine,
                ["steel"] = colony.Steel,
                ["components"] = colony.Components,
                ["silver"] = colony.Silver,
                ["wood"] = colony.Wood,
                ["food_nutrition"] = Round(colony.FoodNutrition, 1),
                ["power_stored"] = Round(colony.PowerStored, 0),
                ["power_gain"] = Round(colony.PowerGain, 0),
                ["bills"] = colony.Bills,
                ["growing_zones"] = colony.GrowingZones,
                ["stockpiles"] = colony.Stockpiles,
                ["construction"] = colony.Construction,
                ["designations"] = colony.Designations,
                ["hostiles"] = colony.Hostiles,
                ["research"] = colony.Research,
                ["research_progress"] = Round(colony.ResearchProgress, 2)
            };
        }

        private static double Round(float value, int digits)
        {
            return Math.Round((double)value, digits);
        }
    }
}
