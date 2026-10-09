using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace DOL.GS
{
    public sealed partial class AutonomousWorldBotController
    {
        // No live NPC or GameBot is handed to the planning worker. Zone
        // metadata is world-lifetime data; mesh queries remain on the game loop.
        private sealed record CampMonster(string InternalId, string Name, int X, int Y, int Z,
            int EffectiveLevel, ushort CurrentRegionID, Zone CurrentZone,
            AutonomousDungeonGoalCatalog.Point DungeonPoint);

        private static BackgroundSnapshotBuilder<CampMonster[], CampCatalogCell[]> _campBuilder;
        private static CampCatalogCell[] _campDraft;
        private static CampCatalogCell[] _campProjected;
        private static int _projectionCursor;
        private static readonly List<int> ProjectionWork = new(128);
        private static bool _campBuildInFlight;
        private static bool _hasCampCatalog;
        private static long _nextCampCaptureTick;
        private static long _campCapturedTick;
        private static Exception _campProjectionError;
        private readonly record struct ProjectionKey(Zone Zone, int X, int Y, int Z, long Revision);
        // Retain only the previous/current generation, never an ever-growing
        // location cache. Live counts and levels are rebuilt for every snapshot.
        private static ConcurrentDictionary<ProjectionKey, Vector3> _previousProjections = new();
        private static ConcurrentDictionary<ProjectionKey, Vector3> _buildingProjections = new();
        private static IPathfindingMgr _projectionProvider;

        /// <summary>Called once by the NPC service owner, before actor dispatch.
        /// Pure grouping/filtering happens independently; bounded native checks
        /// run in the existing parallel phase, never across movement/door phases.</summary>
        public static void PrepareCampPlanningTick()
        {
            try { PrepareCampPlanningTickCore(); }
            catch (Exception exception)
            {
                _campDraft = _campProjected = null;
                _nextCampCaptureTick = GameLoop.GameLoopTime + 5_000;
                Log.Error("Camp snapshot capture failed; retaining the previous catalog and retrying.", exception);
            }
        }

        private static void PrepareCampPlanningTickCore()
        {
            long now = GameLoop.GameLoopTime;
            _campBuilder ??= new("CampSnapshotPlanner", BuildCampCatalogDraft);
            if (_campBuilder.TryTake(out CampCatalogCell[] draft, out Exception error))
            {
                _campBuildInFlight = false;
                if (error != null)
                {
                    Log.Error("Camp snapshot planning failed; retaining the previous catalog and retrying.", error);
                    _nextCampCaptureTick = now + 5_000;
                }
                else
                {
                    if (!ReferenceEquals(_projectionProvider, PathfindingProvider.Instance))
                    {
                        _previousProjections.Clear();
                        _projectionProvider = PathfindingProvider.Instance;
                    }
                    _buildingProjections = new();
                    _campDraft = draft;
                    _campProjected = new CampCatalogCell[draft.Length];
                    _projectionCursor = 0;
                    _campProjectionError = null;
                }
            }

            if (_campDraft != null)
            {
                ProjectionWork.Clear();
                int end = Math.Min(_campDraft.Length, _projectionCursor + 128);
                for (int i = _projectionCursor; i < end; i++) ProjectionWork.Add(i);
                GameLoop.ExecuteForEach(ProjectionWork, ProjectionWork.Count, ProjectCamp);
                _projectionCursor = end;
                if (_campProjectionError != null)
                {
                    Log.Error("Camp navigation validation failed; retaining the previous complete catalog.", _campProjectionError);
                    _campDraft = _campProjected = null;
                    _campProjectionError = null;
                    _nextCampCaptureTick = now + 5_000;
                    return;
                }
                if (end == _campDraft.Length)
                {
                    // Publish an entire generation, including a genuinely empty
                    // result. A worker can never expose a partly built catalog.
                    CampCatalogCell[] published = _campProjected.Where(cell => cell != null).ToArray();
                    Volatile.Write(ref _campCatalog, published);
                    AutonomousDungeonPopulationPolicy.PublishCatalog(published
                        .Where(cell => cell.IsDungeon)
                        .Select(cell => (cell.Id, cell.RegionId, cell.LiveMobCount)));
                    _previousProjections = _buildingProjections;
                    _hasCampCatalog = true;
                    _campDraft = _campProjected = null;
                    // Start ahead of the previous 45-second expiry, preserving
                    // freshness while projection proceeds across short batches.
                    _nextCampCaptureTick = _campCapturedTick + 40_000;
                }
            }

            if (_campBuildInFlight || _campDraft != null || now < _nextCampCaptureTick) return;
            CampMonster[] snapshot = CaptureCampMonsters();
            _campCapturedTick = now;
            _campBuildInFlight = _campBuilder.TryRequest(snapshot);
        }

        private static CampMonster[] CaptureCampMonsters()
        {
            var monsters = new List<CampMonster>();
            foreach (Region region in WorldMgr.GetAllRegions())
            {
                if (region == null || !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion)) continue;
                foreach (GameObject actor in region.Objects)
                {
                    if (actor is not GameNPC npc || npc.ObjectState != GameObject.eObjectState.Active ||
                        !npc.IsAlive || npc.CurrentZone == null || !IsExperienceMonster(npc) ||
                        npc.Name != npc.Name.ToLowerInvariant() ||
                        AutonomousAuditedCampPolicy.IsBotExcludedNpc(npc)) continue;
                    AutonomousDungeonGoalCatalog.Point point = null;
                    if (npc.CurrentZone.IsDungeon) AutonomousDungeonGoalCatalog.TryGet(npc, out point);
                    monsters.Add(new(npc.InternalID ?? string.Empty, npc.Name, npc.X, npc.Y, npc.Z, npc.EffectiveLevel,
                        npc.CurrentRegionID, npc.CurrentZone, point));
                }
            }
            return monsters.ToArray();
        }

        private static void ProjectCamp(int index)
        {
            try { ProjectCampCore(index); }
            catch (Exception exception) { Interlocked.CompareExchange(ref _campProjectionError, exception, null); }
        }

        private static void ProjectCampCore(int index)
        {
            CampCatalogCell cell = _campDraft[index];
            if (!cell.NeedsProjection)
            {
                if (PathfindingProvider.Instance.HasNavmesh(cell.Zone)) _campProjected[index] = cell;
                return;
            }
            var key = new ProjectionKey(cell.Zone, cell.X, cell.Y, cell.Z, NavigationGeometryRevision.Read(cell.Zone));
            // Reuse only exact anchor/geometry matches. Doors changing state,
            // mesh reloads, new anchors and failed projections get real checks.
            bool starter = cell.Id.StartsWith(StarterCampPrefix, StringComparison.Ordinal);
            if (starter && StarterRouteFailures.ContainsKey(key))
                return;
            if (_previousProjections.TryGetValue(key, out Vector3 point) ||
                AutonomousRendezvousNavigation.TryChoosePoint(PathfindingProvider.Instance, cell.Zone,
                    new Vector3(cell.X, cell.Y, cell.Z), out point) &&
                (!starter || HasStarterRouteFromZoneHub(cell.Zone, point, key)))
            {
                _buildingProjections.TryAdd(key, point);
                _campProjected[index] = cell with
                {
                    X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y), Z = (int)Math.Round(point.Z)
                };
            }
        }

        // Starter camps are new, unaudited live clusters, so beyond the ordinary
        // walkable-anchor projection they must also be reachable on the navmesh
        // from where bots actually arrive in that zone: a complete corridor
        // from at least one stable master in the same zone. A zone without a
        // stable master keeps the ordinary projection rule used by every camp.
        private static readonly ConcurrentDictionary<ProjectionKey, bool> StarterRouteFailures = new();
        private static readonly ConcurrentDictionary<ushort, Vector3[]> ZoneHubs = new();

        private static bool HasStarterRouteFromZoneHub(Zone zone, Vector3 point, ProjectionKey key)
        {
            IPathfindingMgr nav = PathfindingProvider.Instance;
            Vector3[] hubs = ZoneHubs.GetOrAdd(zone.ID, _ =>
            {
                var found = new List<Vector3>();
                foreach (GameStableMaster master in zone.ZoneRegion?.Objects?.OfType<GameStableMaster>() ??
                                                     Enumerable.Empty<GameStableMaster>())
                {
                    if (master?.CurrentZone != zone) continue;
                    Vector3? floor = nav.GetClosestPoint(zone, new Vector3(master.X, master.Y, master.Z), 128, 128, 256,
                        nav.DefaultFilters);
                    if (floor.HasValue) found.Add(floor.Value);
                }
                return found.ToArray();
            });
            if (hubs.Length == 0)
                return true;
            foreach (Vector3 hub in hubs)
                if (AutonomousRendezvousNavigation.CanReachFrom(nav, zone, hub, point))
                    return true;
            StarterRouteFailures[key] = true;
            return false;
        }

        public static void StopCampPlanning()
        {
            StarterRouteFailures.Clear();
            ZoneHubs.Clear();
            PrepareRvrPlanningTick();
            _campBuilder?.Dispose();
            _campBuilder = null;
            _campDraft = _campProjected = null;
            _campCatalog = [];
            AutonomousDungeonPopulationPolicy.PublishCatalog(null);
            _campBuildInFlight = _hasCampCatalog = false;
            _campProjectionError = null;
            _nextCampCaptureTick = 0;
            ProjectionWork.Clear();
            _previousProjections.Clear();
            _buildingProjections.Clear();
            _projectionProvider = null;
        }
    }
}
