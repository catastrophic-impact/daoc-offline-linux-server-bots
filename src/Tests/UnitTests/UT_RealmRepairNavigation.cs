using System;
using System.Collections.Generic;
using System.IO;
using System.Data.SQLite;
using System.Numerics;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;
[NonParallelizable, Explicit("Installed navigation read-only repair probes")]
public class UT_RealmRepairNavigation
{
    [Test]
    public void CapturedGlacierApproaches()
    {
        string previous=Environment.CurrentDirectory;
        string root=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        var regions=new Dictionary<ushort,Region>();
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,(name,asm,search)=>name=="lib/Detour"?NativeLibrary.Load(Path.Combine(root,"lib","Detour.dll")):IntPtr.Zero);
            Environment.CurrentDirectory=root;
            using var db=new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");db.Open();
            foreach(ushort rid in new ushort[]{151,160,200})
            {
                var region=new Region(new RegionData{Id=rid,Name="Probe",Description="Probe",Mobs=[]});regions[rid]=region;
                using var cmd=db.CreateCommand();cmd.CommandText="SELECT ZoneID,Name,OffsetX,OffsetY,Width,Height FROM Zones WHERE RegionID="+rid;
                using var rows=cmd.ExecuteReader();
                while(rows.Read())
                {
                    ushort id=Convert.ToUInt16(rows[0]);
                    var z=new Zone(region,id,rows.GetString(1),Convert.ToInt32(rows[2])*8192,Convert.ToInt32(rows[3])*8192,Convert.ToInt32(rows[4])*8192,Convert.ToInt32(rows[5])*8192,id,false,0,false,0,0,0,0,0){IsPathfindingEnabled=true};
                    region.Zones.Add(z);LocalPathfindingMgr.LoadNavMesh(z);
                }
            }
            var nav=PathfindingProvider.LocalPathfindingMgr;
            foreach(var (rid,p) in new (ushort,Vector3)[]{
                (151,new(382872,333782,6389)),(151,new(377328,286268,7593)),
                (151,new(348195,302918,5526)),(151,new(376591,288264,7446)),
                (151,new(376439,288389,7439)),(151,new(375726,288975,7405)),
                (151,new(370425,282504,8025)),(151,new(385318,329452,5962)),
                (151,new(380478,280008,8784)),(151,new(331156,298157,8789)),
                (151,new(331682,297516,8370)),(160,new(30522,22478,17467))})
            {
                var region=regions[rid];var zone=region.GetZone((int)p.X,(int)p.Y);
                var small=nav.GetClosestPoint(zone,p,64,64,128,nav.DefaultFilters);
                var wide=nav.GetClosestPoint(zone,p,768,768,768,nav.DefaultFilters);
                var tall=nav.GetClosestPoint(zone,p,64,64,4096,nav.DefaultFilters);
                TestContext.Progress.WriteLine($"CAPTURE region={rid} zone={zone?.Description} position={p} small={small} wide={wide} vertical={tall}");
            }
            var start=new Vector3(379260,385996,7752);var goal=new Vector3(398621,271246,8575);
            bool success=RealmRaidMuster.TryRoute(regions[151],nav,eRealm.Midgard,start,goal,out var seams);
            TestContext.Progress.WriteLine($"HAGALL ENTRY {success} [{string.Join(";",seams)}]");
            Assert.That(success, Is.True);
            var dragonHome = DragonLairPlacement.Home(eRealm.Hibernia);
            var dragonGoal = new Vector3(dragonHome.X,dragonHome.Y,dragonHome.Z);
            Assert.That(RealmRaidMuster.TryRoute(regions[200],nav,eRealm.Hibernia,
                new(352160,680000,7224),dragonGoal,out var hibSeams,new(361354,750434,4944)), Is.True);
            TestContext.Progress.WriteLine($"CUULDURACH CAPTURE CORRIDOR [{string.Join(";",hibSeams)}]");
            Vector3 surface = nav.GetClosestPoint(regions[151].GetZone(382872,333782),
                new(382872,333782,7709),64,64,128,nav.DefaultFilters).Value;
            Zone surfaceZone = regions[151].GetZone((int)surface.X,(int)surface.Y);
            foreach (var delta in new[]{new Vector3(150,0,0),new Vector3(0,150,0),new Vector3(-150,0,0)})
            {
                var grounded = nav.GetMoveAlongSurfaceGrounded(surfaceZone,surface,surface+delta,nav.DefaultFilters);
                Assert.That(grounded.HasValue, Is.True);
                var floor = nav.GetClosestPoint(surfaceZone,grounded.Value,2,2,4,nav.DefaultFilters);
                Assert.That(floor.HasValue, Is.True);
                Assert.That(Math.Abs(floor.Value.Z-grounded.Value.Z), Is.LessThan(2));
                TestContext.Progress.WriteLine($"GLACIER APPROACH GROUND start={surface} legacy={nav.GetMoveAlongSurface(surfaceZone,surface,surface+delta,nav.DefaultFilters)} grounded={grounded}");
            }
        }
        finally
        {
            foreach(var r in regions.Values)foreach(var z in r.Zones)LocalPathfindingMgr.UnloadNavMesh(z);
            Environment.CurrentDirectory=previous;
        }
    }
}
