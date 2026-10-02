using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable, Explicit("Read-only installed Hibernian dungeon exterior route proof")]
public class UT_RecentHibDungeonSeams
{
    [Test]
    public void SiArrivalMustRetainAConnectedRouteToKoalinth()
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        string previous = Environment.CurrentDirectory;
        var loaded = new List<Zone>();
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                (name, _, _) => name == "lib/Detour"
                    ? NativeLibrary.Load(Path.Combine(root, "lib", "Detour.dll")) : IntPtr.Zero);
            Environment.CurrentDirectory = root;
            using var db = new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
            db.Open();
            Region region = UT_AuditedDungeonInstalledMesh.BuildRegion(db, 200, loaded);
            IPathfindingMgr nav = PathfindingProvider.LocalPathfindingMgr;
            Vector3 start = new(310228, 645039, 4848); // authentic Domnann -> Silvermine arrival
            Vector3 goal = new(247379, 472830, 5598); // authentic Koalinth portal
            AuditRoute(region, nav, start, goal);
            // Treibh Caillte is a different northbound branch. Do not route
            // it through Koalinth's lower-road detour.
            AuditRoute(region, nav, new(310228, 645039, 4848), new(296010, 705974, 6483));
        }
        finally
        {
            foreach (Zone zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }

    private static void AuditRoute(Region region, IPathfindingMgr nav, Vector3 start, Vector3 goal)
    {
        Zone current = region.GetZone((int)start.X, (int)start.Y);
        Zone destination = region.GetZone((int)goal.X, (int)goal.Y);
        Assert.That(current, Is.Not.Null);
        Assert.That(destination, Is.Not.Null);
        for (int hop = 0; hop < 16 && current != destination; hop++)
        {
            bool routed = AutonomousZoneItinerary.TryNextStep(region, current, destination,
                start, goal, nav, out AutonomousZoneBoundaryRouting.Step step);
            TestContext.WriteLine($"HIB_DUNGEON_SEAM hop={hop} zone={current.ID} start={start} routed={routed} next={step}");
            Assert.That(routed, Is.True, $"Unable to leave {current.Description} for goal {goal}");
            if (destination.ID == 203 && current.ID == 200)
                Assert.That(step.Outside.Z, Is.LessThan(9_000),
                    "The Lough Derg/Connacht border must not use the stranded 11,299-unit shelf");
            start = step.Outside;
            current = region.GetZone((int)start.X, (int)start.Y);
        }
        Assert.That(current, Is.EqualTo(destination));
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, destination, start, goal), Is.True);
    }
}
