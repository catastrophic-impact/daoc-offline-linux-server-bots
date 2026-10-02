using CEM.Utils;
using OpenDAoC.Pathing;
using Numerics = System.Numerics;

namespace CEM.World
{
    /// <summary>
    /// Two-step ladder placement:
    /// 1. Free-side floor discovery (+/- ThinAxis, Z sample, L/C/R columns, merge).
    /// 2. Emit 3 links per hop: landing → pad → pad → landing (pads near ladder on free face).
    /// </summary>
    internal static class LadderLinkPlacer
    {
        // Outward distances from the ladder's center plane to probe for walkable landings.
        // Closer offsets help recessed/alcove ladders; farther ones cover freestanding approach floors.
        // Prefer the closest valid snap so tight front landings win over distant false positives.
        private static readonly float[] LandingOffsets = [12f, 24f, 48f, 60f, 72f, 84f, 96f, 108f, 120f];
        private static readonly float[] RoofFrontOffsets = LandingOffsets;

        // Distance left/right from the center column to cast secondary probes.
        // Ensures wide ladders allow AI to mount from the sides.
        private const float COLUMN_OFFSET = 48f;

        private static readonly float[] ColumnOffsets = [-COLUMN_OFFSET, 0f, COLUMN_OFFSET];

        // The dimensions of the bounding box given to Detour's FindClosestPointInBox.
        // Depth: How far "forward/backward" to search around the probe point.
        private const float BOX_DEPTH = 36f;
        // Half-Width: How far left/right to search.
        private const float BOX_HALF_WIDTH = 20f;
        // Half-Height: How far up/down Detour is allowed to look for a poly.
        private const float BOX_HALF_HEIGHT = 24f;

        // Distance behind the ladder plane that a snapped navmesh point is allowed to be.
        // Uses a half-space check to strictly prevent generating links through solid walls.
        private const float HALF_SPACE_TOLERANCE = 4f;

        // Absolute maximum 3D distance between our ideal probe point and Detour's snapped point.
        // If the closest navmesh poly is further than this, the probe is discarded.
        private const float MAX_SNAP_DISTANCE = 64f;

        // Roof snaps: loft deck Z often differs from Top.Z; mesh can sit farther out.
        private const float ROOF_BOX_HALF_HEIGHT = 48f;
        private const float ROOF_MAX_SNAP_DISTANCE = 96f;
        private const float ROOF_HALF_SPACE_TOLERANCE = 16f;

        private const float Z_SAMPLE_STEP = 48f;
        private const float Z_MERGE_DISTANCE = 72f;

        // Band around Bottom/Top anchors treated as climb extremities (anti-merge).
        private const float EXTREMITY_Z_BAND = 48f;

        // If a direct vertical column connection fails, allow a diagonal jump to another column up to this horizontal distance.
        private const float CROSS_COLUMN_FALLBACK_MAX_HORIZ = 72f;

        public const float PAD_WIDTH = 96f;
        public const float PAD_DEPTH = 96f;
        public const float WALL_CLEARANCE = 24f;

        private static readonly EDtPolyFlags[] WalkFilters =
        [
            EDtPolyFlags.All ^ EDtPolyFlags.Disabled ^ EDtPolyFlags.Jump,
            0
        ];

        public sealed class PlacementResult
        {
            public List<OffMeshLink> Links { get; } = new();
            public List<LadderPad> Pads { get; } = new();
        }

        public static PlacementResult Place(string navMeshPath, IEnumerable<LadderDefinition> ladders)
        {
            PlacementResult result = new();

            if (!DetourNavMesh.TryProbeNativeLibrary(out Exception? probeError))
            {
                Log.Error($"Detour library not available for ladder placement: {probeError?.Message}");
                return result;
            }

            using DetourNavMesh? mesh = DetourNavMesh.TryLoad(navMeshPath);
            if (mesh == null)
            {
                Log.Error($"Failed to load navmesh for ladder placement: {navMeshPath}");
                return result;
            }

            using DetourNavMeshQuery query = mesh.CreateQuery();

            foreach (LadderDefinition ladder in ladders)
            {
                int before = result.Links.Count;
                PlaceLadder(query, ladder, result);
                int emitted = result.Links.Count - before;
                if (emitted > 0)
                    Log.Normal($"Ladder '{ladder.Name}': emitted {emitted} off-mesh link(s)");
            }

            return result;
        }

        private static void PlaceLadder(
            DetourNavMeshQuery query,
            LadderDefinition ladder,
            PlacementResult result)
        {
            List<Numerics.Vector2> freeSides = ResolveFreeSides(query, ladder);
            if (freeSides.Count == 0)
            {
                Log.Warn($"Ladder '{ladder.Name}': no free-side walkable mesh found; skipping");
                return;
            }

            int totalHops = 0;
            foreach (Numerics.Vector2 outward in freeSides)
            {
                int hops = PlaceLadderSide(query, ladder, outward, result);
                totalHops += hops;
                if (hops == 0)
                {
                    Log.Debug(
                        $"Ladder '{ladder.Name}' side ({outward.X:F2},{outward.Y:F2}): no hops " +
                        "(floors < 2 after sample/merge)");
                }
            }

            if (totalHops == 0)
            {
                Log.Warn(
                    $"Ladder '{ladder.Name}': free side(s) found but no floor pair produced a hop " +
                    $"({freeSides.Count} free side(s))");
            }
        }

        private static List<Numerics.Vector2> ResolveFreeSides(DetourNavMeshQuery query, LadderDefinition ladder)
        {
            Numerics.Vector2 thin = ToNum(ladder.ThinAxis);
            if (thin.LengthSquared() < 1e-8f)
                thin = Numerics.Vector2.UnitX;
            else
                thin = Numerics.Vector2.Normalize(thin);

            Numerics.Vector2 center = ToNum(ladder.CenterXY);

            // Probe several heights so intermediate recess floors still mark a side free.
            // ThinAxis sign from export is arbitrary; treat + and - symmetrically.
            float[] probeZs = BuildFreeSideProbeHeights(ladder);
            bool pos = ProbeSide(query, ladder, thin, probeZs);
            bool neg = ProbeSide(query, ladder, -thin, probeZs);

            List<Numerics.Vector2> sides = new();
            if (pos)
                sides.Add(thin);
            if (neg)
                sides.Add(-thin);

            if (sides.Count == 0)
            {
                // ThinAxis sign is arbitrary; try both faces (may still fail later at snap).
                sides.Add(thin);
                sides.Add(-thin);
                Log.Warn($"Ladder '{ladder.Name}': free-side probe failed both sides; trying +/- thinAxis");
            }

            return sides;
        }

        private static float[] BuildFreeSideProbeHeights(LadderDefinition ladder)
        {
            // Extremities plus mid-height: enough to catch single-floor recess landings without
            // running the full placement sample grid twice.
            float mid = (ladder.MinZ + ladder.MaxZ) * 0.5f;
            HashSet<float> heights =
            [
                ladder.Bottom.Z,
                ladder.Top.Z,
                ladder.MinZ,
                ladder.MaxZ,
                mid,
            ];
            return heights.OrderBy(z => z).ToArray();
        }

        private static bool ProbeSide(
            DetourNavMeshQuery query,
            LadderDefinition ladder,
            Numerics.Vector2 outward,
            float[] probeZs)
        {
            foreach (float z in probeZs)
            {
                Numerics.Vector2 centerAtZ = GetLadderCenterAtZ(ladder, z);
                if (TrySnapLanding(query, centerAtZ, outward, tangentOffset: 0f, z).HasValue)
                    return true;
            }

            return false;
        }

        private static int PlaceLadderSide(
            DetourNavMeshQuery query,
            LadderDefinition ladder,
            Numerics.Vector2 outward,
            PlacementResult result)
        {
            Numerics.Vector2 tangent = ResolveTangent(ladder, outward);
            List<float> sampleZs = BuildSampleHeights(ladder);

            List<Numerics.Vector3?[]> floors = new();

            foreach (float z in sampleZs)
            {
                Numerics.Vector2 centerAtZ = GetLadderCenterAtZ(ladder, z);
                Numerics.Vector3?[] columns = SampleColumnsAtZ(query, centerAtZ, outward, tangent, z, false);
                if (HasAny(columns))
                    floors.Add(columns);
            }

            // Roof tops: deck often misses standard front snaps / sits past 100 GU / different Z.
            if (!HasFloorNearZ(floors, ladder.Top.Z, EXTREMITY_Z_BAND))
            {
                Numerics.Vector2 topCenter = GetLadderCenterAtZ(ladder, ladder.Top.Z);
                if (TrySampleRoofTopFloor(query, ladder, topCenter, outward, tangent, out Numerics.Vector3?[] roofFloor))
                {
                    floors.Add(roofFloor);
                    Log.Debug(
                        $"Ladder '{ladder.Name}' side ({outward.X:F2},{outward.Y:F2}): " +
                        "recovered roof-top landing");
                }
            }

            floors = MergeFloors(floors, ladder);

            if (floors.Count < 2)
            {
                Log.Debug(
                    $"Ladder '{ladder.Name}' side ({outward.X:F2},{outward.Y:F2}): " +
                    $"only {floors.Count} landing floor(s); need >= 2");
                return 0;
            }

            return EmitThreeLinkHops(ladder, outward, tangent, floors, result);
        }

        private static Numerics.Vector3?[] SampleColumnsAtZ(
            DetourNavMeshQuery query,
            Numerics.Vector2 origin,
            Numerics.Vector2 outward,
            Numerics.Vector2 tangent,
            float z,
            bool roofMode)
        {
            Numerics.Vector3?[] columns = new Numerics.Vector3?[ColumnOffsets.Length];
            for (int c = 0; c < ColumnOffsets.Length; c++)
            {
                float s = ColumnOffsets[c];
                Numerics.Vector3? hit = roofMode
                    ? TrySnapLandingRoof(query, origin, outward, s, z, tangent)
                    : TrySnapLanding(query, origin, outward, s, z, tangent);
                if (hit.HasValue)
                    columns[c] = hit.Value;
            }

            return columns;
        }

        private static bool HasAny(Numerics.Vector3?[] columns)
        {
            foreach (Numerics.Vector3? c in columns)
            {
                if (c.HasValue)
                    return true;
            }

            return false;
        }

        private static bool HasFloorNearZ(List<Numerics.Vector3?[]> floors, float z, float band)
        {
            foreach (Numerics.Vector3?[] floor in floors)
            {
                if (Math.Abs(AverageZ(floor) - z) <= band)
                    return true;
            }

            return false;
        }

        private static bool TrySampleRoofTopFloor(
            DetourNavMeshQuery query,
            LadderDefinition ladder,
            Numerics.Vector2 center,
            Numerics.Vector2 outward,
            Numerics.Vector2 tangent,
            out Numerics.Vector3?[] roofFloor)
        {
            roofFloor = new Numerics.Vector3?[ColumnOffsets.Length];

            Numerics.Vector2 topXY = new(ladder.Top.X, ladder.Top.Y);
            Numerics.Vector2[] origins = [topXY, center];
            float[] zs =
            [
                ladder.Top.Z,
                ladder.MaxZ,
                ladder.Top.Z + 16f,
                ladder.Top.Z - 16f,
                ladder.Top.Z + 32f,
                ladder.Top.Z - 32f,
            ];

            Numerics.Vector2[] directions =
            [
                outward,
                -outward,
                tangent,
                -tangent,
            ];

            // Lean direction (bottom -> top XY). Roofs often open past the climb end.
            Numerics.Vector2 lean = topXY - new Numerics.Vector2(ladder.Bottom.X, ladder.Bottom.Y);
            if (lean.LengthSquared() > 1e-4f)
            {
                lean = Numerics.Vector2.Normalize(lean);
                directions = [outward, lean, -outward, tangent, -tangent, -lean];
            }

            foreach (float z in zs.Distinct())
            {
                foreach (Numerics.Vector2 origin in origins)
                {
                    foreach (Numerics.Vector2 dir in directions)
                    {
                        Numerics.Vector3?[] columns = new Numerics.Vector3?[ColumnOffsets.Length];
                        bool any = false;
                        for (int c = 0; c < ColumnOffsets.Length; c++)
                        {
                            Numerics.Vector3? hit = TrySnapLandingRoof(
                                query, origin, dir, ColumnOffsets[c], z, tangent);
                            if (hit.HasValue)
                            {
                                columns[c] = hit.Value;
                                any = true;
                            }
                        }

                        if (any)
                        {
                            roofFloor = columns;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static Numerics.Vector2 ResolveTangent(LadderDefinition ladder, Numerics.Vector2 outward)
        {
            Numerics.Vector2 tangent = ToNum(ladder.Tangent);
            if (tangent.LengthSquared() < 1e-8f)
                tangent = new(-outward.Y, outward.X);
            else
                tangent = Numerics.Vector2.Normalize(tangent);

            float proj = Numerics.Vector2.Dot(tangent, outward);
            tangent -= outward * proj;
            if (tangent.LengthSquared() > 1e-8f)
                return Numerics.Vector2.Normalize(tangent);

            return new(-outward.Y, outward.X);
        }

        private static List<float> BuildSampleHeights(LadderDefinition ladder)
        {
            HashSet<float> heights =
            [
                ladder.MinZ,
                ladder.MaxZ,
                ladder.Bottom.Z,
                ladder.Top.Z,
                .. ladder.SeedHeights,
            ];

            for (float z = ladder.MinZ; z <= ladder.MaxZ; z += Z_SAMPLE_STEP)
                heights.Add(z);

            return heights.OrderBy(z => z).ToList();
        }

        private static Numerics.Vector3? TrySnapLanding(
            DetourNavMeshQuery query,
            Numerics.Vector2 center,
            Numerics.Vector2 outward,
            float tangentOffset,
            float z,
            Numerics.Vector2? tangent = null)
        {
            return TrySnapLandingWith(
                query, center, outward, tangentOffset, z, tangent,
                LandingOffsets, BOX_HALF_HEIGHT, MAX_SNAP_DISTANCE, HALF_SPACE_TOLERANCE);
        }

        private static Numerics.Vector3? TrySnapLandingRoof(
            DetourNavMeshQuery query,
            Numerics.Vector2 center,
            Numerics.Vector2 outward,
            float tangentOffset,
            float z,
            Numerics.Vector2? tangent = null)
        {
            return TrySnapLandingWith(
                query, center, outward, tangentOffset, z, tangent,
                RoofFrontOffsets, ROOF_BOX_HALF_HEIGHT, ROOF_MAX_SNAP_DISTANCE, ROOF_HALF_SPACE_TOLERANCE);
        }

        private static Numerics.Vector3? TrySnapLandingWith(
            DetourNavMeshQuery query,
            Numerics.Vector2 center,
            Numerics.Vector2 outward,
            float tangentOffset,
            float z,
            Numerics.Vector2? tangent,
            float[] offsets,
            float boxHalfHeight,
            float maxSnapDistance,
            float halfSpaceTolerance)
        {
            Numerics.Vector2 t = tangent ?? Numerics.Vector2.Zero;

            // Offsets are sorted nearest-first; return the first valid snap.
            foreach (float landingOffset in offsets)
            {
                Numerics.Vector3 sample = new(
                    center.X + outward.X * landingOffset + t.X * tangentOffset,
                    center.Y + outward.Y * landingOffset + t.Y * tangentOffset,
                    z);

                Numerics.Vector3? hit = SnapFreeSide(
                    query, sample, center, outward, sample,
                    boxHalfHeight, maxSnapDistance, halfSpaceTolerance);
                if (hit.HasValue)
                    return hit;
            }

            return null;
        }

        private static Numerics.Vector3? SnapFreeSide(
            DetourNavMeshQuery query,
            Numerics.Vector3 sample,
            Numerics.Vector2 ladderCenter,
            Numerics.Vector2 outward,
            Numerics.Vector3 reference,
            float boxHalfHeight = BOX_HALF_HEIGHT,
            float maxSnapDistance = MAX_SNAP_DISTANCE,
            float halfSpaceTolerance = HALF_SPACE_TOLERANCE)
        {
            Numerics.Vector3 boxCenter = new(
                sample.X + outward.X * (BOX_DEPTH * 0.5f),
                sample.Y + outward.Y * (BOX_DEPTH * 0.5f),
                sample.Z);

            Numerics.Vector3 extents = new(
                Math.Max(BOX_HALF_WIDTH, BOX_DEPTH * 0.5f + 4f),
                Math.Max(BOX_HALF_WIDTH, BOX_DEPTH * 0.5f + 4f),
                boxHalfHeight);

            Numerics.Vector3? hit = query.FindClosestPointInBox(boxCenter, extents, reference, WalkFilters);
            if (!hit.HasValue)
                return null;

            Numerics.Vector3 p = hit.Value;
            float side = (p.X - ladderCenter.X) * outward.X + (p.Y - ladderCenter.Y) * outward.Y;
            if (side < -halfSpaceTolerance)
                return null;

            float dx = p.X - sample.X;
            float dy = p.Y - sample.Y;
            float dz = p.Z - sample.Z;
            if (dx * dx + dy * dy + dz * dz > maxSnapDistance * maxSnapDistance)
                return null;

            return p;
        }

        private static List<Numerics.Vector3?[]> MergeFloors(
            List<Numerics.Vector3?[]> floors,
            LadderDefinition ladder)
        {
            if (floors.Count == 0)
                return floors;

            floors = floors.OrderBy(AverageZ).ToList();

            List<Numerics.Vector3?[]> merged = new();
            Numerics.Vector3?[] current = (Numerics.Vector3?[])floors[0].Clone();
            float currentZ = AverageZ(current);

            for (int i = 1; i < floors.Count; i++)
            {
                Numerics.Vector3?[] next = floors[i];
                float nextZ = AverageZ(next);

                bool nearEachOther = Math.Abs(nextZ - currentZ) <= Z_MERGE_DISTANCE;
                bool keepExtremitiesSeparate =
                    nearEachOther &&
                    IsNearBottomExtremity(currentZ, ladder) &&
                    IsNearTopExtremity(nextZ, ladder);

                if (nearEachOther && !keepExtremitiesSeparate)
                {
                    for (int c = 0; c < current.Length; c++)
                    {
                        if (!current[c].HasValue && next[c].HasValue)
                            current[c] = next[c];
                    }
                }
                else
                {
                    merged.Add(current);
                    current = (Numerics.Vector3?[])next.Clone();
                    currentZ = nextZ;
                }
            }

            merged.Add(current);
            return merged;
        }

        private static bool IsNearBottomExtremity(float z, LadderDefinition ladder)
        {
            return Math.Abs(z - ladder.Bottom.Z) <= EXTREMITY_Z_BAND ||
                Math.Abs(z - ladder.MinZ) <= EXTREMITY_Z_BAND;
        }

        private static bool IsNearTopExtremity(float z, LadderDefinition ladder)
        {
            return Math.Abs(z - ladder.Top.Z) <= EXTREMITY_Z_BAND ||
                Math.Abs(z - ladder.MaxZ) <= EXTREMITY_Z_BAND;
        }

        private static float AverageZ(Numerics.Vector3?[] columns)
        {
            float sum = 0;
            int n = 0;
            foreach (Numerics.Vector3? c in columns)
            {
                if (!c.HasValue)
                    continue;
                sum += c.Value.Z;
                n++;
            }

            return n == 0 ? 0f : sum / n;
        }

        private static Numerics.Vector2 GetLadderCenterAtZ(LadderDefinition ladder, float z)
        {
            float dz = ladder.Top.Z - ladder.Bottom.Z;

            if (Math.Abs(dz) < 1e-4f)
                return ToNum(ladder.CenterXY);

            float t = (z - ladder.Bottom.Z) / dz;

            float x = ladder.Bottom.X + t * (ladder.Top.X - ladder.Bottom.X);
            float y = ladder.Bottom.Y + t * (ladder.Top.Y - ladder.Bottom.Y);

            return new Numerics.Vector2(x, y);
        }

        private static int EmitThreeLinkHops(
            LadderDefinition ladder,
            Numerics.Vector2 outward,
            Numerics.Vector2 tangent,
            List<Numerics.Vector3?[]> floors,
            PlacementResult result)
        {
            OpenTK.Vector2 tkTangent = ToOpenTK(tangent);
            OpenTK.Vector2 tkFront = ToOpenTK(outward);

            // One pad per floor on this free face (shared across adjacent hops).
            Numerics.Vector3[] pads = new Numerics.Vector3[floors.Count];
            for (int i = 0; i < floors.Count; i++)
            {
                float z = AverageZ(floors[i]);
                Numerics.Vector2 centerAtZ = GetLadderCenterAtZ(ladder, z);

                pads[i] = new(
                    centerAtZ.X + outward.X * WALL_CLEARANCE,
                    centerAtZ.Y + outward.Y * WALL_CLEARANCE,
                    z);

                result.Pads.Add(new(
                    ToOpenTK(pads[i]),
                    tkTangent,
                    tkFront,
                    PAD_WIDTH,
                    PAD_DEPTH));
            }

            int hops = 0;
            for (int i = 0; i < floors.Count - 1; i++)
            {
                if (!TryPickHopLandings(floors[i], floors[i + 1], out Numerics.Vector3 lower, out Numerics.Vector3 upper))
                    continue;

                result.Links.Add(new(ToOpenTK(lower), ToOpenTK(pads[i])));
                result.Links.Add(new(ToOpenTK(pads[i]), ToOpenTK(pads[i + 1])));
                result.Links.Add(new(ToOpenTK(pads[i + 1]), ToOpenTK(upper)));
                hops++;
            }

            Log.Debug(
                $"Ladder '{ladder.Name}' side ({outward.X:F2},{outward.Y:F2}): " +
                $"{floors.Count} floor(s), {hops} hop(s)×3");

            return hops;
        }

        private static bool TryPickHopLandings(
            Numerics.Vector3?[] lower,
            Numerics.Vector3?[] upper,
            out Numerics.Vector3 a,
            out Numerics.Vector3 b)
        {
            // Prefer center column (index of 0 in offsets is -32; center is index 1).
            int centerIdx = 1;
            if (lower[centerIdx].HasValue && upper[centerIdx].HasValue)
            {
                a = lower[centerIdx]!.Value;
                b = upper[centerIdx]!.Value;
                return true;
            }

            for (int c = 0; c < ColumnOffsets.Length; c++)
            {
                if (lower[c].HasValue && upper[c].HasValue)
                {
                    a = lower[c]!.Value;
                    b = upper[c]!.Value;
                    return true;
                }
            }

            return TryCrossColumnFallback(lower, upper, out a, out b);
        }

        private static bool TryCrossColumnFallback(
            Numerics.Vector3?[] lower,
            Numerics.Vector3?[] upper,
            out Numerics.Vector3 a,
            out Numerics.Vector3 b)
        {
            a = default;
            b = default;
            float best = float.MaxValue;
            bool found = false;

            for (int i = 0; i < lower.Length; i++)
            {
                if (!lower[i].HasValue)
                    continue;

                for (int j = 0; j < upper.Length; j++)
                {
                    if (!upper[j].HasValue)
                        continue;

                    Numerics.Vector3 p = lower[i]!.Value;
                    Numerics.Vector3 q = upper[j]!.Value;
                    float dx = p.X - q.X;
                    float dy = p.Y - q.Y;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist <= CROSS_COLUMN_FALLBACK_MAX_HORIZ && dist < best)
                    {
                        best = dist;
                        a = p;
                        b = q;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static Numerics.Vector2 ToNum(OpenTK.Vector2 v)
        {
            return new(v.X, v.Y);
        }

        private static OpenTK.Vector2 ToOpenTK(Numerics.Vector2 v)
        {
            return new(v.X, v.Y);
        }

        private static OpenTK.Vector3 ToOpenTK(Numerics.Vector3 v)
        {
            return new(v.X, v.Y, v.Z);
        }

        public readonly record struct LadderPad(
            OpenTK.Vector3 Center,
            OpenTK.Vector2 Tangent,
            OpenTK.Vector2 Front,
            float Width,
            float Depth);

        public readonly record struct OffMeshLink(OpenTK.Vector3 Start, OpenTK.Vector3 End);
    }
}
