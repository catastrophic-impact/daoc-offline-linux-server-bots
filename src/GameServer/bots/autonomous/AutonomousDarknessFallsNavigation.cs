using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DOL.Database;

namespace DOL.GS;

/// <summary>
/// Fail-closed navigation certificate for Darkness Falls. A compatible
/// certificate contains only the rebuilt-mesh, source-audited, floor-reachable,
/// level-60-or-lower staged goal subset. It stays closed if the installed
/// mesh or live database differs from that exact audit.
/// Every other ordinary spawn must be named in the exact exclusion snapshot;
/// flying/perched mobs are never silently admitted as goals.
/// </summary>
public static class AutonomousDarknessFallsNavigation
{
    // Only ordinary grind targets belong to the random goal certificate.
    // The separate exact-ID raid/event manifest validates the other 85 rows.
    // This is the full ordinary DB baseline, not the number of eligible goals.
    public const int RequiredCombatSpawns = AutonomousDarknessFallsGoalScope.OrdinaryCombatRows;
    private const string ResourceSuffix = "darkness_falls_navigation_proofs.json";
    private static readonly Lazy<Certificate> CertificateData = new(LoadCertificate, true);
    private static readonly Lazy<Dictionary<string, SpawnProof>> ProofIndex = new(() =>
        CertificateData.Value?.Spawns.ToDictionary(proof => proof.Id, StringComparer.Ordinal) ??
        new Dictionary<string, SpawnProof>(StringComparer.Ordinal), true);
    private static readonly Lazy<Dictionary<(int X, int Y, eRealm Realm), Vector3[]>> EntranceIndex = new(() =>
        CertificateData.Value?.Spawns.SelectMany(proof => proof.Routes.Select(route =>
                (Key: ((int)Math.Round(proof.Approach[0]), (int)Math.Round(proof.Approach[1]), route.Realm),
                    Point: new Vector3(route.InWaypoints[0][0], route.InWaypoints[0][1],
                        route.InWaypoints[0][2]))))
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Point).ToArray()) ??
        new Dictionary<(int, int, eRealm), Vector3[]>(), true);

    public sealed class RouteProof
    {
        public eRealm Realm { get; set; }
        public float DistanceFromEntrance { get; set; }
        public float DistanceToOwnExit { get; set; }
        public ushort ExitZonePointId { get; set; }
        public ushort ExitTargetRegion { get; set; }
        public int[] Exit { get; set; }
        // Each neighboring pair is a separately proven complete native leg.
        // In starts at this realm's real entrance; Out ends at its own exit.
        public float[][] InWaypoints { get; set; }
        public float[][] OutWaypoints { get; set; }
    }

    public sealed class SpawnProof
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public string ClassType { get; set; }
        public ushort Model { get; set; }
        public uint Flags { get; set; }
        public int[] Spawn { get; set; }
        public float[] Approach { get; set; }
        public bool AttackableFromApproach { get; set; }
        public RouteProof[] Routes { get; set; }
    }

    public sealed class ExcludedOrdinarySpawn
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public string ClassType { get; set; }
        public ushort Model { get; set; }
        public uint Flags { get; set; }
        public int[] Spawn { get; set; }
        // Flying/perched goals stay excluded regardless of a later nav pass;
        // unverified ground is excluded until separately audited.
        public string Reason { get; set; }
    }

    public sealed class Certificate
    {
        public string NavSha256 { get; set; }
        public SpawnProof[] Spawns { get; set; }
        public ExcludedOrdinarySpawn[] ExcludedOrdinarySpawns { get; set; }
        // A generic Jump flag never certifies a stair or drop. These exact
        // geometry-backed links are part of the offline native route audit.
        public TraversalLink[] TraversalLinks { get; set; }
    }

    public sealed class TraversalLink
    {
        // Climb: short collision-backed ladder segment. Fall: measured
        // one-way entrance ledge, always upper to lower. Neither is a teleport.
        public string Kind { get; set; }
        public float[] Start { get; set; }
        // Falls require a collision-checked air position just past the lip.
        // The NPC mover must not interpolate diagonally through a cliff.
        public float[] Air { get; set; }
        public float[] End { get; set; }
        public bool Bidirectional { get; set; }
    }

    /// <summary>Only an exact installed-mesh + full database snapshot opens
    /// the staged goal pool. Unknown added/deleted/moved combat mobs close it.</summary>
    // Do not touch the Lazy during early world startup; a missing database at
    // that instant must not cache a permanent false after initialization.
    public static bool IsReady => ReadyWhen(GameServer.Instance != null && GameServer.Database != null,
        () => CertificateData.Value != null);

    public static bool ReadyWhen(bool databaseReady, Func<bool> certificateAvailable) =>
        databaseReady && certificateAvailable?.Invoke() == true;

    public static bool TryGetProof(string id, out SpawnProof proof)
    {
        proof = null;
        return IsReady && !string.IsNullOrWhiteSpace(id) && ProofIndex.Value.TryGetValue(id, out proof);
    }

    public static SpawnProof[] SnapshotCertifiedProofs() =>
        IsReady ? CertificateData.Value.Spawns.ToArray() : [];

    /// <summary>The old general dungeon-point file contains only three DF
    /// samples. Authorize entry for a selected camp from the certificate's
    /// exact realm arrival instead of that stale file or any other portal.</summary>
    public static bool HasCertifiedEntrance(DbZonePoint edge, int campX, int campY)
    {
        if (!IsReady || edge?.TargetRegion != AutonomousDarknessFallsPolicy.RegionId)
            return false;
        eRealm realm = new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }
            .FirstOrDefault(candidate => AutonomousDarknessFallsPolicy.HomeRegion(candidate) == edge.SourceRegion);
        if (realm == eRealm.None || edge.Realm != 0 && edge.Realm != (ushort)realm ||
            !EntranceIndex.Value.TryGetValue((campX, campY, realm), out Vector3[] entries))
            return false;
        return entries.Any(entry => MatchesCertifiedEntrance(edge, realm, entry));
    }

    /// <summary>
    /// A realm's own way into Darkness Falls from its home region. Realm events (Legion, the
    /// High Lords...) are not ordinary camps, so they use these instead of certified camp entries.
    /// </summary>
    public static bool IsHomeEntrance(DbZonePoint edge)
    {
        if (edge?.TargetRegion != AutonomousDarknessFallsPolicy.RegionId) return false;
        eRealm realm = new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }
            .FirstOrDefault(candidate => AutonomousDarknessFallsPolicy.HomeRegion(candidate) == edge.SourceRegion);
        return realm != eRealm.None && (edge.Realm == 0 || edge.Realm == (ushort)realm);
    }

    public static bool MatchesCertifiedEntrance(DbZonePoint edge, eRealm realm, Vector3 entry) =>
        edge != null && edge.TargetRegion == AutonomousDarknessFallsPolicy.RegionId &&
        edge.SourceRegion == AutonomousDarknessFallsPolicy.HomeRegion(realm) &&
        (edge.Realm == 0 || edge.Realm == (ushort)realm) &&
        Vector2.Distance(new(entry.X, entry.Y), new(edge.TargetX, edge.TargetY)) <= 4 &&
        Math.Abs(entry.Z - edge.TargetZ) <= 32;

    public static bool HasFullCombatCoverage(IEnumerable<DbMob> combatMobs,
        IEnumerable<SpawnProof> proofs) => HasFullCombatCoverage(combatMobs, proofs, []);

    public static bool HasFullCombatCoverage(IEnumerable<DbMob> combatMobs,
        IEnumerable<SpawnProof> proofs, IEnumerable<ExcludedOrdinarySpawn> exclusions)
    {
        if (combatMobs == null || proofs == null || exclusions == null) return false;
        if (!AutonomousDarknessFallsGoalScope.TryGetOrdinaryCombatRows(combatMobs, out DbMob[] required))
            return false;
        SpawnProof[] certified = proofs.ToArray();
        ExcludedOrdinarySpawn[] excluded = exclusions.ToArray();
        if (required.Length != RequiredCombatSpawns || certified.Length < 1 ||
            certified.Length + excluded.Length != required.Length ||
            required.Select(mob => mob.ObjectId).Distinct(StringComparer.Ordinal).Count() != required.Length ||
            certified.Any(proof => !ValidProof(proof)) ||
            excluded.Any(item => !ValidExclusion(item))) return false;
        var byId = certified.GroupBy(proof => proof.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var excludedById = excluded.GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return byId.Count == certified.Length && excludedById.Count == excluded.Length &&
            required.All(mob =>
            {
                bool proved = byId.TryGetValue(mob.ObjectId, out SpawnProof[] matching);
                bool omitted = excludedById.TryGetValue(mob.ObjectId, out ExcludedOrdinarySpawn[] skipped);
                return proved != omitted && (proved
                    ? matching.Length == 1 && MatchesSnapshot(mob, matching[0].Name,
                        matching[0].Level, matching[0].ClassType, matching[0].Model,
                        matching[0].Flags, matching[0].Spawn)
                    : skipped.Length == 1 && MatchesSnapshot(mob, skipped[0].Name,
                        skipped[0].Level, skipped[0].ClassType, skipped[0].Model,
                        skipped[0].Flags, skipped[0].Spawn));
            });
    }

    private static bool MatchesSnapshot(DbMob mob, string name, int level,
        string classType, ushort model, uint flags, int[] spawn) =>
        string.Equals(name, mob.Name, StringComparison.Ordinal) && level == mob.Level &&
        string.Equals(classType, mob.ClassType, StringComparison.Ordinal) &&
        model == mob.Model && flags == mob.Flags && spawn?.Length == 3 &&
        spawn[0] == mob.X && spawn[1] == mob.Y && spawn[2] == mob.Z;

    private static bool ValidExclusion(ExcludedOrdinarySpawn item) =>
        item != null && !string.IsNullOrWhiteSpace(item.Id) &&
        !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.ClassType) &&
        item.Level > 0 && item.Spawn?.Length == 3 &&
        (item.Reason == "FlyingOrPerched" ||
         item.Reason == "AboveLevel60" && item.Level > 60 ||
         item.Reason == "UnverifiedRoute" && item.Level <= 60 &&
             (item.Flags & (uint)GameNPC.eFlags.FLYING) == 0);

    private static bool ValidProof(SpawnProof proof)
    {
        if (proof == null || string.IsNullOrWhiteSpace(proof.Id) || string.IsNullOrWhiteSpace(proof.Name) ||
            string.IsNullOrWhiteSpace(proof.ClassType) || proof.Level is < 1 or > 60 ||
            (proof.Flags & (uint)GameNPC.eFlags.FLYING) != 0 ||
            proof.Spawn?.Length != 3 || proof.Approach?.Length != 3 || !proof.AttackableFromApproach ||
            proof.Approach.Any(value => !float.IsFinite(value)) || proof.Routes?.Length != 3 ||
            !IsInAssignedRoom(new(proof.Approach[0], proof.Approach[1], proof.Approach[2]),
                new(proof.Spawn[0], proof.Spawn[1], proof.Spawn[2]))) return false;
        return new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }.All(realm =>
            proof.Routes.Count(route => route != null && route.Realm == realm &&
                float.IsFinite(route.DistanceFromEntrance) && route.DistanceFromEntrance > 0 &&
                float.IsFinite(route.DistanceToOwnExit) && route.DistanceToOwnExit > 0 &&
                route.Exit?.Length == 3 &&
                route.ExitZonePointId == AutonomousDarknessFallsPolicy.HomeExitZonePointId(realm) &&
                route.ExitTargetRegion == AutonomousDarknessFallsPolicy.HomeRegion(realm) &&
                ValidWaypoints(route.InWaypoints) && ValidWaypoints(route.OutWaypoints) &&
                Near(route.InWaypoints[^1], proof.Approach, 48) &&
                Near(route.OutWaypoints[0], proof.Approach, 48) &&
                Near(route.OutWaypoints[^1], route.Exit, 64)) == 1);
    }

    private static bool ValidWaypoints(float[][] waypoints) =>
        waypoints is { Length: >= 2 and <= 256 } &&
        waypoints.All(point => point?.Length == 3 && point.All(float.IsFinite));

    private static bool Near(float[] a, float[] b, float maxDistance) =>
        b?.Length == 3 && Vector3.Distance(new(a[0], a[1], a[2]), new(b[0], b[1], b[2])) <= maxDistance;

    private static bool Near(float[] a, int[] b, float maxDistance) =>
        b?.Length == 3 && Vector3.Distance(new(a[0], a[1], a[2]), new(b[0], b[1], b[2])) <= maxDistance;

    /// <summary>The certificate's exit path must terminate at a real
    /// same-realm DF portal row. Shared physical portals have different DB
    /// rows for each realm; their visible location alone is not authority.</summary>
    public static bool HasAuthoritativeRealmExits(IEnumerable<SpawnProof> spawns,
        IEnumerable<DbZonePoint> zonePoints)
    {
        if (spawns == null || zonePoints == null) return false;
        RouteProof[] routes = spawns.SelectMany(spawn => spawn?.Routes ?? []).ToArray();
        var exits = zonePoints.Where(point => point?.SourceRegion == AutonomousDarknessFallsPolicy.RegionId)
            .ToLookup(point => (point.Id, point.Realm, point.TargetRegion));
        return routes.Length > 0 && routes.All(route => route?.Exit?.Length == 3 &&
            route.ExitZonePointId == AutonomousDarknessFallsPolicy.HomeExitZonePointId(route.Realm) &&
            route.ExitTargetRegion == AutonomousDarknessFallsPolicy.HomeRegion(route.Realm) &&
            exits[(route.ExitZonePointId, (ushort)route.Realm, route.ExitTargetRegion)].Any(point =>
                Math.Abs(point.SourceX - route.Exit[0]) <= 32 &&
                Math.Abs(point.SourceY - route.Exit[1]) <= 32 &&
                Math.Abs(point.SourceZ - route.Exit[2]) <= 64));
    }

    private static Certificate LoadCertificate()
    {
        try
        {
            Assembly assembly = typeof(AutonomousDarknessFallsNavigation).Assembly;
            string name = assembly.GetManifestResourceNames().SingleOrDefault(resource =>
                resource.EndsWith(ResourceSuffix, StringComparison.Ordinal));
            if (name == null || GameServer.Database == null) return null;
            using Stream stream = assembly.GetManifestResourceStream(name);
            Certificate certificate = JsonSerializer.Deserialize<Certificate>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (certificate?.Spawns == null || certificate.ExcludedOrdinarySpawns == null ||
                certificate.TraversalLinks?.Length is not > 0 ||
                certificate.TraversalLinks.Any(link => !ValidTraversalLink(link)) || !HasFullCombatCoverage(
                    DOLDB<DbMob>.SelectObjects(DB.Column("Region").IsEqualTo(
                        AutonomousDarknessFallsPolicy.RegionId)), certificate.Spawns,
                    certificate.ExcludedOrdinarySpawns) ||
                !HasAuthoritativeRealmExits(certificate.Spawns,
                    GameServer.Database.SelectAllObjects<DbZonePoint>())) return null;
            string path = Path.GetFullPath(Path.Combine("navmesh", "zone249.nav"));
            if (!File.Exists(path)) path = Path.GetFullPath(Path.Combine("pathing", "zone249.nav"));
            if (!File.Exists(path) || string.IsNullOrWhiteSpace(certificate.NavSha256)) return null;
            using Stream nav = File.OpenRead(path);
            string actualHash = Convert.ToHexString(SHA256.HashData(nav));
            return string.Equals(actualHash, certificate.NavSha256, StringComparison.OrdinalIgnoreCase)
                ? certificate : null;
        }
        catch
        {
            // No proof, a malformed proof, or a different mesh must never
            // create a random PvE objective in a disconnected DF wing.
            return null;
        }
    }

    public static bool IsStrictSegment(PathfindingResult result, ReadOnlySpan<WrappedPathfindingNode> nodes,
        Vector3 start, Vector3 destination, IReadOnlyList<TraversalLink> approvedLinks = null)
    {
        if (result.Status != PathfindingStatus.PathFound || result.NodeCount < 1 ||
            result.NodeCount >= 256 ||
            result.NodeCount > nodes.Length || Vector3.Distance(start, nodes[0].Position) > 64 ||
            Vector3.Distance(destination, nodes[result.NodeCount - 1].Position) > 48) return false;
        for (int i = 1; i < result.NodeCount; i++)
        {
            Vector3 a = nodes[i - 1].Position, b = nodes[i].Position;
            float horizontal = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
            float vertical = Math.Abs(a.Z - b.Z);
            // Detour marks the source node of an off-mesh traversal as Jump.
            // The preceding ordinary walk can end at that node, sometimes
            // after more than 256 GU; the destination's flag must not turn
            // that safe approach into a falsely rejected off-mesh edge.
            bool climb = (nodes[i - 1].Flags & EDtPolyFlags.Jump) != 0;
            // Detour can report Walk at the exact lip of a real one-way fall,
            // even though a longer query through the same link reports Jump.
            // Authorize only the precise forward manifest edge, independent
            // of that flag; never relax generic steep Walk or reverse travel.
            bool approvedTraversal = vertical > 16 && IsApprovedTraversal(a, b, approvedLinks);
            // A Jump flag may also cover a nearly flat pad attachment. Any
            // vertical traversal beyond that must match a certified segment:
            // a short climb or an authentic *directed* entrance fall.
            if (vertical > 224 || (climb
                    ? horizontal > 256 || vertical > 16 && !approvedTraversal
                    : vertical > 64 && vertical > horizontal * 1.25f + 32 && !approvedTraversal)) return false;
        }
        return true;
    }

    /// <summary>Reject a partial, unproven, or uncertified DF route before
    /// Pathfinder queues even its first node. Otherwise the normal NPC mover
    /// could interpolate an early node through a cliff before discovering that
    /// the complete route was impossible. Other regions are unchanged.</summary>
    public static bool MayQueuePath(bool autonomousBot, ushort region, bool certificateReady,
        PathfindingResult result, ReadOnlySpan<WrappedPathfindingNode> nodes,
        Vector3 start, Vector3 destination, IReadOnlyList<TraversalLink> approvedLinks = null) =>
        !autonomousBot || region != AutonomousDarknessFallsPolicy.RegionId ||
        certificateReady && IsStrictSegment(result, nodes, start, destination, approvedLinks);

    public static bool MayQueueRuntimePath(PathfindingResult result, ReadOnlySpan<WrappedPathfindingNode> nodes,
        Vector3 start, Vector3 destination) =>
        MayQueuePath(true, AutonomousDarknessFallsPolicy.RegionId, IsReady,
            result, nodes, start, destination, IsReady ? CertificateData.Value.TraversalLinks : null);

    /// <summary>Evacuation is independent of ordinary-goal readiness. Only a
    /// complete walk-only corridor to the physical home exit is allowed when
    /// the full DF certificate is stale; unknown Jump links and partial paths
    /// remain forbidden. This does not grant any new DF entrance or goal.</summary>
    public static bool MayQueueOwnExitGroundPath(eRealm realm, PathfindingResult result,
        ReadOnlySpan<WrappedPathfindingNode> nodes, Vector3 start, Vector3 destination)
    {
        Vector3 ownExit = realm switch
        {
            eRealm.Albion => new(36118, 30504, 22381),
            eRealm.Midgard => new(11920, 18671, 22380),
            eRealm.Hibernia => new(41722, 36569, 20333),
            _ => default,
        };
        if (ownExit == default ||
            Vector2.Distance(new(destination.X, destination.Y), new(ownExit.X, ownExit.Y)) > 48 ||
            Math.Abs(destination.Z - ownExit.Z) > 64 ||
            !IsStrictSegment(result, nodes, start, destination)) return false;
        for (int i = 0; i < result.NodeCount; i++)
            if ((nodes[i].Flags & EDtPolyFlags.Jump) != 0) return false;
        return true;
    }

    private static bool ValidTraversalLink(TraversalLink link)
    {
        if (link?.Start?.Length != 3 || link.End?.Length != 3 ||
            link.Start.Any(value => !float.IsFinite(value)) ||
            link.End.Any(value => !float.IsFinite(value))) return false;
        Vector3 a = new(link.Start[0], link.Start[1], link.Start[2]);
        Vector3 b = new(link.End[0], link.End[1], link.End[2]);
        float xy = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
        float dz = Math.Abs(a.Z - b.Z);
        return xy <= 256 && (link.Kind switch
        {
            "Climb" => dz <= 66,
            "Fall" => !link.Bidirectional && a.Z > b.Z && dz is >= 120 and <= 210 &&
                ValidFallAir(link, a, b),
            _ => false,
        });
    }

    private static bool ValidFallAir(TraversalLink link, Vector3 start, Vector3 end)
    {
        if (link.Air?.Length != 3 || link.Air.Any(value => !float.IsFinite(value))) return false;
        Vector3 air = new(link.Air[0], link.Air[1], link.Air[2]);
        float offLip = Vector2.Distance(new(start.X, start.Y), new(air.X, air.Y));
        float overLanding = Vector2.Distance(new(air.X, air.Y), new(end.X, end.Y));
        return offLip is >= 16 and <= 128 && overLanding <= 8 &&
            Math.Abs(start.Z - air.Z) <= 16;
    }

    private static bool IsApprovedTraversal(Vector3 a, Vector3 b, IReadOnlyList<TraversalLink> links)
    {
        if (links == null) return false;
        foreach (TraversalLink link in links)
        {
            if (!ValidTraversalLink(link)) continue;
            Vector3 start = new(link.Start[0], link.Start[1], link.Start[2]);
            Vector3 end = new(link.End[0], link.End[1], link.End[2]);
            if (Vector3.Distance(a, start) <= 12 && Vector3.Distance(b, end) <= 12 ||
                link.Bidirectional && Vector3.Distance(a, end) <= 12 && Vector3.Distance(b, start) <= 12)
                return true;
        }
        return false;
    }

    public static bool HasStrictSegment(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 destination)
    {
        if (!IsReady || zone?.ZoneRegion?.ID != AutonomousDarknessFallsPolicy.RegionId || nav == null) return false;
        Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
        PathfindingResult result = nav.GetPathStraight(zone, start, destination, nav.DefaultFilters, nodes);
        return IsStrictSegment(result, nodes, start, destination, CertificateData.Value.TraversalLinks);
    }

    public static bool HasStrictDirectedChain(IPathfindingMgr nav, Zone zone, IReadOnlyList<Vector3> anchors)
    {
        if (anchors == null || anchors.Count < 2) return false;
        for (int i = 1; i < anchors.Count; i++)
            if (!HasStrictSegment(nav, zone, anchors[i - 1], anchors[i])) return false;
        return true;
    }

    public static bool TryGetCertifiedFall(Vector3 actor, Vector3 nextNode, out Vector3 air, out Vector3 landing)
    {
        air = landing = default;
        return IsReady && TryResolveFall(actor, nextNode, CertificateData.Value.TraversalLinks,
            out air, out landing);
    }

    public static bool TryResolveFall(Vector3 actor, Vector3 nextNode, IReadOnlyList<TraversalLink> links,
        out Vector3 air, out Vector3 landing)
    {
        air = landing = default;
        if (links == null) return false;
        foreach (TraversalLink link in links)
        {
            if (link.Kind != "Fall" || !ValidTraversalLink(link)) continue;
            Vector3 start = new(link.Start[0], link.Start[1], link.Start[2]);
            Vector3 end = new(link.End[0], link.End[1], link.End[2]);
            if (Vector3.Distance(actor, start) > 24 || Vector3.Distance(nextNode, end) > 12) continue;
            air = new(link.Air[0], link.Air[1], link.Air[2]);
            landing = end;
            return true;
        }
        return false;
    }

    public static float DistanceFromEntrance(SpawnProof proof, eRealm realm) =>
        proof?.Routes?.FirstOrDefault(route => route.Realm == realm)?.DistanceFromEntrance ?? float.PositiveInfinity;

    public static bool IsInAssignedRoom(Vector3 camp, Vector3 monster) =>
        Math.Abs(camp.Z - monster.Z) <= 220 &&
        Vector2.DistanceSquared(new(camp.X, camp.Y), new(monster.X, monster.Y)) <= 1200 * 1200;

    /// <summary>Ordinary low-level goals stay in the bot's own entrance wing.
    /// A higher-level spawn can also be used from a genuinely shared interior
    /// corridor, but never because it is merely a same-name copy beside the
    /// opposite realm's entrance.</summary>
    public static bool CanUseOrdinaryCamp(eRealm realm, eRealm nearestWing, int level,
        float ownEntranceDistance, float nearestEntranceDistance) =>
        realm != eRealm.None && float.IsFinite(ownEntranceDistance) &&
        float.IsFinite(nearestEntranceDistance) &&
        (nearestWing == realm || level >= 30 && nearestEntranceDistance >= 15_000 &&
            ownEntranceDistance - nearestEntranceDistance <= 18_000);

    public static bool IsNearbySameWingCamp(eRealm realm, eRealm wing,
        string assignedName, string candidateName, Vector3 from, Vector3 candidate,
        float maximumDistance) =>
        realm != eRealm.None && wing == realm &&
        string.Equals(assignedName, candidateName, StringComparison.OrdinalIgnoreCase) &&
        maximumDistance > 0 && Vector2.DistanceSquared(
            new(from.X, from.Y), new(candidate.X, candidate.Y)) <= maximumDistance * maximumDistance;

    public static eRealm ClosestWing(SpawnProof proof)
    {
        RouteProof route = proof?.Routes?.OrderBy(route => route.DistanceFromEntrance).FirstOrDefault();
        return route?.Realm ?? eRealm.None;
    }

    /// <summary>Within DF only, retain the local wing for a repeated monster
    /// name/level when it exists, then favor the nearest proven entrance-side
    /// rooms. Other dungeons retain their existing uniform selection.</summary>
    public static T[] PreferEntranceSide<T>(IEnumerable<T> candidates, eRealm realm,
        Func<T, string> name, Func<T, int> level, Func<T, eRealm> wing, Func<T, float> distance)
    {
        T[] all = candidates?.ToArray() ?? [];
        if (all.Length == 0) return all;
        T[] ownOrNearest = all.GroupBy(item => (name(item), level(item)))
            .SelectMany(group =>
            {
                T[] own = group.Where(item => wing(item) == realm).ToArray();
                return own.Length > 0 ? (IEnumerable<T>)own : group;
            }).ToArray();
        float minimum = ownOrNearest.Min(distance);
        T[] nearby = ownOrNearest.Where(item => distance(item) <= minimum + 1500).ToArray();
        return nearby.Length > 0 ? nearby : ownOrNearest;
    }
}
