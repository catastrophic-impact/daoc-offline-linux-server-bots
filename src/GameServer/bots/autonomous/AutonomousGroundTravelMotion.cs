using System;
using System.Numerics;

namespace DOL.GS
{
    /// <summary>Ground travel kinematics only. Does not choose or alter a route.</summary>
    public static class AutonomousGroundTravelMotion
    {
        // ObjectUpdate encodes vertical velocity in seven bits, in units of four.
        // Never normalize a tiny XY / large Z segment into an overflowing packet.
        public const float MaximumVerticalSpeed = 508;

        public readonly record struct Motion(Vector3 Velocity, double HorizontalSpeed, int ArrivalMilliseconds);

        public static bool Applies(GameNPC owner, bool fixedSpeed, GameObject followTarget) =>
            owner is GameBot { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false, IsPlayerLedGroup: false,
                IsOnStableMasterRoute: false, IsAttacking: false } &&
            !fixedSpeed && followTarget == null &&
            (owner.Flags & (GameNPC.eFlags.FLYING | GameNPC.eFlags.SWIMMING)) == 0;

        public static bool TryCalculate(Vector3 direction, short speed, out Motion motion)
        {
            motion = default;
            if (speed <= 0 || !float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z))
                return false;
            double horizontal = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y);
            double vertical = Math.Abs((double)direction.Z);
            if (horizontal == 0 && vertical == 0) return false;

            // Ordinary ground movement keeps its requested XY speed, including
            // short steps/height noise on visually flat ground. Pure vertical
            // moves keep their old speed, rather than dividing by zero.
            double distance = horizontal > 0.01 ? horizontal : Math.Sqrt(horizontal * horizontal + vertical * vertical);
            distance = Math.Max(distance, vertical * speed / MaximumVerticalSpeed);
            double scale = speed / distance;
            Vector3 velocity = new((float)(direction.X * scale), (float)(direction.Y * scale),
                Math.Clamp((float)(direction.Z * scale), -MaximumVerticalSpeed, MaximumVerticalSpeed));
            double wireSpeed = horizontal * scale;
            // Avoid 190.99999 -> 190 truncation in the NPC packet on flat ground.
            if (Math.Abs(wireSpeed - speed) < 0.001) wireSpeed = speed;
            int arrival = (int)Math.Clamp(Math.Ceiling(distance * 1000 / speed), 1, int.MaxValue);
            motion = new(velocity, wireSpeed, arrival);
            return true;
        }
    }
}
