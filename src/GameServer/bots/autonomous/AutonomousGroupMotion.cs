using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DOL.GS
{
    /// <summary>
    /// Smooth group travel, ported from the stefanrows/OfflineDAoC fork (0.102.0, "smooth group
    /// travel"). Followers used to walk to a copy of their slot taken when the order was issued,
    /// stop there, and think again seconds later: a stop-go line along the leader's old route.
    /// Now a follower steers toward a slot around where the leader is about to be (0.8 s ahead),
    /// keeps walking while the leader walks, and re-steers only when the slot has really moved (48
    /// units). The fork's speed matching (the leader's current speed with a +/-4% stride) is not
    /// used: it ordered followers down to a walk whenever the leader was momentarily slow (starting,
    /// turning, arriving). Followers always walk at their own full speed; only the game slows them,
    /// and AutonomousGroupPace still lets them run up to 25% faster while behind.
    /// </summary>
    public static class AutonomousGroupMotion
    {
        private sealed class Order
        {
            public Vector3 Target;
            public long IssuedTick;
            public short Speed;
        }

        private static readonly ConditionalWeakTable<GameBot, Order> Orders = new();

        /// <summary>How far ahead of the leader followers aim, in seconds of its travel.</summary>
        public const double LookaheadSeconds = 0.8;
        public const float ResteerDistance = 48;

        /// <summary>The leader's position a moment ahead along its travel direction.</summary>
        public static Vector3 PredictLeader(GameLiving leader)
        {
            Vector3 here = new(leader.X, leader.Y, leader.Z);
            if (!leader.IsMoving || leader.CurrentSpeed <= 0)
                return here;
            Point2D ahead = leader.GetPointFromHeading(leader.Heading, (int)(leader.CurrentSpeed * LookaheadSeconds));
            return new Vector3(ahead.X, ahead.Y, leader.Z);
        }

        /// <summary>A follower of a moving autonomous group leader (gets the faster travel AI cadence).</summary>
        public static bool GroupTraveling(GameBot bot) =>
            bot?.IsAutonomousWorldBot == true && !bot.IsPlayerLedGroup &&
            bot.Group?.LivingLeader is GameBot leader && leader != bot && leader.IsAlive && leader.IsMoving &&
            leader.CurrentRegionID == bot.CurrentRegionID;

        /// <summary>Re-steer only when the slot moved noticeably, the bot stopped, or its speed must change.</summary>
        public static bool ShouldResteer(GameBot bot, Vector3 target, short speed, long now)
        {
            Order order = Orders.GetOrCreateValue(bot);
            lock (order)
            {
                bool fresh = order.IssuedTick == 0 || !bot.IsMoving ||
                             Vector3.Distance(order.Target, target) > ResteerDistance ||
                             Math.Abs(order.Speed - speed) > Math.Max(8, order.Speed / 10) && now - order.IssuedTick > 600;
                if (!fresh) return false;
                order.Target = target;
                order.Speed = speed;
                order.IssuedTick = now;
                return true;
            }
        }

        /// <summary>
        /// Walk with a moving leader: aim at the slot around its predicted spot,
        /// never hard-stop while it walks, at the follower's own full speed.
        /// </summary>
        public static void FollowMovingLeader(GameBot bot, GameLiving leader, Vector3 slot)
        {
            short speed = bot.MaxSpeed;
            if (ShouldResteer(bot, slot, speed, GameLoop.GameLoopTime))
                bot.PathTo(slot, speed);
        }
    }
}
