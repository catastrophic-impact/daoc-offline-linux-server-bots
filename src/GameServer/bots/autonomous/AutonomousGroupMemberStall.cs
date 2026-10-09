using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// A group leader waits for members who trail too far behind. A member that makes no
    /// progress toward the leader for 45 seconds no longer holds the whole group: the leader
    /// moves on (the member keeps following and gets a fresh formation point as the leader
    /// moves). Seen in Iarnwood on 2026-10-05: six groups waited forever for one member each,
    /// all standing at the same spot. The first time a member is released this way its full
    /// movement state is logged (AUTONOMOUS_GROUP_MEMBER_STALLED) so the cause can be found.
    /// </summary>
    public static class AutonomousGroupMemberStall
    {
        public const long StallMilliseconds = 45_000;
        public const float ProgressUnits = 100f;

        private static readonly Logger Log = LoggerManager.Create(typeof(AutonomousGroupMemberStall));

        private sealed class Trail
        {
            public float AnchorX, AnchorY;
            public long Since;
            public bool Logged;
        }

        private static readonly ConditionalWeakTable<GameBot, Trail> Trails = new();

        /// <summary>Pure rule: the member itself moved less than 100 units in 45 seconds.</summary>
        public static bool IsStalled(float movedSinceAnchor, long sinceTick, long now) =>
            sinceTick > 0 && movedSinceAnchor < ProgressUnits && now - sinceTick >= StallMilliseconds;

        /// <summary>True when this trailing member should no longer hold the group.</summary>
        public static bool Release(GameBot member, GameBot leader, float distance, int cohesionRadius, long now)
        {
            if (member == null || leader == null || member == leader)
                return false;
            Trail trail = Trails.GetOrCreateValue(member);
            lock (trail)
            {
                float moved = System.MathF.Sqrt((member.X - trail.AnchorX) * (member.X - trail.AnchorX) +
                    (member.Y - trail.AnchorY) * (member.Y - trail.AnchorY));
                if (distance <= cohesionRadius || trail.Since == 0 || moved >= ProgressUnits)
                {
                    trail.AnchorX = member.X;
                    trail.AnchorY = member.Y;
                    trail.Since = now;
                    if (distance <= cohesionRadius)
                        trail.Logged = false;
                    return false;
                }
                if (!IsStalled(moved, trail.Since, now))
                    return false;
                if (!trail.Logged)
                {
                    trail.Logged = true;
                    var brain = member.Brain as BotBrain;
                    Log.Warn($"AUTONOMOUS_GROUP_MEMBER_STALLED leader={leader.Name} " +
                             $"member={member.Name} id={member.DatabaseID} class=\"{member.ClassName}\" distance={distance:0} " +
                             $"region={member.CurrentRegionID} position={member.X},{member.Y},{member.Z} " +
                             $"moving={member.IsMoving} speed={member.movementComponent?.CurrentSpeed} maxSpeed={member.MaxSpeed} " +
                             $"casting={member.IsCasting} spell=\"{member.castingComponent?.SpellHandler?.Spell?.Name}\" " +
                             $"inCombat={member.InCombat} aggro={brain?.HasAggro} resting={member.IsRecoveryResting} " +
                             $"crowdControlled={member.IsCrowdControlled} fsm={brain?.FSM?.GetCurrentState()?.StateType} " +
                             $"activity=\"{member.PersistentRecord?.Activity}\"; the leader moves on without waiting");
                }
                return true;
            }
        }
    }
}
