using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, Explicit("Read-only installed teleporter mesh check"), NonParallelizable]
public class UT_InstalledPorterWaiting
{
    [Test]
    public void EveryNativePorterHasClearReachableWaitingSpace()
    {
        string previous = Environment.CurrentDirectory;
        var loaded = new List<Zone>();
        try
        {
            Environment.CurrentDirectory = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            using var db = new SQLiteConnection($"Data Source={Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH")};Read Only=True;Pooling=False;");
            db.Open();
            var regions = new Dictionary<ushort,Region>();
            foreach (ushort id in new ushort[] {1,100,200})
            {
                var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                typeof(Region).GetField("m_regionData",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(region,new RegionData{Id=id});
                typeof(Region).GetField("m_zones",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(region,new List<Zone>());
                regions[id] = region;
            }
            using (var q = db.CreateCommand())
            {
                q.CommandText = "select ZoneID,Name,RegionID,OffsetX,OffsetY,Width,Height from Zones where RegionID in (1,100,200)";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    ushort id = (ushort)r.GetInt32(0);
                    var region = regions[(ushort)r.GetInt32(2)];
                    region.Zones.Add(new Zone(region,id,r.GetString(1),r.GetInt32(3)*8192,r.GetInt32(4)*8192,
                        r.GetInt32(5)*8192,r.GetInt32(6)*8192,id,false,0,false,0,0,0,0,0));
                }
            }
            using var query = db.CreateCommand();
            query.CommandText = "select Name,Region,X,Y,Z,Realm from Mob where ClassType='DOL.GS.Scripts.OFTeleporter'";
            using var reader = query.ExecuteReader();
            int porters = 0;
            while (reader.Read())
            {
                Vector3 center = new(reader.GetInt32(2),reader.GetInt32(3),reader.GetInt32(4));
                var zone = regions[(ushort)reader.GetInt32(1)].Zones.First(z => center.X >= z.XOffset && center.X < z.XOffset+z.Width && center.Y>=z.YOffset && center.Y<z.YOffset+z.Height);
                if (!loaded.Contains(zone)) { LocalPathfindingMgr.LoadNavMesh(zone); loaded.Add(zone); }
                var slots = new HashSet<Vector3>();
                for (long id = 1; id <= 128; id++)
                {
                    Assert.That(AutonomousFrontierTransport.TryWaitingPoint(PathfindingProvider.LocalPathfindingMgr,zone,center,
                        (eRealm)reader.GetInt32(5),id,out var point),Is.True,$"{reader.GetString(0)} region {reader.GetInt32(1)} bot {id}");
                    Assert.That(Vector3.Distance(center,point),Is.LessThanOrEqualTo(450));
                    slots.Add(point);
                }
                Assert.That(slots.Count,Is.GreaterThan(100));
                TestContext.Progress.WriteLine($"{reader.GetString(0)} region {reader.GetInt32(1)}: 128 valid waiting positions");
                porters++;
            }
            Assert.That(porters,Is.EqualTo(9));
        }
        finally
        {
            foreach (var zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }
}
