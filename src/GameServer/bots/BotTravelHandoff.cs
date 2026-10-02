using System.Numerics;

namespace DOL.GS
{
    public static class BotTravelHandoff
    {
        // Only extend an already-active, progressing partial corridor. Logical
        // goal completion, blocked routes and combat brain scheduling are unchanged.
        public static bool CanContinuePartial(PathfindingStatus status, Vector3 origin, Vector3 position, Vector3 target) =>
            status == PathfindingStatus.PartialPathFound &&
            Vector3.DistanceSquared(origin, position) >= 16 * 16 &&
            Vector3.DistanceSquared(position, target) > 16 * 16;
    }
}
