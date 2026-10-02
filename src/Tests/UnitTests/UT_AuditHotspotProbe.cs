using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Linq;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Opt-in evidence probe for live-log routing hotspots.  It loads only local
    /// navmeshes and opens the installed database read-only; it never starts a server
    /// or mutates a bot, a mesh, or a database row.
    /// </summary>
    [TestFixture, NonParallelizable, Explicit("Read-only installed-navmesh hotspot probe")]
    public class UT_AuditHotspotProbe
    {
        [Test]
        public void SeptemberSecondHotspotsResolveToConnectedFloor()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                if (!string.IsNullOrEmpty(native))
                    NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                        (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                string databasePath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH") ??
                    Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"));
                using var db = new SQLiteConnection($"Data Source={databasePath};Read Only=True;Pooling=False;");
                db.Open();

                var regions = new Dictionary<int, Region>();
                foreach (int regionId in new[] { 1, 21, 51, 100, 101, 129, 151, 181, 200, 201, 221 })
                {
                    var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                    typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(region, new RegionData { Id = (ushort)regionId });
                    typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(region, new List<Zone>());
                    regions[regionId] = region;
                    using var command = db.CreateCommand();
                    command.CommandText = "select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=@region";
                    command.Parameters.AddWithValue("@region", regionId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        ushort id = (ushort)reader.GetInt32(0);
                        Zone zone = new(region, id, reader.GetString(1), reader.GetInt32(2) * 8192,
                            reader.GetInt32(3) * 8192, reader.GetInt32(4) * 8192, reader.GetInt32(5) * 8192,
                            id, false, 0, false, 0, 0, 0, 0, 0);
                        ((List<Zone>)typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(region)).Add(zone);
                        LocalPathfindingMgr.LoadNavMesh(zone);
                        loaded.Add(zone);
                    }
                }

                var nav = PathfindingProvider.LocalPathfindingMgr;
                ProbeAuditedOutdoorTargets("large dragonfly", regions[51], nav,
                    new(529936, 537808, 3104),
                    [new(528150, 539061, 3140), new(529168, 539735, 3141), new(530832, 536774, 3087)]);
                ProbeAuditedOutdoorTargets("boobrie hatchling", regions[151], nav,
                    new(285151, 346544, 3085),
                    [new(282972, 344890, 3456), new(286527, 346846, 3163), new(288755, 343565, 3477)]);
                ProbeAuditedOutdoorTargets("feccan", regions[200], nav,
                    new(337977, 479973, 5234),
                    [new(337734, 478720, 5325), new(337942, 479912, 5237), new(339331, 476773, 5247)]);
                ProbeAuditedOutdoorTargets("huldu outcast", regions[100], nav,
                    new(812733, 722524, 5152),
                    [new(810855, 719902, 5034), new(811550, 721706, 5104), new(812680, 724343, 5336)]);
                ProbeAuditedOutdoorTargets("green serpent", regions[100], nav,
                    new(759936, 751403, 4625),
                    [new(757921, 743637, 4410), new(759817, 749618, 4568), new(760120, 751345, 4623)]);
                ProbeAuditedOutdoorFailure("large dragonfly raised pocket", regions[51], nav,
                    new(526102, 543403, 3434), new(526091, 543349, 3665));
                ProbeAuditedOutdoorFailure("boobrie hatchling ledge", regions[151], nav,
                    new(285151, 346544, 3085), new(285161, 346544, 3084));
                ProbeAuditedOutdoorFailure("feccan pocket", regions[200], nav,
                    new(337977, 479973, 5234), new(337913, 479981, 5235));
                ProbeAuditedOutdoorFailure("huldu outcast pocket", regions[100], nav,
                    new(812733, 722524, 5152), new(812738, 722572, 5235));
                ProbeAuditedOutdoorFailure("green serpent pocket", regions[100], nav,
                    new(759936, 751403, 4625), new(760047, 751365, 4623));
                VerifyAuditedCampEscape("large dragonfly", regions[51], nav,
                    new(526102, 543403, 3434));
                VerifyAuditedCampEscape("boobrie hatchling", regions[151], nav,
                    new(285151, 346544, 3085));
                VerifyAuditedCampEscape("feccan", regions[200], nav,
                    new(337977, 479973, 5234));
                VerifyAuditedCampEscape("huldu outcast", regions[100], nav,
                    new(812733, 722524, 5152));
                VerifyAuditedCampEscape("green serpent", regions[100], nav,
                    new(759936, 751403, 4625));
                VerifyNearbyUsable("Connacht/Shannon isolated source", 200, regions[200], nav,
                    new(318807, 629660, 4959), new(296110, 642245, 4853), true);
                VerifyNearbyUsable("Salisbury elevated source", 1, regions[1], nav,
                    new(556103, 560268, 2148), new(578266, 550198, 2866), true);
                VerifyNearbyUsable("Jordheim service pocket", 101, regions[101], nav,
                    new(31189, 27479, 8830), new(31740, 28020, 8776), false);
                VerifyNearbyUsable("Pheuloc path break A", 200, regions[200], nav,
                    new(315453, 623125, 6638), new(296068, 642187, 4853), true);
                VerifyNearbyUsable("Pheuloc path break B", 200, regions[200], nav,
                    new(316659, 622101, 6616), new(296068, 642187, 4853), true);
                Assert.That(AutonomousRouteHotspotRepair.IsJordheimServicePocket(101,
                    new(31189, 27479, 8830)), Is.True);
                Zone jordheim = regions[101].GetZone(31182, 27479);
                Assert.That(AutonomousRouteHotspotRepair.TryResolveJordheimServiceApproach(
                    nav, jordheim, 101, new(34100, 34600, 8008), new(31182, 27479, 8819), 256,
                    out Vector3 toraApproach), Is.True,
                    "Tora needs an interaction-range point connected to Jordheim's lower-city network");
                Vector3 jordheimNetwork = default;
                Assert.That(AutonomousRendezvousNavigation.TryChooseFixedPoint(nav, jordheim,
                    AutonomousRendezvousNavigation.JordheimMeetingPoint, out jordheimNetwork), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(Vector2.Distance(new(toraApproach.X, toraApproach.Y), new(31182, 27479)),
                        Is.LessThanOrEqualTo(248));
                    Assert.That(Vector3.Distance(toraApproach, new(31182, 27479, 8819)),
                        Is.LessThanOrEqualTo(248), "The safe point must remain in the real transaction radius");
                    Assert.That(Vector2.Distance(new(toraApproach.X, toraApproach.Y), new(31182, 27479)),
                        Is.GreaterThanOrEqualTo(96));
                    Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, jordheim,
                        toraApproach, jordheimNetwork), Is.True);
                    Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, jordheim,
                        jordheimNetwork, toraApproach), Is.True);
                    Assert.That(AutonomousRouteHotspotRepair.TryResolveJordheimServiceApproach(
                        nav, jordheim, 101, new(34100, 34600, 8008), new(31740, 28020, 8776), 256,
                        out _), Is.False, "Unrelated Jordheim services keep the general resolver");
                });
                Assert.That(AutonomousRouteHotspotRepair.TryResolveJordheimServiceApproach(
                    nav, jordheim, 101, new(34100, 34600, 8008), new(32499, 28664, 8830), 256,
                    out Vector3 cruellaApproach), Is.True,
                    "Cruella needs an interaction point reached from the open lower-city side");
                Assert.That(Vector3.Distance(cruellaApproach, new(32499, 28664, 8830)), Is.LessThanOrEqualTo(240));
                TestContext.WriteLine($"CRUELLA_APPROACH {cruellaApproach}");

                var camelotExit = new DbZonePoint { Id = 5, SourceRegion = 10, TargetRegion = 1 };
                Assert.That(AutonomousRouteHotspotRepair.TryResolveAuditedPortalLanding(
                    nav, regions[1], camelotExit, out Vector3 cotswoldLanding), Is.True);
                Zone cotswold = regions[1].GetZone((int)cotswoldLanding.X, (int)cotswoldLanding.Y);
                Vector3 cotswoldWitness = nav.GetClosestPoint(cotswold, new(560054, 513908, 2619),
                    96, 96, 256, nav.DefaultFilters).Value;
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, cotswold,
                    cotswoldLanding, cotswoldWitness), Is.True,
                    "Thedenhelm and Paladins must arrive on the Cotswold road network");

                var connachtExit = new DbZonePoint { Id = 26, SourceRegion = 201, TargetRegion = 200 };
                Assert.That(AutonomousRouteHotspotRepair.TryResolveAuditedPortalLanding(
                    nav, regions[200], connachtExit, out Vector3 connachtLanding), Is.True);
                Zone connacht = regions[200].GetZone((int)connachtLanding.X, (int)connachtLanding.Y);
                Vector3 connachtWitness = nav.GetClosestPoint(connacht, new(330135, 464853, 5571),
                    96, 96, 256, nav.DefaultFilters).Value;
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, connacht,
                    connachtLanding, connachtWitness), Is.True,
                    "The TNN exit must land directly on Connacht's ordinary road network");
                Assert.That(AutonomousRouteHotspotRepair.TryResolveAuditedPortalLanding(
                    nav, regions[200], new DbZonePoint { Id = 25, SourceRegion = 201, TargetRegion = 200 },
                    out _), Is.False, "No unaudited zone point may inherit either replacement landing");
                Assert.That(AutonomousRouteHotspotRepair.TryGetImmediateEscape(nav, regions[200], 200,
                    new(312969, 473100, 5254), out Vector3 connachtEscape), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(Vector2.Distance(new(connachtEscape.X, connachtEscape.Y),
                        new(311960, 470002)), Is.LessThan(128));
                    Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav,
                        regions[200].GetZone((int)connachtEscape.X, (int)connachtEscape.Y), connachtEscape), Is.True);
                    Assert.That(AutonomousRouteHotspotRepair.IsConnachtTransferLoopArea(200,
                        new(313848, 474886, 5344)), Is.True);
                    Assert.That(AutonomousRouteHotspotRepair.IsConnachtTransferLoopArea(200,
                        new(330135, 464853, 5571)), Is.False);
                });
                foreach ((string name, ushort regionId, Vector3 source) in new[]
                {
                    ("Rianaenan Shannon pocket", (ushort)200, new Vector3(298241, 636847, 4884)),
                    ("Nialinorron Shannon pocket", (ushort)200, new Vector3(311024, 638475, 4853)),
                    ("Nialeron Connacht pocket", (ushort)200, new Vector3(340988, 469188, 5203)),
                    ("Caoeorna Domnann pocket", (ushort)181, new Vector3(423162, 443589, 5955)),
                    ("Lough Derg orchard collision strip", (ushort)200, new Vector3(337920, 496976, 5136)),
                })
                {
                    Assert.That(AutonomousRouteHotspotRepair.TryGetImmediateEscape(nav, regions[regionId],
                        regionId, source, out Vector3 escape), Is.True, name);
                    Zone escapeZone = regions[regionId].GetZone((int)escape.X, (int)escape.Y);
                    Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, escapeZone, escape), Is.True, name);
                }

                foreach ((string name, ushort sourceRegion, ushort dungeonRegion, Vector3 source,
                    float maximumMove) in new[]
                {
                    ("Mithra Black Mountains pocket", (ushort)1, (ushort)21,
                        new Vector3(511683, 382045, 8288), 500f),
                    ("Mithra Camelot Hills stale height", (ushort)1, (ushort)21,
                        new Vector3(584092, 483576, 2587), 500f),
                    ("Mithra Salisbury seam pocket", (ushort)1, (ushort)21,
                        new Vector3(582507, 553028, 2276), 1_650f),
                    ("Muire Connacht north-edge pocket", (ushort)200, (ushort)221,
                        new Vector3(330480, 482970, 7690), 750f),
                })
                {
                    Assert.That(AutonomousRouteHotspotRepair.TryResolveStarterDungeonRouteSurface(
                        nav, regions[sourceRegion], sourceRegion, dungeonRegion, source,
                        out Vector3 recovery), Is.True, name);
                    Assert.That(Vector2.Distance(new(source.X, source.Y), new(recovery.X, recovery.Y)),
                        Is.LessThanOrEqualTo(maximumMove), name + " remains a bounded local repair");
                }
                Assert.That(AutonomousRouteHotspotRepair.TryGetImmediateEscape(nav, regions[221], 221,
                    new(32960, 31892, 16003), out Vector3 muireInteriorEscape), Is.True,
                    "Muire's audited entry-room collision corner needs a connected nearby escape");
                Assert.That(Vector2.Distance(new(32960, 31892),
                    new(muireInteriorEscape.X, muireInteriorEscape.Y)), Is.LessThan(1_000));

                var albionSiExit = new DbZonePoint
                {
                    Id = 154, SourceRegion = 51, SourceX = 525872, SourceY = 542106, SourceZ = 3173,
                    TargetRegion = 1
                };
                var midgardSiExit = new DbZonePoint
                {
                    Id = 166, SourceRegion = 151, SourceX = 294122, SourceY = 355624, SourceZ = 3570,
                    TargetRegion = 100
                };
                foreach ((string name, Region region, DbZonePoint edge, Vector3 source) in new[]
                {
                    ("Adalelle", regions[51], albionSiExit, new Vector3(532550, 544857, 3702)),
                    ("Adaorbeth", regions[51], albionSiExit, new Vector3(530813, 543371, 3702)),
                    ("Mularn stable arrival", regions[100], new DbZonePoint
                    {
                        Id = 165, SourceRegion = 100, SourceX = 808830, SourceY = 725039, SourceZ = 4882,
                        TargetRegion = 151
                    }, new Vector3(803743, 722129, 4684)),
                    ("Ulfungrim", regions[151], midgardSiExit, new Vector3(304535, 370789, 3241)),
                })
                {
                    Assert.That(AutonomousRouteHotspotRepair.TryResolveAuditedCrossingSource(
                        nav, region, edge, source, out Vector3 repairedSource), Is.True, name);
                    Zone sourceZone = region.GetZone((int)source.X, (int)source.Y);
                    Vector3 portalFloor = nav.GetClosestPoint(sourceZone,
                        new(edge.SourceX, edge.SourceY, edge.SourceZ), 64, 64, 256, nav.DefaultFilters).Value;
                    Assert.Multiple(() =>
                    {
                        Assert.That(Vector2.Distance(new(repairedSource.X, repairedSource.Y),
                            new(source.X, source.Y)), Is.LessThanOrEqualTo(name == "Adalelle" ? 320 : 64));
                        Assert.That(MathF.Abs(repairedSource.Z - source.Z),
                            Is.LessThanOrEqualTo(name == "Mularn stable arrival" ? 192 : 128));
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, sourceZone,
                            repairedSource, portalFloor), Is.True, name + " reaches the real SI exit");
                    });
                }
                Assert.That(AutonomousRouteHotspotRepair.TryResolveAuditedCrossingSource(
                    nav, regions[51], albionSiExit, new Vector3(540000, 540000, 3702), out _), Is.False,
                    "Unrelated Isle of Glass positions must not inherit the repair");
                Assert.That(AutonomousRouteHotspotRepair.TryResolveAuditedCrossingSource(
                    nav, regions[51], new DbZonePoint { Id = 155, SourceRegion = 51 },
                    new Vector3(532550, 544857, 3702), out _), Is.False,
                    "An unrelated portal must not inherit the repair");
                VerifyKeepApproach("Grallarhorn Faste", regions[100], nav,
                    new(686076, 707808, 5800), new(677480, 710406, 6912));
                VerifyKeepApproach("Dun Crimthain", regions[200], nav,
                    new(427511, 373605, 2211), new(437607, 368014, 4088));
                VerifyKeepApproach("Mjollner Faste mountain shelf", regions[100], nav,
                    new(780328, 625531, 8436), new(771929, 626751, 7184));
                VerifyKeepApproach("Hibernia Portal Keep disconnected shelf", regions[1], nav,
                    new(609655, 303980, 2947), new(605589, 293789, 4839));
                foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
                {
                    Assert.That(AutonomousRvrStaging.TryGetBorderKeep(realm, out var keep), Is.True);
                    Vector3 staging = default;
                    Zone zone = null;
                    bool found = AutonomousRvrStaging.CandidateAnchors(keep).Any(anchor =>
                    {
                        zone = regions[keep.RegionId].GetZone((int)anchor.X, (int)anchor.Y);
                        return zone != null && AutonomousRendezvousNavigation.TryChoosePoint(nav, zone, anchor, out staging);
                    });
                    Assert.That(found, Is.True, $"{keep.Name} needs connected staging ground inside its safe area");
                    Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, zone, staging), Is.True);
                }
            }
            finally
            {
                foreach (Zone zone in loaded)
                    LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }

        private static void VerifyKeepApproach(string name, Region region, IPathfindingMgr nav,
            Vector3 actor, Vector3 keep)
        {
            Zone zone = region.GetZone((int)actor.X, (int)actor.Y);
            Assert.That(zone, Is.Not.Null, $"{name} start must be in a loaded zone");
            Assert.That(AutonomousRvrApproach.TryResolve(nav, zone, actor, keep, out Vector3 approach),
                Is.True, $"{name} needs a connected perimeter approach");
            float radius = Vector2.Distance(new(approach.X, approach.Y), new(keep.X, keep.Y));
            Assert.Multiple(() =>
            {
                Assert.That(radius, Is.InRange(650, 3_500), $"{name} approach must remain outside the keep geometry");
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, actor, approach), Is.True,
                    $"{name} approach must be connected from the recorded live failure surface");
            });
        }


        [Test]
        public void PrintHotspotFloorExitAndCorridorEvidence()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                if (!string.IsNullOrEmpty(native))
                    NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                        (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                string databasePath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH") ??
                    Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"));
                using var db = new SQLiteConnection($"Data Source={databasePath};Read Only=True;Pooling=False;");
                db.Open();

                var regions = new Dictionary<int, Region>();
                foreach (int regionId in new[] { 1, 10, 21, 51, 100, 101, 129, 151, 181, 200, 201, 221 })
                {
                    var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                    typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(region, new RegionData { Id = (ushort)regionId });
                    typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(region, new List<Zone>());
                    regions[regionId] = region;
                    using var command = db.CreateCommand();
                    command.CommandText = "select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=@region";
                    command.Parameters.AddWithValue("@region", regionId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        ushort id = (ushort)reader.GetInt32(0);
                        Zone zone = new(region, id, reader.GetString(1), reader.GetInt32(2) * 8192,
                            reader.GetInt32(3) * 8192, reader.GetInt32(4) * 8192, reader.GetInt32(5) * 8192,
                            id, false, 0, false, 0, 0, 0, 0, 0);
                        ((List<Zone>)typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(region)).Add(zone);
                        LocalPathfindingMgr.LoadNavMesh(zone);
                        loaded.Add(zone);
                    }
                }

                var nav = PathfindingProvider.LocalPathfindingMgr;
                Vector3 meeting = AutonomousRendezvousNavigation.JordheimMeetingPoint;
                foreach (var hotspot in new[]
                {
                    (Region: 10, Position: new Vector3(40935,26512,8256)),
                    (Region: 10, Position: new Vector3(35953,30436,7991)),
                    (Region: 100, Position: new Vector3(808662,724912,4883)),
                    (Region: 200, Position: new Vector3(348000,493000,5000)),
                    (Region: 201, Position: new Vector3(17883,35855,6265)),
                    (Region: 1, Position: new Vector3(603265,522848,3123))
                })
                {
                    using var edges = db.CreateCommand();
                    edges.CommandText = "select Id,SourceX,SourceY,SourceZ from ZonePoint where SourceRegion=" + hotspot.Region;
                    using var edgeReader = edges.ExecuteReader();
                    while (edgeReader.Read())
                    {
                        Vector3 portal = new(edgeReader.GetInt32(1),edgeReader.GetInt32(2),edgeReader.GetInt32(3));
                        if (Vector3.Distance(hotspot.Position, portal) > 15000) continue;
                        Zone startZone = regions[hotspot.Region].GetZone((int)hotspot.Position.X,(int)hotspot.Position.Y);
                        Zone endZone = regions[hotspot.Region].GetZone((int)portal.X,(int)portal.Y);
                        bool approachOk = startZone == endZone && AutonomousZonePointApproach.TryResolve(nav,
                            startZone, hotspot.Position, portal, 190, out _);
                        TestContext.WriteLine($"SEPT9_ENDPOINT region={hotspot.Region} start={hotspot.Position} edge={edgeReader.GetInt32(0)} portal={portal} connectedApproach={approachOk}");
                    }
                }
                Zone jordheim = regions[101].GetZone((int)meeting.X, (int)meeting.Y);
                Vector3 jordheimFloor = nav.GetClosestPoint(jordheim, meeting, 48, 48, 96, nav.DefaultFilters).Value;
                Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, jordheim, jordheimFloor), Is.True);
                foreach (Vector3 end in new[] { new Vector3(32020,28294,8803), new Vector3(32869,36103,8002) })
                {
                    Vector3 endpoint = nav.GetClosestPoint(jordheim, end, 48, 48, 96, nav.DefaultFilters).Value;
                    Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, jordheim, endpoint, jordheimFloor), Is.True, "Jordheim -> open meetup");
                    Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, jordheim, jordheimFloor, endpoint), Is.True, "Jordheim meetup -> exit/bank");
                }
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Assert.That(AutonomousRendezvousNavigation.TryChooseFixedPoint(nav, jordheim, meeting, out Vector3 chosen), Is.True);
                    Assert.That(chosen.Z, Is.LessThan(8200), "Stay off the vault shelf");
                    Assert.That(Vector3.Distance(jordheimFloor, chosen), Is.LessThanOrEqualTo(1));
                    foreach (int radius in new[] { 64, 104, 136 })
                    for (int slot = 0; slot < 32; slot++)
                    {
                        float angle = slot * MathF.PI / 16;
                        Vector3 target = chosen + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0);
                        Vector3? standing = AutonomousNavigationSurface.MoveAlongGround(nav, jordheim, chosen, target);
                        Assert.That(standing.HasValue, Is.True);
                        Assert.That(Vector2.Distance(new(standing.Value.X, standing.Value.Y), new(target.X, target.Y)), Is.LessThan(2));
                        Assert.That(standing.Value.Z, Is.LessThan(8200));
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, jordheim, chosen, standing.Value), Is.True);
                    }
                }
                TestContext.WriteLine($"JORDHEIM_MEETUP_VALIDATED floor={jordheimFloor}; bank/exit bidirectional; 768 formation positions on low ground");
                Probe("Black prop-top -> Bombard", regions[1], nav,
                    new(515616, 494274, 3397), new(515649, 496745, 3352));
                Probe("Black suspected island -> Yaren", regions[1], nav,
                    new(531481, 479508, 2280), new(529218, 477805, 2200));
                Probe("Domnann approach -> region-200 portal", regions[181], nav,
                    new(423160, 444324, 5955), new(423900, 440147, 5998));
                Probe("Connacht logged start -> proven outside road", regions[200], nav,
                    new(312969, 473100, 5254), new(313848, 474886, 5344));
                Probe("East Svealand logged start -> Rundorik", regions[100], nav,
                    new(748652, 749959, 4532), new(749808, 814018, 4408));
                Probe("Mularn stable arrival -> SI portal 165", regions[100], nav,
                    new(803743, 722129, 4684), new(808830, 725039, 4882));
                Probe("Mularn stable arrival -> Stor Gothi Annark", regions[100], nav,
                    new(803743, 722129, 4684), new(804367, 723895, 4680));
                VerifyItinerary("Arnenbrand Nisse camp", regions[129], nav,
                    new(34693, 33173, 16447), new(33835, 32891, 16197));
                VerifyItinerary("Branelaedan Lough Derg to Connacht camp", regions[200], nav,
                    new(333218, 516107, 4695), new(330135, 464853, 5571));

                PrintNearestConnectedFloor("Muire exterior north-edge pocket", regions[200], nav,
                    new(330480, 482970, 7690), new(322927, 458013, 6673), 30_000);
                PrintNearestConnectedFloor("Muire interior collision pocket", regions[221], nav,
                    new(32960, 31892, 16003), new(31120, 29939, 16239), 6_000, 250);
                PrintNearestConnectedFloor("Mithra entrance no-movement cluster", regions[21], nav,
                    new(32500, 32000, 16000), new(33150, 32732, 16480), 6_000);
                PrintNearestConnectedFloor("Lough Derg formation pocket A", regions[200], nav,
                    new(335101, 496030, 5100), new(342015, 498967, 5000), 5_000, 125);
                PrintNearestConnectedFloor("Lough Derg formation pocket B", regions[200], nav,
                    new(338246, 498319, 5100), new(342015, 498967, 5000), 5_000, 125);
                PrintNearestConnectedFloor("Lough Derg formation pocket C", regions[200], nav,
                    new(335558, 493005, 5100), new(342015, 498967, 5000), 5_000, 125);
                PrintNearestConnectedFloor("Lough Derg formation pocket D", regions[200], nav,
                    new(335542, 499585, 5100), new(342015, 498967, 5000), 5_000, 125);
                PrintNearestConnectedFloor("Domnann catty sylvanshade pocket", regions[181], nav,
                    new(404222, 443278, 4435), new(404356, 447740, 4435), 5_000, 125);
                foreach ((string name, Vector3 source) in new[]
                {
                    ("Muire repeated pocket A", new Vector3(31900, 32669, 16000)),
                    ("Muire repeated pocket B", new Vector3(30567, 34164, 16000)),
                    ("Muire repeated pocket C", new Vector3(29811, 32799, 16000)),
                })
                    PrintNearestConnectedFloor(name, regions[221], nav, source,
                        new(31120, 29939, 16239), 4_000, 125);
                PrintNearestConnectedFloor("Black Mountains North Mithra approach pocket", regions[1], nav,
                    new(511683, 382045, 8288), new(603376, 523018, 3136), 20_000);
                foreach ((string name, Vector3 source) in new[]
                {
                    ("Mithra approach Hareralric", new Vector3(584092, 483576, 2587)),
                    ("Mithra approach Adaiorora", new Vector3(578211, 537750, 2776)),
                    ("Mithra approach Theaeisine", new Vector3(579836, 547185, 2559)),
                    ("Mithra approach Goderorren", new Vector3(583648, 544902, 2604)),
                    ("Mithra approach Adaenbeth", new Vector3(516576, 372299, 8211)),
                    ("Mithra approach Maraeislyn", new Vector3(574149, 552064, 2160)),
                    ("Mithra approach Godoric", new Vector3(561625, 510236, 2416)),
                    ("Mithra approach Aelenenbeth A", new Vector3(585882, 535280, 1917)),
                    ("Mithra approach Aelenenbeth B", new Vector3(585946, 535801, 2144)),
                    ("Mithra approach Seranne", new Vector3(582507, 553028, 2276)),
                    ("Mithra approach Gareorhelm", new Vector3(562439, 568657, 2185)),
                    ("Mithra approach Gisenora", new Vector3(565820, 522486, 2431)),
                    ("Mithra approach Elaealbel", new Vector3(578625, 546834, 2561)),
                })
                    PrintNearestConnectedFloor(name, regions[1], nav, source,
                        new(603376, 523018, 3136), 5_000);
                foreach ((string name, Vector3 source) in new[]
                {
                    ("Mithra SI approach Garowell", new Vector3(532666, 544957, 4470)),
                    ("Mithra SI approach Roselenanne", new Vector3(532661, 544953, 4469)),
                    ("Mithra SI approach Elaialine", new Vector3(530569, 543158, 3540)),
                })
                    PrintNearestConnectedFloor(name, regions[51], nav, source,
                        new(525872, 542106, 3173), 5_000);

                // Current-run recurring clusters. These exact coordinates are
                // retained as installed-mesh regression evidence.
                Probe("Connacht TNN portal-14 approach", regions[200], nav,
                    new(311840, 474275, 5222), new(311643, 474288, 5220));
                Assert.That(TryFindConnectedApproach(regions[200], nav,
                    new(311840, 474275, 5222), new(311643, 474288, 5220), 190, out _), Is.True,
                    "The installed Connacht mesh now exposes a connected bounded approach to portal 14");
                Zone portal14Zone = regions[200].GetZone(311840, 474275);
                Assert.That(AutonomousNavigationSurface.TryFloor(nav, portal14Zone,
                    new(311840, 474275, 5222), out Vector3 portal14Current), Is.True);
                Vector3 portal14Source = nav.GetClosestPoint(portal14Zone,
                    new(311643, 474288, 5220), 48, 48, 192, nav.DefaultFilters).Value;
                Assert.Multiple(() =>
                {
                    Assert.That(Vector2.Distance(new(portal14Current.X, portal14Current.Y),
                        new(portal14Source.X, portal14Source.Y)), Is.LessThanOrEqualTo(208));
                    Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, portal14Zone, portal14Current), Is.True);
                    Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, portal14Zone, portal14Source), Is.True);
                    Assert.That(AutonomousWorldBotController.CanUsePortal14Endpoint(14,
                        (int)Math.Ceiling(Vector2.Distance(new(portal14Current.X, portal14Current.Y),
                            new(portal14Source.X, portal14Source.Y))),
                        MathF.Abs(portal14Current.Z - 5220), true, true, true), Is.True);
                    Assert.That(AutonomousWorldBotController.CanUsePortal14Endpoint(15, 197, 2, true, true, true), Is.False,
                        "The bounded exception cannot affect any other outdoor portal");
                });
                Probe("Domnann bard trainer Iian approach", regions[181], nav,
                    new(422093, 448048, 5955), new(422351, 446567, 5976));
                Assert.That(TryFindConnectedApproach(regions[181], nav,
                    new(422093, 448048, 5955), new(422351, 446567, 5976), 280, out _), Is.False,
                    "Iian's blocked interior approach must be rejected rather than looped");
                Probe("Isle of Glass portal-154 elevated approach", regions[51], nav,
                    new(525872, 542935, 3504), new(525872, 542106, 3173));
                Assert.That(TryFindConnectedApproach(regions[51], nav,
                    new(525872, 542935, 3504), new(525872, 542106, 3173), 190, out _), Is.False,
                    "The height-separated portal-154 surface must be rejected rather than side-stepped");
                Probe("Midgard failed rendezvous slot", regions[100], nav,
                    new(745917, 764677, 4649), new(745909, 763418, 4638));
                Probe("Albion stable source component A", regions[1], nav,
                    new(503603, 475016, 3084), new(515649, 496745, 3352));
                Probe("Albion stable source component B", regions[1], nav,
                    new(511683, 382045, 8288), new(529218, 477805, 2200));

                Probe("Linaorora", regions[1], nav, new(531597,479471,2245), new(531576,479561,2200));
                Probe("Yselenbel", regions[1], nav, new(508560,477186,2328), new(519828,473604,3212));
                Probe("Ivareunar", regions[100], nav, new(747968,750104,4530), new(759111,745070,4416));
                Probe("Haldenvald", regions[100], nav, new(744203,750638,4523), new(724951,760273,4529));
                Probe("Coningan", regions[200], nav, new(344410,546394,5533), new(348198,492819,5240));
                Probe("Eogoinan", regions[200], nav, new(327461,475257,7203), new(327643,475079,7070));
                Probe("Maerieldra", regions[200], nav, new(329738,487146,5472), new(328620,486689,9383));
                Probe("Dairren", regions[181], nav, new(425734,447046,5965), new(424924,445575,5977));
                Probe("Eilra", regions[200], nav, new(347820,491607,5178), new(355296,492453,5221));
                Probe("Eogaingan", regions[200], nav, new(346486,493829,5165), new(347799,491071,5236));
                Probe("Orlarielra", regions[201], nav, new(18714,36113,6251), new(22898,27768,7230));

                if (!string.IsNullOrEmpty(native))
                {
                    // Replay height drift on slopes: grounded bot queries must
                    // use the reached polygon, while the old 2D API is unchanged.
                    foreach (Vector3 start in new[] { new Vector3(747968,750104,4609.828f), new Vector3(744203,750638,4613.189f) })
                    {
                        Zone zone = regions[100].GetZone((int)start.X,(int)start.Y);
                        Vector3 desired = start + new Vector3(0,340,0);
                        Vector3? grounded = nav.GetMoveAlongSurfaceGrounded(zone,start,desired,nav.DefaultFilters);
                        Assert.That(grounded.HasValue, Is.True);
                        Vector3? floor = nav.GetClosestPoint(zone,grounded.Value,2,2,4,nav.DefaultFilters);
                        Assert.That(floor.HasValue, Is.True);
                        Assert.That(Math.Abs(floor.Value.Z-grounded.Value.Z), Is.LessThan(2));
                        TestContext.WriteLine($"GROUNDED_SURFACE start={start} legacy={nav.GetMoveAlongSurface(zone,start,desired,nav.DefaultFilters)} grounded={grounded}");
                    }
                    foreach (var p in new[] { new Vector3(531603,479443,2248), new Vector3(508537,477173,2321) })
                    {
                        Zone zone = regions[1].GetZone((int)p.X,(int)p.Y);
                        Point3D bind = BotReleaseBindPoints.Resolve(nav,zone,p);
                        Assert.That(bind == null || AutonomousRendezvousNavigation.HasLocalExit(nav,zone,new(bind.X,bind.Y,bind.Z)), Is.True);
                        TestContext.WriteLine($"BIND_FILTER raw={p} usable={bind != null}");
                    }
                    Assert.That(AutonomousRendezvousNavigation.CanReachFrom(nav,regions[181].GetZone(425734,447046),
                        new(425734,447046,5965),new(424924,445575,5988.079f)), Is.False, "Dairren must not get a disconnected meetup");
                    Assert.That(AutonomousRendezvousNavigation.CanReachFrom(nav,regions[200].GetZone(327461,475257),
                        new(327461,475257,7203),new(327643,475079,7069)), Is.False, "Eogoinan must not get a disconnected meetup");
                    Assert.That(AutonomousRendezvousNavigation.CanReachFrom(nav,regions[100].GetZone(747968,750104),
                        new(747968,750104,4530),new(759111,745070,4416)), Is.True, "Ivareunar's ground-corrected route is complete");
                    Assert.That(AutonomousRendezvousNavigation.CanReachFrom(nav,regions[200].GetZone(347820,491607),
                        new(347820,491607,5178),new(355296,492453,5221)), Is.True, "Eilra's ground-corrected route is complete");
                }

                if (Environment.GetEnvironmentVariable("OFFLINE_DAOC_AUDIT_ROSTER") == "1")
                {
                    using var rosterCommand = db.CreateCommand();
                    rosterCommand.CommandText = "select BotId,Name,Realm,RegionId,X,Y,Z from offline_world_bots where IsRetired=0";
                    using var roster = rosterCommand.ExecuteReader();
                    int inspected = 0, skipped = 0;
                    while (roster.Read())
                    {
                        int regionId = roster.GetInt32(3), x = roster.GetInt32(4), y = roster.GetInt32(5), z = roster.GetInt32(6);
                        if (!regions.TryGetValue(regionId, out Region region)) { skipped++; continue; }
                        Zone zone = region.GetZone(x, y);
                        if (zone == null || !nav.HasNavmesh(zone)) { skipped++; continue; }
                        inspected++;
                        Vector3 position = new(x,y,z);
                        Vector3? floor = nav.GetClosestPoint(zone, position, 2, 2, 4096, nav.DefaultFilters);
                        if (!floor.HasValue) continue;
                        bool below = floor.Value.Z - z > 64;
                        bool isolated = !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value);
                        if (!below && !isolated) continue;
                        Vector3 destination = floor.Value;
                        int targetRegion = regionId;
                        Zone targetZone = zone;
                        if (isolated || floor.Value.Z-z>128)
                        {
                            var capital = AutonomousStuckWatchdog.SafeCapitalFor((eRealm)roster.GetInt32(2));
                            targetRegion = capital.RegionId;
                            targetZone = regions[targetRegion].GetZone(capital.X, capital.Y);
                            destination = nav.GetClosestPoint(targetZone, new(capital.X, capital.Y, capital.Z), 48,48,128,nav.DefaultFilters) ?? new(capital.X,capital.Y,capital.Z);
                        }
                        Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav,targetZone,destination), Is.True, "Recovery point must have an exit");
                        TestContext.WriteLine("ROSTER_REPAIR " + JsonSerializer.Serialize(new { id=roster.GetInt64(0), name=roster.GetString(1), region=regionId,x,y,z,
                            reason=below ? "below mesh ground" : "isolated walkable prop", toRegion=targetRegion,
                            toX=(int)Math.Round(destination.X),toY=(int)Math.Round(destination.Y),toZ=(int)Math.Round(destination.Z),
                            toZone=(int)targetZone.ID,toZoneName=targetZone.Description }));
                    }
                    TestContext.WriteLine($"ROSTER_AUDIT inspected={inspected} skipped={skipped}");
                }

                Assert.That(loaded.Count, Is.GreaterThan(0));
                Assert.That(loaded.TrueForAll(nav.HasNavmesh), Is.True);
            }
            finally
            {
                foreach (Zone zone in loaded)
                    LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }

        private static void Probe(string name, Region region, IPathfindingMgr nav, Vector3 start, Vector3 goal)
        {
            Zone startZone = region.GetZone((int)start.X, (int)start.Y);
            Zone goalZone = region.GetZone((int)goal.X, (int)goal.Y);
            Assert.That(startZone, Is.Not.Null, $"{name}: start zone");
            Assert.That(goalZone, Is.Not.Null, $"{name}: goal zone");

            Vector3? strictStart = nav.GetClosestPoint(startZone, start, 64, 64, 128, nav.DefaultFilters);
            Vector3? broadStart = nav.GetClosestPoint(startZone, start, 512, 512, 4096, nav.DefaultFilters);
            Vector3? strictGoal = nav.GetClosestPoint(goalZone, goal, 128, 128, 256, nav.DefaultFilters);
            bool startExit = strictStart.HasValue && AutonomousRendezvousNavigation.HasLocalExit(nav, startZone, strictStart.Value);
            bool goalExit = strictGoal.HasValue && AutonomousRendezvousNavigation.HasLocalExit(nav, goalZone, strictGoal.Value);
            bool corridor = strictStart.HasValue && strictGoal.HasValue && startZone == goalZone &&
                AutonomousZoneItinerary.HasCompleteCorridor(nav, startZone, strictStart.Value, strictGoal.Value);
            bool repairFound = AutonomousRendezvousNavigation.TryChoosePoint(nav, goalZone, goal, out Vector3 repair);

            WrappedPathfindingNode[] nodes = new WrappedPathfindingNode[512];
            PathfindingResult rawPath = startZone == goalZone ? nav.GetPathStraight(startZone, start, goal, nav.DefaultFilters, nodes) : default;
            Vector3 rawLast = rawPath.NodeCount > 0 ? nodes[rawPath.NodeCount - 1].Position : default;
            PathfindingResult direct = strictStart.HasValue && strictGoal.HasValue && startZone == goalZone
                ? nav.GetPathStraight(startZone, strictStart.Value, strictGoal.Value, nav.DefaultFilters, nodes)
                : default;
            TestContext.WriteLine($"{name}: rawStart={start} rawGoal={goal} startZone={startZone.ID} goalZone={goalZone.ID} " +
                $"strictStart={strictStart} broadStart={broadStart} strictGoal={strictGoal} " +
                $"startLocalExit={startExit} goalLocalExit={goalExit} completeCorridor={corridor} " +
                $"pathStatus={direct.Status} nodes={direct.NodeCount} rawPath={rawPath.Status} rawLast={rawLast} repairFound={repairFound} repair={repair}");
            if (Environment.GetEnvironmentVariable("OFFLINE_DAOC_REQUIRE_REPAIRS") == "1" &&
                startZone.ID is 2 or 181)
            {
                Assert.That(startExit && corridor, Is.True, name);
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, startZone,
                    strictGoal.Value, strictStart.Value), Is.True, name + " reverse route");
                if (startZone.ID == 181)
                    Assert.That(strictStart.Value.Z, Is.GreaterThan(6000), "Never route underneath the solid platform");
            }
            if (!startExit && Environment.GetEnvironmentVariable("OFFLINE_DAOC_SCAN_ISLAND") == "1")
            {
                for (int x = -350; x <= 350; x += 25)
                for (int y = -350; y <= 350; y += 25)
                {
                    Vector3? floor = nav.GetClosestPoint(startZone, start + new Vector3(x, y, 0), 12, 12, 160, nav.DefaultFilters);
                    if (!floor.HasValue) continue;
                    bool island = AutonomousZoneItinerary.HasCompleteCorridor(nav, startZone, strictStart.Value, floor.Value);
                    bool road = AutonomousZoneItinerary.HasCompleteCorridor(nav, startZone, floor.Value, strictGoal.Value);
                    TestContext.WriteLine($"GRID,{startZone.ID},{floor.Value.X},{floor.Value.Y},{floor.Value.Z},{island},{road}");
                }
            }
        }

        private static void ProbeAuditedOutdoorTargets(string name, Region region, IPathfindingMgr nav,
            Vector3 start, IEnumerable<Vector3> targets)
        {
            Zone zone = region.GetZone((int)start.X, (int)start.Y);
            Assert.That(zone, Is.Not.Null, name + " start zone");
            foreach (Vector3 target in targets)
            {
                bool reachable = region.GetZone((int)target.X, (int)target.Y) == zone &&
                    AutonomousDungeonTargetRoute.CanReach(nav, zone, start, target);
                TestContext.WriteLine($"AUDITED_OUTDOOR_TARGET {name}: start={start} target={target} reachable={reachable}");
            }
        }

        private static void ProbeAuditedOutdoorFailure(string name, Region region,
            IPathfindingMgr nav, Vector3 start, Vector3 target)
        {
            Zone zone = region.GetZone((int)start.X, (int)start.Y);
            Assert.That(zone, Is.Not.Null, name + " zone");
            Assert.That(region.GetZone((int)target.X, (int)target.Y), Is.EqualTo(zone), name + " target zone");
            bool reachable = AutonomousDungeonTargetRoute.CanReach(nav, zone, start, target);
            TestContext.WriteLine($"AUDITED_OUTDOOR_FAILURE {name}: start={start} target={target} reachable={reachable}");
        }

        private static void VerifyAuditedCampEscape(string name, Region region,
            IPathfindingMgr nav, Vector3 failure)
        {
            Assert.That(AutonomousRouteHotspotRepair.TryGetAuditedCampEscape(nav, region,
                region.ID, name, failure, out Vector3 escape), Is.True, name + " audited escape");
            Zone zone = region.GetZone((int)escape.X, (int)escape.Y);
            Assert.That(zone, Is.Not.Null, name + " escape zone");
            Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, zone, escape), Is.True,
                name + " escape local exit");
            Assert.That(AutonomousRouteHotspotRepair.TryGetAuditedCampEscape(nav, region,
                region.ID, "unrelated creature", failure, out _), Is.False,
                name + " must not affect another objective");
            Assert.That(AutonomousRouteHotspotRepair.TryGetAuditedCampEscape(nav, region,
                region.ID, name, failure + new Vector3(5_000, 5_000, 0), out _), Is.False,
                name + " must stay inside audited footprint");
        }

        private static void PrintNearestConnectedFloor(string name, Region region, IPathfindingMgr nav,
            Vector3 source, Vector3 goal, int maximumRadius, int minimumRadius = 0)
        {
            Zone sourceZone = region.GetZone((int)source.X, (int)source.Y);
            Zone goalZone = region.GetZone((int)goal.X, (int)goal.Y);
            Assert.That(sourceZone, Is.Not.Null, name + " source zone");
            Assert.That(goalZone, Is.Not.Null, name + " goal zone");
            Vector3 destination = nav.GetClosestPoint(goalZone, goal, 96, 96, 512, nav.DefaultFilters) ?? goal;

            for (int radius = minimumRadius; radius <= maximumRadius; radius += 250)
            {
                int angleStep = radius == 0 ? 360 : 10;
                for (int angle = 0; angle < 360; angle += angleStep)
                {
                    float radians = angle * MathF.PI / 180f;
                    Vector3 raw = source + new Vector3(MathF.Cos(radians) * radius,
                        MathF.Sin(radians) * radius, 0);
                    Zone candidateZone = region.GetZone((int)raw.X, (int)raw.Y);
                    if (candidateZone == null)
                        continue;
                    Vector3? candidate = nav.GetClosestPoint(candidateZone, raw, 48, 48, 512, nav.DefaultFilters);
                    if (!candidate.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, candidateZone, candidate.Value))
                        continue;

                    bool reaches;
                    AutonomousZoneBoundaryRouting.Step nextStep = default;
                    if (candidateZone == goalZone)
                    {
                        reaches = AutonomousZoneItinerary.HasCompleteCorridor(nav, goalZone,
                            candidate.Value, destination);
                    }
                    else
                    {
                        reaches = AutonomousZoneItinerary.TryNextStep(region, candidateZone, goalZone,
                            candidate.Value, destination, nav, out nextStep);
                    }
                    if (!reaches)
                        continue;

                    TestContext.WriteLine($"NEAREST_CONNECTED {name}: radius={radius} raw={raw} floor={candidate.Value} " +
                        $"zone={candidateZone.ID} goalZone={goalZone.ID} nextInside={nextStep.Inside} nextOutside={nextStep.Outside}");
                    return;
                }
            }

            Assert.Fail($"{name}: no connected floor found within {maximumRadius} units");
        }

        private static bool TryFindConnectedApproach(Region region, IPathfindingMgr nav,
            Vector3 start, Vector3 target, int radius, out Vector3 approach)
        {
            approach = default;
            Zone zone = region.GetZone((int)target.X, (int)target.Y);
            if (zone == null || region.GetZone((int)start.X, (int)start.Y) != zone ||
                !AutonomousNavigationSurface.TryFloor(nav, zone, start, out start))
                return false;

            var candidates = new List<Vector3>();
            void Add(Vector3 raw, int xy, int z)
            {
                Vector3? floor = nav.GetClosestPoint(zone, raw, xy, xy, z, nav.DefaultFilters);
                if (floor.HasValue && Vector2.Distance(new(floor.Value.X, floor.Value.Y), new(target.X, target.Y)) <= radius &&
                    MathF.Abs(floor.Value.Z - target.Z) <= 256 &&
                    !candidates.Any(existing => Vector3.DistanceSquared(existing, floor.Value) < 24 * 24))
                    candidates.Add(floor.Value);
            }
            Add(target, 48, 192);
            foreach (int ring in new[] { Math.Max(32, radius / 3), Math.Max(48, radius * 2 / 3), Math.Max(64, radius - 20) }.Distinct())
            for (int angle = 0; angle < 360; angle += 30)
            {
                float radians = angle * MathF.PI / 180f;
                Add(target + new Vector3(MathF.Cos(radians) * ring, MathF.Sin(radians) * ring, 0), 36, 128);
            }
            foreach (Vector3 candidate in candidates.OrderBy(point => Vector3.DistanceSquared(start, point)))
            {
                if (!AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, candidate)) continue;
                approach = candidate;
                return true;
            }
            return false;
        }

        private static void VerifyNearbyUsable(string name, ushort regionId, Region region, IPathfindingMgr nav,
            Vector3 source, Vector3 destination, bool expectLocalRepair)
        {
            Zone sourceZone = region.GetZone((int)source.X, (int)source.Y);
            Zone destinationZone = region.GetZone((int)destination.X, (int)destination.Y);
            Assert.That(sourceZone, Is.Not.Null, name + " source zone");
            Assert.That(destinationZone, Is.Not.Null, name + " destination zone");
            Vector3? target = nav.GetClosestPoint(destinationZone, destination, 96, 96, 512, nav.DefaultFilters);

            bool repaired = AutonomousRouteHotspotRepair.TryResolveFloor(nav, sourceZone,
                regionId, source, out Vector3 repairedFloor);
            Assert.That(repaired, Is.EqualTo(expectLocalRepair), name + " bounded repair classification");
            if (expectLocalRepair)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(Vector2.Distance(new(repairedFloor.X, repairedFloor.Y),
                        new(source.X, source.Y)), Is.LessThanOrEqualTo(2), name + " must preserve XY");
                    Assert.That(repairedFloor.Z, Is.LessThan(source.Z), name + " must correct downward only");
                    Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, sourceZone, repairedFloor),
                        Is.True, name + " repaired floor has a local exit");
                    Assert.That(target.HasValue && sourceZone == destinationZone &&
                        AutonomousZoneItinerary.HasCompleteCorridor(nav, sourceZone, repairedFloor, target.Value),
                        Is.True, name + " repaired floor reaches the logged destination");
                });
            }
        }

        private static void VerifyItinerary(string name, Region region, IPathfindingMgr nav,
            Vector3 start, Vector3 goal)
        {
            Zone currentZone = region.GetZone((int)start.X, (int)start.Y);
            Zone goalZone = region.GetZone((int)goal.X, (int)goal.Y);
            Assert.That(currentZone, Is.Not.Null, name + " start zone");
            Assert.That(goalZone, Is.Not.Null, name + " goal zone");
            Vector3 cursor = nav.GetClosestPoint(currentZone, start, 96, 96, 512, nav.DefaultFilters) ?? start;
            Vector3 destination = nav.GetClosestPoint(goalZone, goal, 96, 96, 512, nav.DefaultFilters) ?? goal;
            int hops = 0;
            while (currentZone != goalZone && hops++ < 16)
            {
                Assert.That(AutonomousZoneItinerary.TryNextStep(region, currentZone, goalZone,
                    cursor, destination, nav, out AutonomousZoneBoundaryRouting.Step step), Is.True,
                    name + " connected seam");
                cursor = step.Outside;
                currentZone = region.GetZone((int)cursor.X, (int)cursor.Y);
            }
            Assert.That(currentZone, Is.EqualTo(goalZone), name + " reaches goal zone");
            Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, goalZone, cursor, destination),
                Is.True, name + " final corridor");
        }
    }
}
