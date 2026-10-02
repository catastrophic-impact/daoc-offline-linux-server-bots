using System;
using System.Collections.Generic;
using System.IO;
using System.Data.SQLite;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    // Opt-in, read-only local meshes. Never starts the server or touches its database.
    [TestFixture, NonParallelizable, Explicit("Requires OFFLINE_DAOC_NAV_ROOT; reads local meshes only")]
    public class UT_LongRunNavigationProbe
    {
        [Test]
        public void InspectNamedRoutes()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string previous = Environment.CurrentDirectory;
            var zones = new List<Zone>();
            try
            {
                Environment.CurrentDirectory = root;
                Zone Add(ushort id, int x, int y)
                {
                    Zone zone = new(null, id, "Offline probe", x, y, 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    zones.Add(zone);
                    return zone;
                }
                var nav = PathfindingProvider.LocalPathfindingMgr;
                Zone vale = Add(100, 753664, 671744);
                Zone east = Add(101, 720896, 737280);
                Zone gotar = Add(103, 720896, 802816);
                Zone jordheim = Add(120, 8192, 8192);
                void Probe(string name, Zone zone, Vector3 start, Vector3 end)
                {
                    WrappedPathfindingNode[] nodes = new WrappedPathfindingNode[512];
                    PathfindingResult result = nav.GetPathStraight(zone, start, end, nav.DefaultFilters, nodes);
                    TestContext.WriteLine($"{name}: {result.Status} nodes={result.NodeCount} last={(result.NodeCount > 0 ? nodes[result.NodeCount - 1].Position : default)} startSnap={nav.GetClosestPoint(zone, start, 64, 64, 4096, nav.DefaultFilters)} endSnap={nav.GetClosestPoint(zone, end, 64, 64, 4096, nav.DefaultFilters)}");
                }
                Vector3 start = new(802031, 722930, 4682);
                Probe("Gularg master", vale, start, new(803738, 721911, 4688));
                Probe("Gularg horse origin", vale, start, new(803840, 721833, 4687));
                Probe("Gularg master terrain Z", vale, start, new(803738, 721911, 4899));
                Probe("Gularg horse terrain Z", vale, start, new(803840, 721833, 4909));
                Assert.That(AutonomousStableRoutePlanner.TryResolveBoardingPoint(new(803840, 721833, 4687),
                    p => nav.GetClosestPoint(vale, p, 48, 48, 256, nav.DefaultFilters), out var boarding), Is.True);
                Assert.That(AutonomousStableRoutePlanner.TryResolveBoardingPoint(new(803738, 721911, 4688),
                    p => nav.GetClosestPoint(vale, p, 48, 48, 256, nav.DefaultFilters), out var interaction), Is.True);
                Assert.That(Vector3.Distance(boarding, interaction), Is.LessThanOrEqualTo(WorldMgr.INTERACT_DISTANCE));
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, vale, start, boarding), Is.True);
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, vale, boarding, interaction), Is.True);
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, vale, start, new(803840, 721833, 4687)), Is.False);
                Probe("Jordheim entrance approach", vale, start, new(805489, 731160, 5028));
                Probe("Jordheim recovery to exit", jordheim, new(32020, 28294, 8803), new(32869, 36103, 8002));
                Probe("Arngevald Fort Atla", gotar, new(752061, 816710, 5053), new(749617, 816121, 4409));
                foreach (int x in new[] { 786300, 785000, 780000, 775000, 770000, 760000 })
                {
                    Vector3? a = nav.GetClosestPoint(vale, new(x, 737216, 4682), 64, 64, 4096, nav.DefaultFilters);
                    Vector3? b = nav.GetClosestPoint(east, new(x, 737344, 4682), 64, 64, 4096, nav.DefaultFilters);
                    TestContext.WriteLine($"Vale/East seam x={x}: {a} -> {b}");
                }
                Assert.That(zones.TrueForAll(nav.HasNavmesh), Is.True);
                string databasePath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH") ??
                    Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"));
                using var db = new SQLiteConnection($"Data Source={databasePath};Read Only=True;Pooling=False;");
                db.Open();
                foreach (int regionId in new[] { 1, 10, 100, 101, 200, 201 })
                {
                    using var expansionCommand = db.CreateCommand();
                    expansionCommand.CommandText = "select Expansion from Regions where RegionID=" + regionId;
                    int rawExpansion = Convert.ToInt32(expansionCommand.ExecuteScalar());
                    Assert.That(rawExpansion, Is.Zero, $"Capital/homeland region {regionId} must use the live Classic DB value");
                    Assert.That(AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(rawExpansion + 1), Is.True,
                        $"Autonomous routing rejected Classic region {regionId}");
                }
                foreach ((int capital, int homeland) in new[] { (10, 1), (101, 100), (201, 200) })
                {
                    using var edgeCommand = db.CreateCommand();
                    edgeCommand.CommandText = "select count(*) from ZonePoint where SourceRegion=@capital and TargetRegion=@homeland";
                    edgeCommand.Parameters.AddWithValue("@capital", capital);
                    edgeCommand.Parameters.AddWithValue("@homeland", homeland);
                    Assert.That(Convert.ToInt32(edgeCommand.ExecuteScalar()), Is.GreaterThan(0),
                        $"Missing authoritative exit from capital {capital} to homeland {homeland}");
                }
                var regions = new Dictionary<int, Region>();
                var gates = new List<DOL.Database.DbZonePoint>();
                using (var gatesCommand = db.CreateCommand())
                {
                    gatesCommand.CommandText = "select Id,SourceRegion,SourceX,SourceY,SourceZ,TargetRegion,TargetX,TargetY,TargetZ from ZonePoint where SourceRegion in (200,201)";
                    using var reader = gatesCommand.ExecuteReader();
                    while (reader.Read()) gates.Add(new()
                    {
                        Id = (ushort)reader.GetInt32(0), SourceRegion = (ushort)reader.GetInt32(1),
                        SourceX = reader.GetInt32(2), SourceY = reader.GetInt32(3), SourceZ = reader.GetInt32(4),
                        TargetRegion = (ushort)reader.GetInt32(5), TargetX = reader.GetInt32(6),
                        TargetY = reader.GetInt32(7), TargetZ = reader.GetInt32(8)
                    });
                }
                foreach (int regionId in new[] { 201, 100, 1, 200, 181, 10, 101 })
                {
                    Region region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                    var list = new List<Zone>();
                    typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(region, new RegionData { Id = (ushort)regionId });
                    typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, list);
                    regions[regionId] = region;
                    using var command = db.CreateCommand();
                    command.CommandText = "select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=" + regionId;
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            ushort id = (ushort)reader.GetInt32(0);
                            Zone zone = new(region, id, reader.GetString(1), reader.GetInt32(2) * 8192, reader.GetInt32(3) * 8192,
                                reader.GetInt32(4) * 8192, reader.GetInt32(5) * 8192, id, false, 0, false, 0, 0, 0, 0, 0);
                            list.Add(zone);
                            if (!zones.Any(loaded => loaded.ID == id))
                            {
                                LocalPathfindingMgr.LoadNavMesh(zone);
                                zones.Add(zone);
                            }
                            else zone.IsPathfindingEnabled = true;
                        }
                    }
                    (Vector3 cursor, Vector3 goal) = regionId switch
                    {
                        100 => (start, new Vector3(749617, 816121, 4409)),
                        1 => (new Vector3(571290, 528794, 2218), new Vector3(524639, 488541, 2823)),
                        200 => (new Vector3(313848, 474886, 5344), new Vector3(348198, 492819, 5240)),
                        181 => (new Vector3(423803, 439910, 5967), new Vector3(370000, 400000, 5000)),
                        10 => (new Vector3(35990, 30298, 8005), new Vector3(32629, 32493, 7999)),
                        101 => (new Vector3(32020, 28294, 8803), new Vector3(32893, 36286, 8002)),
                        _ => (new Vector3(33197, 31200, 8002), new Vector3(31802, 38049, 7693))
                    };
                    // The SI end point is a navigation-only sample, not an
                    // invented mob spawn; project to the actual outdoor terrain.
                    if (regionId == 181)
                        goal = nav.GetClosestPoint(region.GetZone((int)goal.X, (int)goal.Y), goal, 64, 64, 4096, nav.DefaultFilters) ?? goal;
                    Zone goalZone = region.GetZone((int)goal.X, (int)goal.Y);
                    Zone currentZone = region.GetZone((int)cursor.X, (int)cursor.Y);
                    var route = AutonomousZoneItinerary.FindZoneRoute(list, currentZone, goalZone);
                    TestContext.WriteLine($"Region {regionId} topology: {string.Join(" -> ", route.Select(zone => zone.ID))}");
                    int hops = 0;
                    while (currentZone != goalZone && hops++ < 16)
                    {
                        bool found = AutonomousZoneItinerary.TryNextStep(region, currentZone, goalZone, cursor, goal, nav, out var step);
                        TestContext.WriteLine($"Region {regionId} seam from {currentZone.ID}: success={found} {step.Inside} -> {step.Outside}");
                        if (!found && regionId == 200)
                        {
                            var transit = AutonomousCapitalTransit.Choose(region, regions[201], cursor, goal, 200, 201, gates, nav);
                            // Keep the REAL onward destination. A test ending
                            // at the broken island's last node hid the gate pileup.
                            Probe("city exit19 to SI portal", goalZone, new(332733, 485215, 5215), goal);
                            Assert.That(transit, Is.Not.Null, "Reachable cross-capital travel uses both real gates");
                            Assert.That(transit.Entry.Id, Is.EqualTo(14));
                            Assert.That(transit.Exit.Id, Is.EqualTo(19));
                            cursor = new(transit.Exit.TargetX, transit.Exit.TargetY, transit.Exit.TargetZ);
                            currentZone = region.GetZone((int)cursor.X, (int)cursor.Y);
                            TestContext.WriteLine($"Verified city transit: gate {transit.Entry.Id} -> walk through Tir na Nog -> gate {transit.Exit.Id}");
                            continue;
                        }
                        Assert.That(found, Is.True, $"Named region {regionId} route must reach a connected seam");
                        cursor = step.Outside;
                        currentZone = region.GetZone((int)cursor.X, (int)cursor.Y);
                    }
                    Assert.That(currentZone, Is.EqualTo(goalZone));
                    Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, goalZone, cursor, goal), Is.True, "Last walking leg reaches actual destination");
                    TestContext.WriteLine($"Region {regionId}: complete verified walking itinerary to {goal}");
                }
                void VerifyCapitalApproach(int regionId, Vector3 startPoint, Vector3 portal, string label)
                {
                    Region city = regions[regionId];
                    Zone cityZone = city.GetZone((int)portal.X, (int)portal.Y);
                    Assert.That(city.GetZone((int)startPoint.X, (int)startPoint.Y), Is.EqualTo(cityZone),
                        label + " starts in the portal zone");
                    Assert.That(AutonomousZonePointApproach.TryResolve(nav, cityZone, startPoint,
                        portal, 190, out Vector3 approach), Is.True, label + " has a connected activation approach");
                    Assert.That(Vector2.Distance(new(approach.X, approach.Y), new(portal.X, portal.Y)),
                        Is.LessThanOrEqualTo(190), label + " approach remains inside the real portal radius");
                }
                VerifyCapitalApproach(10, new(35990, 30298, 8005), new(32629, 32493, 7999), "Camelot north exit 5");
                VerifyCapitalApproach(10, new(35990, 30298, 8005), new(41123, 26442, 8253), "Camelot south exit 9");
                VerifyCapitalApproach(101, AutonomousRendezvousNavigation.JordheimMeetingPoint,
                    new(36279, 33707, 8001), "Jordheim exit 20");
                VerifyCapitalApproach(101, AutonomousRendezvousNavigation.JordheimMeetingPoint,
                    new(36195, 31891, 8005), "Jordheim exit 21");
                VerifyCapitalApproach(101, AutonomousRendezvousNavigation.JordheimMeetingPoint,
                    new(32893, 36286, 8002), "Jordheim exit 22");
                VerifyCapitalApproach(101, AutonomousRendezvousNavigation.JordheimMeetingPoint,
                    new(31100, 36272, 8034), "Jordheim exit 23");
                VerifyCapitalApproach(201, new(33197, 31200, 8002), new(17703, 35966, 6261), "Tir na Nog Lough Derg exit 19");
                VerifyCapitalApproach(201, new(33197, 31200, 8002), new(31802, 38049, 7693), "Tir na Nog Connacht exit 26");

                void DiagnoseLiveCapitalStart(int regionId, Vector3 startPoint, Vector3[] portals, string label)
                {
                    Region city = regions[regionId];
                    Zone startZone = city.GetZone((int)startPoint.X, (int)startPoint.Y);
                    Vector3? broadFloor = startZone == null ? null :
                        nav.GetClosestPoint(startZone, startPoint, 64, 64, 512, nav.DefaultFilters);
                    Vector3 strict = default;
                    bool hasStrictFloor = startZone != null &&
                        AutonomousNavigationSurface.TryFloor(nav, startZone, startPoint, out strict);
                    TestContext.WriteLine($"{label}: zone={startZone?.ID} strictFloor=" +
                        $"{(hasStrictFloor ? strict : null)} broadFloor={broadFloor}");
                    Assert.That(startZone, Is.Not.Null, label + " remains inside a loaded capital zone");
                    Assert.That(hasStrictFloor, Is.True, label + " accepts the real floor beneath its persisted integral Z");
                    foreach (Vector3 portal in portals)
                    {
                        Zone portalZone = city.GetZone((int)portal.X, (int)portal.Y);
                        Vector3 approach = default;
                        bool resolved = startZone != null && startZone == portalZone &&
                            AutonomousZonePointApproach.TryResolve(nav, startZone, startPoint, portal, 190, out approach);
                        TestContext.WriteLine($"{label} -> {portal}: resolved={resolved} approach={(resolved ? approach : default)}");
                        Assert.That(resolved, Is.True, label + " reaches every eligible real capital exit");
                    }
                }
                DiagnoseLiveCapitalStart(101, new(32404, 35950, 8006),
                    [new(36279, 33707, 8001), new(36195, 31891, 8005), new(32893, 36286, 8002), new(31100, 36272, 8034)],
                    "Ivararvald live Jordheim start");
                DiagnoseLiveCapitalStart(101, new(34996, 34090, 8009),
                    [new(36279, 33707, 8001), new(36195, 31891, 8005), new(32893, 36286, 8002), new(31100, 36272, 8034)],
                    "Astra live Jordheim start");
                DiagnoseLiveCapitalStart(201, new(24411, 30256, 7148),
                    [new(17703, 35966, 6261), new(31802, 38049, 7693)],
                    "Maeinlin live Tir na Nog start");
                DiagnoseLiveCapitalStart(10, new(34354, 31368, 7898),
                    [new(32629, 32493, 7999), new(41123, 26442, 8253)],
                    "Camelot live start one");
                DiagnoseLiveCapitalStart(10, new(34330, 31370, 7903),
                    [new(32629, 32493, 7999), new(41123, 26442, 8253)],
                    "Camelot live start two");
                DiagnoseLiveCapitalStart(10, new(35993, 30396, 7999),
                    [new(32629, 32493, 7999), new(41123, 26442, 8253)],
                    "Camelot live start three");

                Region hibernia = regions[200];
                Vector3 loughExit = new(332733, 485215, 5215);
                Vector3 magMell = new(346463, 490663, 5330);
                Zone lough = hibernia.GetZone((int)loughExit.X, (int)loughExit.Y);
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, lough, loughExit, magMell), Is.True,
                    "Leaving Tir na Nog must reach Mag Mell, not just the small gate island");
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, lough, magMell, new(332389, 485179, 5221)), Is.True,
                    "The return route must reach the actual Lough Derg entry gate");
                Vector3 connachtExit = new(311589, 473954, 5219);
                Vector3 connachtRoad = new(313848, 474886, 5344);
                Zone connacht = hibernia.GetZone((int)connachtExit.X, (int)connachtExit.Y);
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, connacht, connachtExit, connachtRoad), Is.True,
                    "Connacht exit must connect to its outside road");
                var returnTransit = AutonomousCapitalTransit.Choose(hibernia, regions[201], magMell, connachtRoad, 200, 201, gates, nav);
                Assert.That(returnTransit, Is.Not.Null, "Cross-capital travel works in both directions");
                Assert.That(returnTransit.Entry.Id, Is.EqualTo(13));
                Assert.That(returnTransit.Exit.Id, Is.EqualTo(26));
                // The three audited DF stalls oscillated at this exact
                // Lough Derg/Valley seam. A real ticket can instead land on
                // the Connacht component with a connected portal approach.
                Vector3 dfPortal = new(325269, 433985, 6297);
                Vector3 magMellTicket = new(345398, 492987, 5183);
                Zone dfPortalZone = hibernia.GetZone((int)dfPortal.X, (int)dfPortal.Y);
                Assert.That(AutonomousCapitalTransit.Choose(hibernia, regions[201],
                    magMellTicket, dfPortal, 200, 201, gates, nav), Is.Null,
                    "The real Connacht capital gate does not lead to the DF portal component");
                Assert.That(AutonomousZoneItinerary.TryNextStep(hibernia, lough, dfPortalZone,
                    magMellTicket, dfPortal, nav, out var outbound), Is.True);
                Assert.That(Vector3.Distance(outbound.Outside, new(385088, 483424, 7262)),
                    Is.LessThan(3), "The installed route selects the audited Valley seam");
                Zone valley = hibernia.GetZone(385088, 483424);
                Assert.That(AutonomousZoneItinerary.TryNextStep(hibernia, valley, dfPortalZone,
                    outbound.Outside, dfPortal, nav, out var reverse), Is.True);
                Assert.That(hibernia.GetZone((int)reverse.Outside.X, (int)reverse.Outside.Y),
                    Is.EqualTo(lough), "The next route step reverses across that seam");
                foreach (Vector3 landing in new[]
                {
                    new Vector3(335660, 422602, 5219), // Tir na mBeo ticket
                    new Vector3(335608, 422672, 5224), // Druim Cain ticket
                    new Vector3(335704, 422684, 5222), // Connla ticket
                })
                {
                    Assert.That(AutonomousZonePointApproach.TryResolve(nav, dfPortalZone,
                        landing, dfPortal, 174, out Vector3 approach), Is.True,
                        $"Ticket landing {landing} must reach portal row 87");
                    Assert.That(Vector2.Distance(new(approach.X, approach.Y), new(dfPortal.X, dfPortal.Y)),
                        Is.LessThanOrEqualTo(174));
                }
                foreach ((Vector3 startPoint, Vector3 horseOrigin) in new[]
                {
                    (new Vector3(326427, 610167, 5515), new Vector3(343430, 526748, 5448)),
                    (magMellTicket, new Vector3(343430, 526748, 5448)),
                    (new Vector3(332555, 485333, 5215), new Vector3(343430, 526748, 5448)),
                    (new Vector3(403458, 441679, 3111), new Vector3(421892, 488015, 1824)),
                    (new Vector3(385088, 483424, 7262), new Vector3(421892, 488015, 1824)),
                })
                {
                    Zone from = hibernia.GetZone((int)startPoint.X, (int)startPoint.Y);
                    Zone to = hibernia.GetZone((int)horseOrigin.X, (int)horseOrigin.Y);
                    Vector3? snapped = nav.GetClosestPoint(to, horseOrigin, 48, 48, 256, nav.DefaultFilters);
                    Vector3 cursor = startPoint;
                    Zone cursorZone = from;
                    int hops = 0;
                    while (cursorZone != to && hops++ < 16 &&
                           AutonomousZoneItinerary.TryNextStep(hibernia, cursorZone, to,
                               cursor, horseOrigin, nav, out var next))
                    {
                        cursor = next.Outside;
                        cursorZone = hibernia.GetZone((int)cursor.X, (int)cursor.Y);
                    }
                    Assert.That(cursorZone, Is.EqualTo(to), $"First boarding walk from {startPoint} reaches the horse's zone");
                    Assert.That(snapped, Is.Not.Null, $"Horse origin {horseOrigin} has walkable terrain");
                    Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, to, cursor, snapped.Value),
                        Is.True, $"First boarding walk from {startPoint} reaches horse {horseOrigin}");
                }
                Zone blackMountains = regions[1].GetZone(515616, 494274);
                Assert.That(AutonomousRendezvousNavigation.TryChoosePoint(nav, blackMountains,
                    new(515616, 494274, 3397), out _), Is.True,
                    "The repaired prop-top now has a real short step onto connected ground");
                Vector3 bombard = new(515649, 496745, 3352);
                Assert.That(AutonomousRendezvousNavigation.TryChoosePoint(nav, blackMountains, bombard, out Vector3 assembly), Is.True,
                    "A real connected nearby town/stable anchor remains usable");
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, blackMountains, assembly, bombard), Is.True);
                // Optional, offline one-time repair PLAN only. The fixture never
                // writes the live DB. Only already-stranded non-waiting actors
                // in the evidenced prop footprint qualify, after native proof.
                string rescuePlan = Environment.GetEnvironmentVariable("OFFLINE_DAOC_RESCUE_PLAN");
                if (!string.IsNullOrWhiteSpace(rescuePlan))
                {
                    var repairs = new List<object>();
                    using var command = db.CreateCommand();
                    command.CommandText = "select BotId,Name,X,Y,Z,Activity from offline_world_bots where RegionId=1 and IsAlive=1 " +
                        "and X between 515480 and 515760 and Y between 494240 and 494460 " +
                        "and Activity not in ('Formed up at rendezvous','Holding group formation')";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        Vector3 old = new(reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
                        if (AutonomousRendezvousNavigation.HasLocalExit(nav, blackMountains, old)) continue;
                        Assert.That(AutonomousRendezvousNavigation.TryChoosePoint(nav, blackMountains, bombard, out Vector3 safe), Is.True);
                        Vector3 exact = new((int)safe.X, (int)safe.Y, (int)safe.Z);
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, blackMountains, exact, bombard), Is.True);
                        repairs.Add(new { Id = reader.GetInt64(0), Name = reader.GetString(1), OldX = (int)old.X,
                            OldY = (int)old.Y, OldZ = (int)old.Z, Activity = reader.GetString(5),
                            X = (int)exact.X, Y = (int)exact.Y, Z = (int)exact.Z });
                    }
                    File.WriteAllText(rescuePlan, JsonSerializer.Serialize(repairs, new JsonSerializerOptions { WriteIndented = true }));
                    TestContext.WriteLine($"Read-only native-verified rescue plan: {repairs.Count} stranded bots; legitimate formation waits excluded");
                }
            }
            finally
            {
                foreach (Zone zone in zones) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }
    }
}
