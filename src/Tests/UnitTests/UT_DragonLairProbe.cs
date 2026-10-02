using System;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable, Explicit("Reads installed navigation meshes only")]
    public class UT_DragonLairProbe
    {
        [Test]
        public void Inspect()
        {
            string previous = Environment.CurrentDirectory;
            var zones = new System.Collections.Generic.Dictionary<ushort, Zone>();
            try
            {
                Environment.CurrentDirectory = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
                foreach (var entry in new[] {
                    (Id: (ushort)4, X:43*8192,Y:85*8192,P:new Vector3(391326,755351,388)),
                    (Id: (ushort)116,X:84*8192,Y:118*8192,P:new Vector3(708811,1021459,3007)),
                    (Id: (ushort)216,X:43*8192,Y:79*8192,P:new Vector3(391272,707008,6212)),
                    (Id: (ushort)216,X:43*8192,Y:79*8192,P:new Vector3(408646,706432,2965)),
                    (Id: (ushort)60,X:8192,Y:8192,P:new Vector3(26567,40143,15372)),
                    (Id: (ushort)61,X:8192,Y:8192,P:new Vector3(21615,54280,14076)),
                    (Id: (ushort)248,X:8192,Y:8192,P:new Vector3(32050,40863,15468)),
                    (Id: (ushort)160,X:8192,Y:8192,P:new Vector3(34542,57121,11881)),
                    (Id: (ushort)160,X:8192,Y:8192,P:new Vector3(34136,57202,12025)),
                    (Id: (ushort)249,X:8192,Y:8192,P:new Vector3(45027,51899,15468)),
                    (Id: (ushort)191,X:8192,Y:8192,P:new Vector3(39237,62644,11685)) })
                {
                    if (!System.IO.File.Exists(System.IO.Path.Combine("navmesh", $"zone{entry.Id:D3}.nav")))
                        continue;
                    if (!zones.TryGetValue(entry.Id, out var zone))
                    {
                        zone = new Zone(null,entry.Id,"Dragon probe",entry.X,entry.Y,65536,65536,entry.Id,false,0,false,0,0,0,0,0);
                        LocalPathfindingMgr.LoadNavMesh(zone);
                        zones.Add(entry.Id, zone);
                    }
                    {
                        var nav = PathfindingProvider.LocalPathfindingMgr;
                        TestContext.WriteLine($"zone={entry.Id} saved={entry.P} surface={nav.GetClosestPoint(zone,entry.P,64,64,4096,nav.DefaultFilters)}");
                        for(int i=0;i<4;i++)
                        {
                            var p=entry.P+new Vector3((float)Math.Cos(i*Math.PI/2)*400,(float)Math.Sin(i*Math.PI/2)*400,0);
                            TestContext.WriteLine($"offset={p} surface={nav.GetClosestPoint(zone,p,64,64,4096,nav.DefaultFilters)}");
                        }
                    }
                }
                foreach (var pair in new[] { (eRealm.Albion, (ushort)4), (eRealm.Midgard, (ushort)116), (eRealm.Hibernia, (ushort)216) })
                {
                    bool baseline = Environment.GetEnvironmentVariable("DRAGON_COMPARE_BASELINE") == "1";
                    var homePoint = DragonLairPlacement.Home(pair.Item1);
                    var home = new Vector3(homePoint.X, homePoint.Y, homePoint.Z);
                    var zone = zones[pair.Item2];
                    var nav = PathfindingProvider.LocalPathfindingMgr;
                    var ground = nav.GetClosestPoint(zone, home, 64, 64, baseline ? 512 : 64, nav.DefaultFilters);
                    TestContext.WriteLine($"REPAIRED {pair.Item1} home={home} nav={ground}");
                    Assert.That(ground.HasValue, Is.True, $"{pair.Item1} home must be grounded");
                    if (!baseline)
                        Assert.That(Math.Abs(ground.Value.Z-home.Z), Is.LessThan(40), "Navigation raster must follow the visible mound, not terrain below it");
                    else
                        home = ground.Value; // Compare old route connectivity at its own (incorrect) floor.
                    int approaches = 0;
                    for (int i=0;i<4;i++)
                    {
                        var candidate = home + new Vector3((float)Math.Cos(i*Math.PI/2)*400,(float)Math.Sin(i*Math.PI/2)*400,0);
                        var start = nav.GetClosestPoint(zone,candidate,64,64,512,nav.DefaultFilters);
                        if (start.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,start.Value,home) &&
                            nav.HasLineOfSight(zone,home,start.Value,nav.DefaultFilters)) approaches++;
                    }
                    TestContext.WriteLine($"{pair.Item1}: grounded melee home, {approaches}/4 connected visible approaches inside 800-unit aggro radius");
                    Assert.That(approaches, Is.GreaterThan(0));
                    int exteriorApproaches = 0;
                    for (int i=0;i<8;i++)
                    {
                        var candidate = home + new Vector3((float)Math.Cos(i*Math.PI/4)*3200,(float)Math.Sin(i*Math.PI/4)*3200,0);
                        // Identical outside sampling height for baseline and repaired meshes.
                        candidate.Z = pair.Item1 == eRealm.Albion ? 228 : pair.Item1 == eRealm.Midgard ? 2846 : 2829;
                        var start = nav.GetClosestPoint(zone,candidate,128,128,2048,nav.DefaultFilters);
                        bool connected = start.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,start.Value,ground.Value);
                        if (connected) exteriorApproaches++;
                        TestContext.WriteLine($"SEAM {pair.Item1} direction={i} from={start} connected={connected}");
                    }
                    Assert.That(exteriorApproaches, Is.GreaterThanOrEqualTo(2), "Lair must connect back into the unchanged outside tiles");
                    Vector3[] destinations = pair.Item1 switch
                    {
                        eRealm.Albion => new[] { new Vector3(398605,754458,1404), new Vector3(392450,743176,1404), new Vector3(383669,758112,1847), new Vector3(401432,755310,1728) },
                        eRealm.Midgard => new[] { new Vector3(708632,1021688,3721), new Vector3(713073,1015679,3833), new Vector3(713388,1024499,3833), new Vector3(705812,1024952,3833), new Vector3(706019,1018867,3833) },
                        _ => new[] { new Vector3(408807,706640,4315), new Vector3(404579,699656,4683), new Vector3(410650,698271,4758), new Vector3(402790,707787,4083), new Vector3(407532,695634,4533) }
                    };
                    int safeTeleports = 0;
                    foreach (var destination in destinations)
                    {
                        var floor = nav.GetClosestPoint(zone,destination,64,64,4096,nav.DefaultFilters);
                        bool safe = floor.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,ground.Value,floor.Value);
                        if (safe) safeTeleports++;
                        TestContext.WriteLine($"TELEPORT {pair.Item1} oldAir={destination} floor={floor} connected={safe}");
                    }
                    Assert.That(safeTeleports, Is.GreaterThan(0), $"{pair.Item1} must retain usable ground teleports");
                }
            }
            finally
            {
                foreach (var zone in zones.Values) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory=previous;
            }
        }
    }
}
