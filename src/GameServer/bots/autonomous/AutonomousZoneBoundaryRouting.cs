using System;
using System.Numerics;

namespace DOL.GS
{
    public static class AutonomousZoneBoundaryRouting
    {
        public readonly record struct Step(Vector3 Inside, Vector3 Outside);

        public static bool TryResolveHeights(Vector3 inside, Vector3 outside,
            Func<Vector3, Vector3?> snapInside, Func<Vector3, Vector3?> snapOutside, out Step step)
        {
            step = default;
            Vector3? a = snapInside(inside);
            Vector3? b = snapOutside(outside);
            if (!a.HasValue || !b.HasValue || !IsFinite(a.Value) || !IsFinite(b.Value))
                return false;
            // A short seam crossing may not jump to another floor or bridge a
            // cliff/hole. Snap vertically to terrain, but keep XY near the seam.
            if (Vector2.Distance(new(a.Value.X, a.Value.Y), new(inside.X, inside.Y)) > 96 ||
                Vector2.Distance(new(b.Value.X, b.Value.Y), new(outside.X, outside.Y)) > 96 ||
                Vector3.Distance(a.Value, b.Value) > 240 || Math.Abs(a.Value.Z - b.Value.Z) > 128)
                return false;
            step = new(a.Value, b.Value);
            return true;
        }

        private static bool IsFinite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    }
}
