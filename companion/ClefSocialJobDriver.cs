using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>Walk to the chosen colonist, chat once, and end.</summary>
    public class ClefSocialJobDriver : JobDriver
    {
        private Pawn Partner
        {
            get
            {
                if (job == null)
                {
                    return null;
                }
                return job.targetA.Thing as Pawn;
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnDowned(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch, false);
            yield return Toils_General.Wait(180, TargetIndex.None);
            yield return new Toil
            {
                initAction = delegate
                {
                    Pawn partner = Partner;
                    if (partner == null || !partner.Spawned || partner.health == null || partner.health.Dead)
                    {
                        return;
                    }
                    if (pawn.interactions != null)
                    {
                        pawn.interactions.TryInteractWith(partner, InteractionDefOf.Chitchat);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return Toils_General.Wait(120, TargetIndex.None);
        }
    }
}
