using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>Offline native-mesh proofs, keyed to the actual database spawn.
    /// No live world scans/path searches per bot. Changed/missing spawns fail
    /// closed until audited again; this never creates monsters or edits levels.</summary>
    public static class AutonomousDungeonGoalCatalog
    {
        public sealed class Point
        {
            public string Id { get; set; }
            public ushort Zone { get; set; }
            public ushort Region { get; set; }
            public string Name { get; set; }
            public int[] Spawn { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("point")]
            public float[] Coordinates { get; set; }
            public int[][] Entries { get; set; }
            public Vector3 Position => new((int)Math.Round(Coordinates[0]), (int)Math.Round(Coordinates[1]), (int)Math.Round(Coordinates[2]));
        }
        private sealed class Document { public Point[] Spawns { get; set; } = []; }
        private sealed record Catalog(Dictionary<string, Point> Spawns,
            Dictionary<(ushort Region, int X, int Y), HashSet<(int X, int Y, int Z)>> Entrances);
        private static readonly Lazy<Catalog> Data = new(Load, true);
        public static int VerifiedSpawnCount => Data.Value.Spawns.Count;
        public static int VerifiedSpawnCountForRegion(ushort region) =>
            region == AutonomousDarknessFallsPolicy.RegionId
                ? AutonomousDarknessFallsNavigation.SnapshotCertifiedProofs().Length
                : Data.Value.Spawns.Values.Count(point => point.Region == region);
        public static bool HasVerifiedSpawn(string id) =>
            !string.IsNullOrWhiteSpace(id) && (Data.Value.Spawns.ContainsKey(id) ||
                AutonomousDarknessFallsNavigation.TryGetProof(id, out _));
        public static Point[] VerifiedPointsForRegion(ushort region) =>
            region == AutonomousDarknessFallsPolicy.RegionId
                ? AutonomousDarknessFallsNavigation.SnapshotCertifiedProofs().Select(FromDarknessFallsProof).ToArray()
                : Data.Value.Spawns.Values.Where(point => point.Region == region).ToArray();
        public static bool HasCompleteDarknessFallsCatalog =>
            AutonomousDarknessFallsNavigation.IsReady;

        private static Point FromDarknessFallsProof(AutonomousDarknessFallsNavigation.SpawnProof proof) => new()
        {
            Id = proof.Id, Zone = 249, Region = AutonomousDarknessFallsPolicy.RegionId,
            Name = proof.Name, Spawn = (int[])proof.Spawn.Clone(),
            Coordinates = (float[])proof.Approach.Clone(),
            Entries = proof.Routes.Select(route => route.InWaypoints[0])
                .Select(point => new[] { (int)Math.Round(point[0]), (int)Math.Round(point[1]),
                    (int)Math.Round(point[2]) }).ToArray()
        };

        private static Catalog Load()
        {
            Assembly assembly = typeof(AutonomousDungeonGoalCatalog).Assembly;
            string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("dungeon_navigation_points.json", StringComparison.Ordinal));
            using Stream stream = assembly.GetManifestResourceStream(resource);
            Document document = JsonSerializer.Deserialize<Document>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var points = new Dictionary<string, Point>(StringComparer.Ordinal);
            var entries = new Dictionary<(ushort, int, int), HashSet<(int, int, int)>>();
            foreach (Point point in document.Spawns)
            {
                // Historical general-dungeon JSON has only three DF samples.
                // Never mix them with the staged exact-spawn certificate or
                // cache an incomplete DF catalog during early world startup.
                if (point.Region == AutonomousDarknessFallsPolicy.RegionId ||
                    !AutonomousDungeonPolicy.IsSupportedDungeonZone(point.Zone) ||
                    point.Coordinates?.Length != 3 ||
                    point.Spawn?.Length != 3 || point.Entries?.Length == 0) continue;
                points[point.Id] = point;
                Vector3 position = point.Position;
                var key = (point.Region, (int)position.X, (int)position.Y);
                if (!entries.TryGetValue(key, out var allowed)) entries[key] = allowed = new();
                foreach (int[] entry in point.Entries) allowed.Add((entry[0], entry[1], entry[2]));
            }
            return new(points, entries);
        }

        public static bool MatchesSpawn(Point point, string id, ushort region, ushort zone, string name, Vector3 spawn) =>
            point != null && point.Id == id && point.Region == region && point.Zone == zone &&
            string.Equals(point.Name, name, StringComparison.OrdinalIgnoreCase) && point.Spawn?.Length == 3 &&
            Vector3.DistanceSquared(spawn, new(point.Spawn[0], point.Spawn[1], point.Spawn[2])) <= 16;

        public static bool TryGet(GameNPC npc, out Point point)
        {
            point = null;
            if (npc?.CurrentRegionID == AutonomousDarknessFallsPolicy.RegionId)
            {
                if (!AutonomousDarknessFallsNavigation.TryGetProof(npc.InternalID, out var proof))
                    return false;
                Point certified = FromDarknessFallsProof(proof);
                if (!MatchesSpawn(certified, npc.InternalID, npc.CurrentRegionID,
                    npc.CurrentZone?.ID ?? 0, npc.Name,
                    new(npc.SpawnPoint.X, npc.SpawnPoint.Y, npc.SpawnPoint.Z))) return false;
                point = certified;
                return true;
            }
            if (npc?.InternalID == null || !Data.Value.Spawns.TryGetValue(npc.InternalID, out Point candidate) ||
                !MatchesSpawn(candidate, npc.InternalID, npc.CurrentRegionID, npc.CurrentZone.ID, npc.Name,
                    new(npc.SpawnPoint.X, npc.SpawnPoint.Y, npc.SpawnPoint.Z))) return false;
            point = candidate;
            return true;
        }

        // Native proofs store floor Z; authoritative portals store client Z.
        // Their measured 2-24 unit floor offset must not delete a real edge.
        // Keep XY effectively exact so this cannot admit a different entrance.
        public static bool MatchesEntrance(Vector3 authored, Vector3 proven) =>
            Vector2.DistanceSquared(new(authored.X, authored.Y), new(proven.X, proven.Y)) <= 4 &&
            Math.Abs(authored.Z - proven.Z) <= 32;

        public static bool CanUseEntrance(DbZonePoint edge, ushort goalRegion, int goalX, int goalY) =>
            goalRegion == AutonomousDarknessFallsPolicy.RegionId && edge?.TargetRegion == goalRegion
                // Ordinary goals need a certified camp entry; a running Darkness Falls raid's
                // destinations (Legion, the High Lords...) use the realm's own home entrances.
                ? AutonomousDarknessFallsNavigation.HasCertifiedEntrance(edge, goalX, goalY) ||
                  AutonomousRealmRaid.IsActiveRaidDestination(goalRegion, goalX, goalY) &&
                  AutonomousDarknessFallsNavigation.IsHomeEntrance(edge)
                :
            edge.TargetRegion != goalRegion || !Data.Value.Entrances.TryGetValue((goalRegion, goalX, goalY), out var entrances) ||
            entrances.Contains((edge.TargetX, edge.TargetY, edge.TargetZ)) ||
            MatchesAnyEntrance(entrances, new(edge.TargetX, edge.TargetY, edge.TargetZ));

        // A plain loop: called for every zone point on every region route (closure allocations
        // per call showed in the run 8 allocation trace).
        private static bool MatchesAnyEntrance(HashSet<(int X, int Y, int Z)> entrances, Vector3 authored)
        {
            foreach (var entry in entrances)
                if (MatchesEntrance(authored, new(entry.X, entry.Y, entry.Z))) return true;
            return false;
        }
    }

    public sealed partial class AutonomousWorldBotController
    {
        private static void AddVerifiedDungeonCamps(List<CampCatalogCell> cells,
            Dictionary<(ushort ZoneId, string Name), CampMonster[]> live)
        {
            var authoritative = AutonomousCapnBryGoalCatalog.Entries.ToLookup(entry => (entry.ZoneId, entry.Name.ToLowerInvariant()));
            foreach (var pair in live.Where(pair => AutonomousDungeonPolicy.IsSupportedDungeonZone(pair.Key.ZoneId)))
            {
                Zone zone = WorldMgr.GetZone(pair.Key.ZoneId);
                if (zone?.IsDungeon != true) continue;
                if (!AutonomousDungeonPolicy.IsReliableAutonomousGoal(zone.ZoneRegion.ID, pair.Key.Name)) continue;
                var verified = pair.Value.Select(npc => (Npc: npc,
                        Point: npc.DungeonPoint))
                    .Where(item => item.Point != null && item.Npc.EffectiveLevel > 0 &&
                        (zone.ZoneRegion.ID != AutonomousDarknessFallsPolicy.RegionId ||
                         AutonomousDarknessFallsGoalScope.IsOrdinaryCatalogId(item.Point.Id))).ToArray();
                if (verified.Length == 0) continue;
                if (AutonomousDungeonPolicy.IsStarterDungeonRegion(zone.ZoneRegion.ID))
                {
                    // The three starter dungeons have now been audited room by
                    // room against the installed meshes. Their period maps are
                    // more complete than CapnBry's sparse dungeon listings, so
                    // expose every proven live room without admitting any
                    // unaudited spawn or inventing a camp center through walls.
                    AddVerifiedLiveRooms(cells, pair.Key.Name, zone, verified);
                    continue;
                }
                if (AutonomousCapnBryGoalCatalog.CoveredZoneIds.Contains(zone.ID))
                {
                    // Retain CapnBry names/levels/locations in covered zones.
                    // Use a proven, real spawn nearby, never a random point on
                    // the other side of a wall or on another dungeon floor.
                    var representedLevels = new HashSet<int>();
                    foreach (var entry in authoritative[(zone.ID, pair.Key.Name)])
                    {
                        var matches = verified.Where(item => entry.Levels.Contains(item.Npc.EffectiveLevel) &&
                            DistanceSquared(item.Point.Spawn[0], item.Point.Spawn[1], zone.XOffset + entry.LocalX, zone.YOffset + entry.LocalY) <= TargetSearchRadius * TargetSearchRadius)
                            .OrderBy(item => Vector3.DistanceSquared(item.Point.Position, new(zone.XOffset + entry.LocalX, zone.YOffset + entry.LocalY, entry.Z))).ToArray();
                        if (matches.Length == 0) continue;
                        // A bestiary entry can cover several levels in the same
                        // room. Do not advertise the low level while pointing
                        // its party at the first (possibly high-level) spawn.
                        foreach (var levelRoom in matches.GroupBy(item => item.Npc.EffectiveLevel))
                        {
                            var first = levelRoom.OrderBy(item => Vector3.DistanceSquared(item.Point.Position,
                                new(zone.XOffset + entry.LocalX, zone.YOffset + entry.LocalY, entry.Z))).First();
                            Vector3 p = first.Point.Position;
                            cells.Add(new($"{entry.Id}:L{levelRoom.Key}", entry.Name, entry.Zone, entry.RegionId,
                                (int)p.X, (int)p.Y, (int)p.Z, [levelRoom.Key], levelRoom.Count(), zone, true,
                                IsFrontierZone(entry.RegionId, entry.ZoneId), NeedsProjection: false));
                        }
                        representedLevels.UnionWith(matches.Select(item => item.Npc.EffectiveLevel));
                    }

                    // Period bestiaries identify the authentic camp, but some
                    // dungeon pages list only a few sample levels/rooms.  Fill
                    // only those uncovered levels from existing live monsters
                    // whose exact spawn has a stored two-way installed-mesh
                    // proof.  This adds no mobs and changes no levels or combat.
                    var uncovered = verified
                        .Where(item => !representedLevels.Contains(item.Npc.EffectiveLevel))
                        .ToArray();
                    if (uncovered.Length > 0)
                        AddVerifiedLiveRooms(cells, pair.Key.Name, zone, uncovered);
                }
                else
                {
                    // Frontier branches have no CapnBry catalog entries. As
                    // with source-empty SI dungeons, expose only existing live
                    // monsters with a proven client-mesh entrance route.
                    AddVerifiedLiveRooms(cells, pair.Key.Name, zone, verified);
                }
            }
        }

        private static void AddVerifiedLiveRooms(List<CampCatalogCell> cells, string normalizedName, Zone zone,
            (CampMonster Npc, AutonomousDungeonGoalCatalog.Point Point)[] verified)
        {
            if (!AutonomousDungeonPolicy.IsReliableAutonomousGoal(zone.ZoneRegion.ID, normalizedName))
                return;

            if (zone.ZoneRegion.ID == AutonomousDarknessFallsPolicy.RegionId)
            {
                // A same-named creature can be present in all three wings.
                // Keep the certified entrance-distance/wing for each room so
                // a Midgard bot is not sent through the center to an Albion
                // copy merely because the monster names match.
                var proved = verified.Select(item =>
                {
                    AutonomousDarknessFallsNavigation.TryGetProof(item.Point.Id, out var proof);
                    return (item.Npc, item.Point, Proof: proof);
                }).Where(item => item.Proof != null);
                foreach (var room in proved.GroupBy(item =>
                         (X: (int)item.Point.Position.X / 900,
                          Y: (int)item.Point.Position.Y / 900,
                          Z: (int)item.Point.Position.Z / 200,
                          Level: item.Npc.EffectiveLevel,
                          Wing: AutonomousDarknessFallsNavigation.ClosestWing(item.Proof))))
                {
                    var first = room.OrderBy(item => AutonomousDarknessFallsNavigation.DistanceFromEntrance(
                        item.Proof, room.Key.Wing)).First();
                    Vector3 position = first.Point.Position;
                    // Group directives retain only the camp ID, not the
                    // catalog Point. Carry the exact certified spawn key so
                    // every member can recover its own realm's waypoint
                    // chain after a restart or a shared-goal handoff.
                    string id = $"df-live:{first.Point.Id}:{zone.ID}:{room.Key}:{normalizedName}";
                    cells.Add(new(id, first.Npc.Name, zone.Description, zone.ZoneRegion.ID,
                        (int)position.X, (int)position.Y, (int)position.Z,
                        [room.Key.Level], room.Count(), zone, true,
                        IsFrontierZone(zone.ZoneRegion.ID, zone.ID), NeedsProjection: false,
                        DarknessFallsWing: room.Key.Wing,
                        DarknessFallsAlbionDistance: AutonomousDarknessFallsNavigation.DistanceFromEntrance(first.Proof, eRealm.Albion),
                        DarknessFallsMidgardDistance: AutonomousDarknessFallsNavigation.DistanceFromEntrance(first.Proof, eRealm.Midgard),
                        DarknessFallsHiberniaDistance: AutonomousDarknessFallsNavigation.DistanceFromEntrance(first.Proof, eRealm.Hibernia)));
                }
                return;
            }

            foreach (var room in verified.GroupBy(item => LiveRoomKey(item.Point.Position,
                         item.Npc.EffectiveLevel)))
            {
                var first = room.First();
                Vector3 p = first.Point.Position;
                string id = $"dungeon-live:{zone.ID}:{room.Key}:{normalizedName}";
                cells.Add(new(id, first.Npc.Name, zone.Description, zone.ZoneRegion.ID,
                    (int)p.X, (int)p.Y, (int)p.Z, [room.Key.Level],
                    room.Count(), zone, true, IsFrontierZone(zone.ZoneRegion.ID, zone.ID), NeedsProjection: false));
            }
        }

        // Keep a camp's advertised level tied to its actual, proven spawn.
        // Koalinth sentinel rooms include both level-18 and level-27 live
        // variants. One shared center/ID can send a low-level party toward
        // a different level than the one that qualified the goal.
        public static (int X, int Y, int Z, int Level) LiveRoomKey(Vector3 position, int level) =>
            ((int)position.X / 900, (int)position.Y / 900, (int)position.Z / 200, level);
    }
}
