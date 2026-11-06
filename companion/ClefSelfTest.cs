using System;
using System.Collections.Generic;
using RimWorld;
using RustBridge;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// In-game smoke test for the companion. Runs once when the game is started with
    /// `-clefselftest`, after `-quicktest` generated a colony, and logs `[selftest] PASS/FAIL/SKIP`
    /// lines plus a summary. It never starts the model server and never waits for an answer.
    /// </summary>
    internal static class ClefSelfTest
    {
        private static readonly bool Enabled = HasFlag();
        private static bool finished;
        private static int startedTick = -1;
        private static int passed;
        private static int failed;
        private static int skipped;

        public static void TryRun()
        {
            if (!Enabled || finished)
            {
                return;
            }
            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null)
            {
                return;
            }
            if (Find.Maps == null || Find.Maps.Count == 0)
            {
                return;
            }
            Map map = Find.Maps[0];
            if (map == null || !map.IsPlayerHome || map.mapPawns == null
                || map.mapPawns.FreeColonistsSpawnedCount == 0)
            {
                return;
            }
            if (startedTick < 0)
            {
                startedTick = Find.TickManager.TicksGame;
                return;
            }
            if (Find.TickManager.TicksGame - startedTick < 120)
            {
                return;
            }
            finished = true;
            try
            {
                Run(map);
            }
            catch (Exception error)
            {
                Fail("selftest crashed", error.ToString());
            }
            Log.Message("[selftest] summary " + passed + " passed, " + failed + " failed, "
                + skipped + " skipped");
        }

        private static void Run(Map map)
        {
            Info("map " + map.Size.x + "x" + map.Size.z + ", "
                + map.mapPawns.FreeColonistsSpawnedCount + " colonist(s)");
            Check("companion ready", ClefRuntime.Ready);
            Check("respect schedule default", ClefRuntime.RespectSchedule);
            Check("ThinkTree PostMain", DefDatabase<ThinkTreeDef>.GetNamedSilentFail(
                "SmartColonists_ClefThinkTree") != null);
            Check("ThinkTree PreMain", DefDatabase<ThinkTreeDef>.GetNamedSilentFail(
                "SmartColonists_GatedThinkTree") != null);
            Check("social JobDriver def", DefDatabase<JobDef>.GetNamedSilentFail(
                "SmartColonists_ClefSocialize") != null);
            TestLabels();
            TestGateReset();

            ClefColonySignals colony = null;
            Guard("colony signals", () => { colony = ClefColonySignals.Build(map); });
            if (colony == null)
            {
                Fail("colony signals returned null", null);
                return;
            }
            Check("colony has colonists", colony.Colonists > 0);

            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            foreach (Pawn pawn in colonists)
            {
                TestPawn(pawn, colony);
            }
            if (colonists.Count > 0)
            {
                TestDrug(map, colonists[0], colony);
                TestScheduleGate(map, colonists[0]);
                TestGatedNode(colonists[0]);
            }
            else
            {
                Skip("per-pawn tests", "no free colonist");
            }
            if (ClefRuntime.Enabled)
            {
                string states = null;
                Guard("collect states", () => { states = ClefBridge.CollectStates(); });
                Check("collect states payload", !string.IsNullOrEmpty(states) && states != "null");
            }
            else
            {
                Skip("collect states", "mod disabled in settings");
            }
        }

        private static void TestPawn(Pawn pawn, ClefColonySignals colony)
        {
            string who = pawn.LabelShort;
            ClefSignalContext signals = null;
            Guard("signals " + who, () => { signals = ClefSignals.Build(pawn, colony); });
            if (signals == null)
            {
                Fail("signals returned null " + who, null);
                return;
            }

            ClefCandidatePools pools = null;
            Guard("candidates " + who, () => { pools = ClefCandidates.Build(pawn, true, signals, colony); });
            if (pools == null)
            {
                Fail("candidates returned null " + who, null);
                return;
            }
            Check("candidates idle " + who, pools.Idle.Count > 0);
            Check("option ids and descriptions " + who, CompleteAndUnique(pools));

            int oldMode = ClefRuntime.Mode;
            try
            {
                for (int mode = 0; mode <= 3; mode++)
                {
                    ClefRuntime.Mode = mode;
                    Dictionary<string, object> questions = null;
                    Guard("questions mode " + mode + " " + who, () =>
                    {
                        questions = ClefQuestions.Build(pawn, pools);
                    });
                    Check("questions non-empty mode " + mode + " " + who,
                        questions != null && questions.Count > 0);
                }
            }
            finally
            {
                ClefRuntime.Mode = oldMode;
            }

            Guard("idle option " + who, () =>
            {
                Check("idle builds no job", ClefJobs.Build(pawn, "idle") == null);
                Check("unknown option builds no job",
                    ClefJobs.Build(pawn, "not-an-action") == null);
            });

            int built = 0;
            Guard("job builders " + who, () => { built = CountJobs(pawn, pools); });
            Info("job builders " + who + ": " + built + " option(s) produced a job");

            Dictionary<string, object> state = null;
            Guard("state payload " + who, () =>
            {
                state = ClefStateBuilder.BuildColony(new List<Pawn> { pawn }, false);
            });
            Check("state payload " + who,
                state != null && state.ContainsKey("pawns") && state.ContainsKey("state")
                && state.ContainsKey("questions"));
        }

        private static void TestDrug(Map map, Pawn pawn, ClefColonySignals colony)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("SmokeleafJoint");
            if (def == null || pawn.drugs == null || pawn.drugs.CurrentPolicy == null)
            {
                Skip("drug candidate", "no SmokeleafJoint or no drug policy");
                return;
            }
            DrugPolicyEntry entry = pawn.drugs.CurrentPolicy[def];
            if (entry == null)
            {
                Skip("drug candidate", "SmokeleafJoint is not in the policy");
                return;
            }
            bool oldJoy = entry.allowedForJoy;
            float oldMood = entry.onlyIfMoodBelow;
            float oldJoyBelow = entry.onlyIfJoyBelow;
            Thing spawned = null;
            try
            {
                entry.allowedForJoy = true;
                entry.onlyIfMoodBelow = 1f;
                entry.onlyIfJoyBelow = 1f;
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(pawn.Position, map, 3, null);
                if (!cell.IsValid)
                {
                    Skip("drug candidate", "no close walkable cell");
                    return;
                }
                spawned = GenSpawn.Spawn(def, cell, map, WipeMode.Vanish);
                Check("drug spawned", spawned != null && spawned.Spawned);

                ClefSignalContext signals = ClefSignals.Build(pawn, colony);
                ClefCandidatePools pools = ClefCandidates.Build(pawn, false, signals, colony);
                string drugId = null;
                foreach (ClefOption option in pools.Needs)
                {
                    if (option.Id.StartsWith("drug:"))
                    {
                        drugId = option.Id;
                        break;
                    }
                }
                Check("drug candidate offered", drugId != null);
                if (drugId != null)
                {
                    Check("drug job built", ClefJobs.Build(pawn, drugId) != null);
                }
            }
            finally
            {
                entry.allowedForJoy = oldJoy;
                entry.onlyIfMoodBelow = oldMood;
                entry.onlyIfJoyBelow = oldJoyBelow;
                if (spawned != null && !spawned.Destroyed)
                {
                    spawned.Destroy(DestroyMode.Vanish);
                }
            }
        }

        private static void TestScheduleGate(Map map, Pawn pawn)
        {
            ClefScheduleGate.Reset();
            ClefScheduleGate.Mark(pawn);
            Check("gate marks a pawn", ClefScheduleGate.IsGated(pawn));
            ClefScheduleGate.Clear(pawn);
            Check("gate clears a pawn", !ClefScheduleGate.IsGated(pawn));

            if (pawn.timetable == null)
            {
                Skip("schedule gate", "no timetable");
                return;
            }
            int hour = GenLocalDate.HourOfDay(map);
            if (pawn.timetable.GetAssignment(hour) == TimeAssignmentDefOf.Sleep)
            {
                Skip("schedule gate", "the current hour is Sleep");
                return;
            }
            int next = (hour + 1) % 24;
            TimeAssignmentDef old = pawn.timetable.GetAssignment(next);
            IntVec3 far = new IntVec3(
                pawn.Position.x < map.Size.x / 2 ? map.Size.x - 2 : 2,
                0,
                pawn.Position.z < map.Size.z / 2 ? map.Size.z - 2 : 2);
            try
            {
                pawn.timetable.SetAssignment(next, TimeAssignmentDefOf.Sleep);
                bool near = ClefScheduleGate.FitsSchedule(
                    pawn,
                    new LocalTargetInfo(pawn.Position),
                    LocalTargetInfo.Invalid,
                    "Hauling");
                bool farFits = ClefScheduleGate.FitsSchedule(
                    pawn,
                    new LocalTargetInfo(far),
                    LocalTargetInfo.Invalid,
                    "Hauling");
                Check("near work fits before sleep", near);
                Check("far work refused before sleep", !farFits);
            }
            finally
            {
                pawn.timetable.SetAssignment(next, old);
            }
        }

        private static void TestGatedNode(Pawn pawn)
        {
            ClefScheduleGate.Mark(pawn);
            try
            {
                Guard("gated think node", () =>
                {
                    var node = new ClefGatedThinkNode();
                    node.TryIssueJobPackage(pawn, new JobIssueParams());
                });
            }
            finally
            {
                ClefScheduleGate.Clear(pawn);
            }
        }

        private static void TestLabels()
        {
            CheckLabel("mode_status");
            CheckLabel("decisions_status");
            CheckLabel("deps_status");
            CheckLabel("server_status");
        }

        private static void CheckLabel(string id)
        {
            string value = null;
            Guard("label " + id, () =>
            {
                value = Companion.Request(
                    "ColonistAutonomy",
                    "settings_label",
                    new Dictionary<string, object> { ["id"] = id });
            });
            Check("label " + id + " non-empty", !string.IsNullOrEmpty(value));
        }

        private static void TestGateReset()
        {
            Guard("reset gate", () =>
            {
                string answer = ClefBridge.ResetGate();
                Check("reset gate answers", answer == "reset");
            });
        }

        private static int CountJobs(Pawn pawn, ClefCandidatePools pools)
        {
            int built = 0;
            foreach (ClefOption option in AllOptions(pools))
            {
                if (ClefJobs.Build(pawn, option.Id) != null)
                {
                    built++;
                }
            }
            return built;
        }

        private static bool CompleteAndUnique(ClefCandidatePools pools)
        {
            var lists = new[]
            {
                pools.Needs,
                pools.WorkTypes,
                pools.Work,
                pools.Social,
                pools.Threat,
                pools.Idle,
            };
            foreach (List<ClefOption> pool in lists)
            {
                var seen = new HashSet<string>();
                foreach (ClefOption option in pool)
                {
                    if (string.IsNullOrEmpty(option.Id) || string.IsNullOrEmpty(option.Description))
                    {
                        return false;
                    }
                    if (!seen.Add(option.Id))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static IEnumerable<ClefOption> AllOptions(ClefCandidatePools pools)
        {
            foreach (ClefOption option in pools.Needs)
            {
                yield return option;
            }
            foreach (ClefOption option in pools.WorkTypes)
            {
                yield return option;
            }
            foreach (ClefOption option in pools.Work)
            {
                yield return option;
            }
            foreach (ClefOption option in pools.Social)
            {
                yield return option;
            }
            foreach (ClefOption option in pools.Threat)
            {
                yield return option;
            }
            foreach (ClefOption option in pools.Idle)
            {
                yield return option;
            }
        }

        private static bool HasFlag()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(
                    arg.TrimStart('-'),
                    "clefselftest",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Fail(what, error.GetType().Name + ": " + error.Message);
            }
        }

        private static void Check(string what, bool ok)
        {
            if (ok)
            {
                Pass(what);
            }
            else
            {
                Fail(what, null);
            }
        }

        private static void Pass(string what)
        {
            passed++;
            Log.Message("[selftest] PASS  " + what);
        }

        private static void Fail(string what, string detail)
        {
            failed++;
            Log.Error("[selftest] FAIL  " + what
                + (string.IsNullOrEmpty(detail) ? "" : ": " + detail));
        }

        private static void Skip(string what, string why)
        {
            skipped++;
            Log.Warning("[selftest] SKIP  " + what + ": " + why);
        }

        private static void Info(string message)
        {
            Log.Message("[selftest] INFO  " + message);
        }
    }
}
