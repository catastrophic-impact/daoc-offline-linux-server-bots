using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[NonParallelizable, Explicit("Read-only reproduction of captured expedition routes")]
public class UT_ExpeditionHubFailures
{
    [Test]
    public void CapturedSourceApproaches()
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
            foreach (var s in new[] {
                (10, 9, new Vector3(32543,26691,7831)),
                (10, 9, new Vector3(32323,26409,8363)),
                (10, 9, new Vector3(32537,26927,7834)),
                (10, 5, new Vector3(32543,26691,7831)),
                (10, 5, new Vector3(32323,26409,8363)),
                (10, 5, new Vector3(32537,26927,7834)),
                (100,165,new Vector3(768012,737271,5558)),
                (100,165,new Vector3(768592,736737,5861)),
                (200,167,new Vector3(330492,483420,7493)),
                (200,167,new Vector3(330845,483364,7356)),
                (200,167,new Vector3(330807,483361,7381)),
                (200,167,new Vector3(330511,483418,7467)) })
            {
                var region = (Region)typeof(UT_AuditedDungeonInstalledMesh).GetMethod("BuildRegion", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] {db,s.Item1,loaded});
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"SELECT SourceX,SourceY,SourceZ FROM ZonePoint WHERE Id={s.Item2}";
                using var r = cmd.ExecuteReader();
                Assert.That(r.Read(), Is.True);
                Vector3 target = new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2));
                var zone=region.GetZone((int)s.Item3.X,(int)s.Item3.Y);
                var nav=PathfindingProvider.LocalPathfindingMgr;
                bool success=AutonomousZonePointApproach.TryResolve(nav,zone,s.Item3,target,112,out var approach);
                var edge = new DOL.Database.DbZonePoint { Id=(ushort)s.Item2, SourceRegion=(ushort)s.Item1, SourceX=(int)target.X, SourceY=(int)target.Y, SourceZ=(int)target.Z };
                bool repaired=AutonomousRouteHotspotRepair.TryResolveAuditedCrossingSource(nav,region,edge,s.Item3,out var corrected);
                TestContext.Progress.WriteLine($"REPAIR {s} repaired={repaired} corrected={corrected}");
                if(s.Item1 != 10)
                {
                    Assert.That(success || repaired,Is.True,$"Captured expedition source {s}");
                    if(repaired)
                    {
                        Assert.That(Vector3.Distance(corrected,s.Item3),Is.LessThan(96));
                        Assert.That(AutonomousZonePointApproach.TryResolve(nav,zone,corrected,target,112,out _),Is.True);
                    }
                }
                TestContext.Progress.WriteLine($"SOURCE {s} target={target} approach={success}:{approach} floor={nav.GetClosestPoint(zone,s.Item3,96,96,1024,nav.DefaultFilters)}");
                foreach (int radius in new[] {0,32,64,128,256,512})
                for(int a=0;a<(radius==0?1:8);a++)
                {
                    Vector3 raw=s.Item3+new Vector3(MathF.Cos(a*MathF.PI/4)*radius,MathF.Sin(a*MathF.PI/4)*radius,0);
                    var floor=nav.GetClosestPoint(zone,raw,16,16,1024,nav.DefaultFilters);
                    if(floor.HasValue && AutonomousZonePointApproach.TryResolve(nav,zone,floor.Value,target,112,out _))
                        TestContext.Progress.WriteLine($"CONNECTED radius={radius} point={floor} delta={floor.Value-s.Item3}");
                }
            }
        }
        finally { foreach(var z in loaded) LocalPathfindingMgr.UnloadNavMesh(z); Environment.CurrentDirectory=previous; }
    }
}
