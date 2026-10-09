using System;
using System.Runtime.CompilerServices;

namespace DOL.GS
{
    /// <summary>
    /// Group travel pace. Bots never slow down on their own: the leader moves at full speed.
    /// Followers behind their formation spot run up to 25% faster until they catch up, so the
    /// party moves as one unit. Only a member far behind (more than 1,500 units) makes the
    /// leader stop, and it starts again once every member is back within 900. (The Oct 5
    /// version walked the leader at 55% speed while a member trailed, which looked like bots
    /// switching to a walk for no reason.)
    /// </summary>
    public static class AutonomousGroupPace
    {
        public const int CohesionRadius = 500;
        public const int TrailRadius = 1500;
        // Once stopped for a far member, the leader goes again when everyone is within this.
        public const int ResumeRadius = 900;
        public const float CatchUpStartDistance = 120f;
        public const float CatchUpFullDistance = 1020f;
        public const float MaximumCatchUpBonus = 0.25f;
        public const long CatchUpHoldMilliseconds = 2_500;

        public enum Decision { FullSpeed, Wait }

        public static Decision Decide(float farthestMember, bool waiting) =>
            farthestMember <= (waiting ? ResumeRadius : TrailRadius) ? Decision.FullSpeed : Decision.Wait;

        /// <summary>Speed multiplier for a follower this far from its formation spot (1.0 to 1.25).</summary>
        public static float CatchUpFactor(float distanceFromFormation) =>
            1f + Math.Clamp((distanceFromFormation - CatchUpStartDistance) /
                (CatchUpFullDistance - CatchUpStartDistance), 0f, 1f) * MaximumCatchUpBonus;

        private sealed class Boost { public float Factor; public long Until; }
        private static readonly ConditionalWeakTable<GameBot, Boost> Boosts = new();

        public static void CatchUp(GameBot follower, float distanceFromFormation, long now)
        {
            if (follower == null) return;
            Boost boost = Boosts.GetOrCreateValue(follower);
            boost.Factor = CatchUpFactor(distanceFromFormation);
            boost.Until = now + CatchUpHoldMilliseconds;
        }

        /// <summary>Applied by the NPC movement component; never below the normal speed, off in combat.</summary>
        public static short Apply(GameBot bot, short speed)
        {
            if (bot == null || speed <= 0 || !Boosts.TryGetValue(bot, out Boost boost) ||
                GameLoop.GameLoopTime >= boost.Until || boost.Factor <= 1f ||
                bot.InCombat || bot.IsAttacking || bot.IsOnStableMasterRoute)
                return speed;
            return (short)Math.Min(short.MaxValue, (int)Math.Round(speed * boost.Factor));
        }
    }
}
