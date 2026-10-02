using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [Explicit("Read-only installed keep-approach incident checks"), NonParallelizable]
    public class UT_SiegeIncidentNavigation
    {
        [Test]
        public void FiveCapturedFrontierPositionsReachKeepExterior()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(Path.Combine(root, "lib", "Detour.dll")) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                using var db = new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
                db.Open();
                Region region = UT_AuditedDungeonInstalledMesh.BuildRegion(db, 100, loaded);
                var nav = PathfindingProvider.LocalPathfindingMgr;
                Vector3 keep = new(678401, 654714, 5968);
                foreach (var (name, start) in new (string, Vector3)[] {
                    ("Godiswin", new(655934,634005,6510)), ("Aelisette",new(658858,630796,5366)),
                    ("Wilenfrey",new(658477,622666,5769)), ("Ranaisward last frontier",new(656407,633479,6265)),
                    ("Rosoanne",new(659131,621458,5370)) })
                {
                    bool raw = AutonomousRvrRally.HasRoute(region, nav, eRealm.Albion, start, keep);
                    bool resolved = AutonomousRvrApproach.TryResolveAcrossZones(nav, region, eRealm.Albion, start, keep, out var approach);
                    TestContext.WriteLine($"{name}: zone={region.GetZone((int)start.X,(int)start.Y)?.ID} raw={raw} exterior={resolved} endpoint={approach}");
                    Assert.That(resolved, Is.True, name);
                    Assert.That(AutonomousRvrRally.HasRoute(region, nav, eRealm.Albion, start, approach), Is.True, name);
                }
            }
            finally
            {
                foreach (var zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }
    }
}
