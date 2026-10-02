using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable, Explicit("Read-only current installed routing proof")]
    public class UT_SeptemberNineInstalledRoutes
    {
        [Test]
        public void LoggedPortalHotspotsHaveWalkableInteractionApproaches()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string prior = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                if (!string.IsNullOrEmpty(native))
                    NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                        (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                using var db = new SQLiteConnection($"Data Source={Path.GetFullPath(Path.Combine(root,"..","data","opendaoc.sqlite3.db"))};Read Only=True;Pooling=False;");
                db.Open();
                var regions = new Dictionary<int, Region>();
                foreach (int id in new[] {1,10,100,101,200,201})
                {
                    var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                    typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(region, new RegionData {Id=(ushort)id});
                    var zones = new List<Zone>();
                    typeof(Region).GetField("m_zones",BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region,zones);
                    regions[id] = region;
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=" + id;
                    using var rows = cmd.ExecuteReader();
                    while (rows.Read())
                    {
                        ushort zoneId=(ushort)rows.GetInt32(0);
                        var zone=new Zone(region,zoneId,rows.GetString(1),rows.GetInt32(2)*8192,rows.GetInt32(3)*8192,
                            rows.GetInt32(4)*8192,rows.GetInt32(5)*8192,zoneId,false,0,false,0,0,0,0,0);
                        zones.Add(zone); loaded.Add(zone); LocalPathfindingMgr.LoadNavMesh(zone);
                    }
                }
                var nav=PathfindingProvider.LocalPathfindingMgr;
                foreach (var route in new[]
                {
                    (Region:10, Edge:9, Start:new Vector3(40935,26512,8256)),
                    (Region:10, Edge:5, Start:new Vector3(35953,30436,7991)),
                    (Region:100, Edge:165, Start:new Vector3(808662,724912,4883)),
                    (Region:200, Edge:167, Start:new Vector3(348372,492733,5270)),
                    (Region:200, Edge:167, Start:new Vector3(348206,493036,5246)),
                    (Region:201, Edge:19, Start:new Vector3(17883,35855,6265)),
                    (Region:1, Edge:24, Start:new Vector3(603265,522848,3123))
                })
                {
                    using var cmd=db.CreateCommand();
                    cmd.CommandText=$"select SourceX,SourceY,SourceZ from ZonePoint where Id={route.Edge} and SourceRegion={route.Region}";
                    using var row=cmd.ExecuteReader(); Assert.That(row.Read(),Is.True);
                    Vector3 portal=new(row.GetInt32(0),row.GetInt32(1),row.GetInt32(2));
                    Zone zone=regions[route.Region].GetZone((int)route.Start.X,(int)route.Start.Y);
                    bool ok=AutonomousZonePointApproach.TryResolve(nav,zone,route.Start,portal,174,out Vector3 approach);
                    TestContext.WriteLine($"CURRENT_PORTAL edge={route.Edge} start={route.Start} approach={approach} connected={ok}");
                    Assert.That(ok,Is.True,$"portal {route.Edge} must have a physical approach");
                    Assert.That(Vector2.Distance(new(approach.X,approach.Y),new(portal.X,portal.Y))+8,
                        Is.LessThanOrEqualTo(190),"Precise mover completion remains inside interaction range");
                }
                foreach (int dungeon in new[] {21,22,24,125,126,128,129,221,222,223})
                {
                    using var cmd=db.CreateCommand();
                    cmd.CommandText=$"select SourceRegion,SourceX,SourceY,SourceZ from ZonePoint where TargetRegion={dungeon} and SourceRegion<>{dungeon}";
                    using var row=cmd.ExecuteReader(); Assert.That(row.Read(),Is.True);
                    Region region=regions[row.GetInt32(0)];
                    Vector3 portal=new(row.GetInt32(1),row.GetInt32(2),row.GetInt32(3));
                    Zone zone=region.GetZone((int)portal.X,(int)portal.Y);
                    int connections=0;
                    for (int angle=0;angle<360;angle+=30)
                    {
                        float radians=angle*MathF.PI/180;
                        Vector3 raw=portal+new Vector3(MathF.Cos(radians)*500,MathF.Sin(radians)*500,0);
                        Vector3? start=nav.GetClosestPoint(zone,raw,96,96,512,nav.DefaultFilters);
                        if (start.HasValue && AutonomousZonePointApproach.TryResolve(nav,zone,start.Value,portal,174,out _)) connections++;
                    }
                    TestContext.WriteLine($"DUNGEON_EXTERIOR region={dungeon} source={portal} connectedApproaches={connections}");
                    Assert.That(connections,Is.GreaterThan(0),$"Dungeon {dungeon} must be approachable from outside its trigger");
                }
                foreach (string name in new[] {"orchard nipper","lugradan whelp","luricaduane","hill toad"})
                {
                    using var cmd=db.CreateCommand();
                    cmd.CommandText="select X,Y,Z,Level from Mob where Region=200 and Name=@name and Level>0";
                    cmd.Parameters.AddWithValue("@name",name);
                    using var rows=cmd.ExecuteReader();
                    int usable=0, total=0;
                    var levels=new HashSet<int>();
                    while (rows.Read())
                    {
                        Vector3 spawn=new(rows.GetInt32(0),rows.GetInt32(1),rows.GetInt32(2));
                        Zone zone=regions[200].GetZone((int)spawn.X,(int)spawn.Y);
                        if (zone == null) continue;
                        total++; levels.Add(rows.GetInt32(3));
                        if (!AutonomousRendezvousNavigation.TryChoosePoint(nav,zone,spawn,out Vector3 point)) continue;
                        Vector3? floor=nav.GetClosestPoint(zone,spawn,48,48,96,nav.DefaultFilters);
                        Assert.That(floor.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,point,floor.Value),
                            Is.True,name + " projected camp must still reach its actual spawn");
                        usable++;
                    }
                    TestContext.WriteLine($"LIVE_CAMP name={name} usable={usable}/{total} levels={string.Join(',',levels.Order())}");
                    Assert.That(usable,Is.GreaterThan(0),name + " must retain real reachable goals");
                }
                foreach (var serviceCase in new[] {
                    (Merchant:new Vector3(32499,28664,8777), Actor:new Vector3(32402,28498,8769)),
                    (Merchant:new Vector3(32250,28294,8819), Actor:new Vector3(32445,28463,8767)) })
                {
                Vector3 merchant=serviceCase.Merchant, actor=serviceCase.Actor;
                Zone jordheim=regions[101].GetZone((int)merchant.X,(int)merchant.Y);
                Assert.That(AutonomousRouteHotspotRepair.TryResolveJordheimServiceApproach(nav,jordheim,101,
                    actor,merchant,256,out Vector3 service),Is.True);
                Assert.That(Vector3.Distance(service,merchant)+8,Is.LessThan(256));
                TestContext.WriteLine($"CURRENT_SERVICE from={actor} safe={service}");
                }
            }
            finally
            {
                foreach (Zone zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory=prior;
            }
        }
    }
}
