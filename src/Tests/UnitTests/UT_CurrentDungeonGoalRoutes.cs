using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[Explicit("Read-only installed native routes for every current ordinary dungeon goal"), NonParallelizable]
public class UT_CurrentDungeonGoalRoutes
{
    [Test]
    public void CurrentGoalsHaveRoundTripEntranceRoutes()
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        string previous = Environment.CurrentDirectory;
        var loaded = new List<Zone>();
        var failures = new List<string>();
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                (name, asm, search) => name == "lib/Detour" ? NativeLibrary.Load(Path.Combine(root,"lib","Detour.dll")) : IntPtr.Zero);
            Environment.CurrentDirectory = root;
            using var db = new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
            db.Open();
            var nav = PathfindingProvider.LocalPathfindingMgr;
            foreach (ushort id in new ushort[] {20,21,22,23,24,61,125,126,127,128,129,150,161,180,190,220,221,222,223,224,246,248,276,277})
            {
                var region = UT_AuditedDungeonInstalledMesh.BuildRegion(db,id,loaded);
                int tested = 0, before = failures.Count;
                foreach (var proof in AutonomousDungeonGoalCatalog.VerifiedPointsForRegion(id))
                {
                    Zone zone = region.GetZone(proof.Spawn[0],proof.Spawn[1]);
                    Vector3 target = new(proof.Spawn[0],proof.Spawn[1],proof.Spawn[2]);
                    bool good = zone != null && proof.Entries.Any(p =>
                        AutonomousDungeonTargetRoute.CanReach(nav,zone,new Vector3(p[0],p[1],p[2]),target));
                    tested++;
                    if (!good) failures.Add($"region={id} {proof.Id} {proof.Name} spawn={target}");
                }
                TestContext.WriteLine($"CURRENT_GOAL_ROUTES region={id} tested={tested} failed={failures.Count-before}");
            }
            foreach (string failure in failures) TestContext.WriteLine("CURRENT_GOAL_FAILURE " + failure);
            Assert.That(failures, Is.Empty, "Existing goal routes must work from an authorized entrance and return, with a real melee approach");
        }
        finally { foreach(var z in loaded) LocalPathfindingMgr.UnloadNavMesh(z); Environment.CurrentDirectory=previous; }
    }
}
