using RimWorld;
using Verse;

namespace SmartColonistsCompanion
{
    internal static class ClefDefs
    {
        public const string SocialJobName = "SmartColonists_ClefSocialize";

        public static JobDef SocialJob;

        public static bool Validate()
        {
            SocialJob = DefDatabase<JobDef>.GetNamedSilentFail(SocialJobName);
            if (SocialJob == null)
            {
                Log.Error("[ColonistAutonomy] JobDef '" + SocialJobName + "' is missing; social decisions are disabled.");
                return false;
            }
            if (InteractionDefOf.Chitchat == null)
            {
                Log.Error("[ColonistAutonomy] InteractionDefOf.Chitchat is missing; social decisions are disabled.");
                return false;
            }
            return true;
        }
    }
}
