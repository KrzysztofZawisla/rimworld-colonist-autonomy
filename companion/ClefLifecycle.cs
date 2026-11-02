using System;
using RustBridge;
using UnityEngine;
using Verse;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// The game's GameComponent only exists while a colony is loaded, so it cannot see a return to
    /// the main menu. This persistent component does exactly one thing: tell Rust to stop the
    /// server it auto-started when the game leaves Playing, or when the application quits.
    /// </summary>
    public sealed class ClefLifecycle : MonoBehaviour
    {
        private static bool installed;
        private static bool stopped;

        public static void Install()
        {
            if (installed)
            {
                return;
            }
            installed = true;
            var host = new GameObject("ColonistAutonomyLifecycle");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<ClefLifecycle>();
            AppDomain.CurrentDomain.ProcessExit += (sender, args) => StopServer();
        }

        private void Update()
        {
            if (Current.ProgramState == ProgramState.Playing)
            {
                stopped = false;
                return;
            }
            StopServer();
        }

        private void OnApplicationQuit()
        {
            StopServer();
        }

        private static void StopServer()
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            try
            {
                Companion.Send("ColonistAutonomy", "clef.stop_server", null);
            }
            catch (Exception error)
            {
                Log.Warning("[ColonistAutonomy] cannot stop the server: " + error);
            }
        }
    }
}
