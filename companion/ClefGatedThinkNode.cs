using Verse;
using Verse.AI;

namespace SmartColonistsCompanion
{
    /// <summary>
    /// Runs before vanilla work when the schedule gate refused this pawn's job. The model picks a
    /// local action instead, so a refused far job does not leave the colonist standing idle.
    /// </summary>
    public class ClefGatedThinkNode : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!ClefRuntime.Ready || !ClefRuntime.Enabled || !ClefRuntime.RespectSchedule)
            {
                return null;
            }
            if (!ClefScheduleGate.IsGated(pawn))
            {
                return null;
            }
            Job job = ClefThinkNode.Decide(
                pawn,
                candidate => ClefScheduleGate.AllowsJob(pawn, candidate));
            if (job != null)
            {
                ClefScheduleGate.Clear(pawn);
            }
            return job;
        }
    }
}
