using System;
using System.Collections.Generic;
using System.Numerics;

namespace DOL.GS
{
    /// <summary>Party-sized posts; existing eight-person formations fill each post.</summary>
    public static class RealmRaidFormation
    {
        public static bool TryResolve(IPathfindingMgr nav, Zone zone, Vector3 center, int slot,
            IReadOnlyCollection<Vector3> occupied, out Vector3 position)
        {
            position = default;
            if (slot < 0 || slot >= RealmRaidRecruitmentPolicy.MaximumParties || nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
            bool epicInterior = zone.ID is 60 or 160 or 191;
            for (int attempt = 0; attempt < (epicInterior ? 22 : 6); attempt++)
            {
                bool compact = attempt >= 6;
                double angle = (slot * 137.5 + attempt * (compact ? 47.5 : 30)) * Math.PI / 180;
                float radius = compact ? 100 + (attempt - 6 + slot) % 5 * 80 :
                    slot == 0 && attempt == 0 ? 0 : 140 * MathF.Sqrt(slot + 1) + attempt % 2 * 70;
                Vector3 raw = center + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
                Vector3? floor = nav.GetClosestPoint(zone, raw, 48, 48, 128, nav.DefaultFilters);
                if (!floor.HasValue || Math.Abs(floor.Value.Z - center.Z) > 128) continue;
                bool overlaps = false;
                foreach (Vector3 other in occupied)
                    if (Vector3.DistanceSquared(other, floor.Value) < (compact ? 80 * 80 : 120 * 120)) { overlaps = true; break; }
                if (overlaps || !nav.HasLineOfSight(zone, center, floor.Value, nav.DefaultFilters) ||
                    !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, center, floor.Value) ||
                    !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, center)) continue;
                position = floor.Value;
                return true;
            }
            // No raw offset or teleport fallback through a wall. The party
            // stays at its previous validated post until room becomes available.
            return false;
        }
    }
}
