using System;
using System.Data.SQLite;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable, Explicit("Read-only installed hotspot navigation checks")]
public class UT_September11HotspotProbe
{
    private readonly System.Collections.Generic.Dictionary<ushort, Zone> _zones = new();
    [OneTimeTearDown]
    public void Unload()
    {
        foreach (var zone in _zones.Values) LocalPathfindingMgr.UnloadNavMesh(zone);
    }
    [TestCase(51,530777,543337,3678,525872,542106,3173)]
    [TestCase(128,31694,33000,16009,32150,32989,16012)]
    [TestCase(128,32924,34022,16003,32930,34097,16003)]
    [TestCase(222,33951,30258,14711,35196,30235,14668)]
    public void Probe(int region, int x,int y,int z,int tx,int ty,int tz)
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        string previous = Environment.CurrentDirectory;
        Zone zone = null;
        try
        {
            Environment.CurrentDirectory = root;
            using var db = new SQLiteConnection($"Data Source={root}/../data/opendaoc.sqlite3.db;Read Only=True;Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"select ZoneID,OffsetX,OffsetY,Width,Height from Zones where RegionID={region} and {x}>=OffsetX*8192 and {x}<(OffsetX+Width)*8192 and {y}>=OffsetY*8192 and {y}<(OffsetY+Height)*8192 limit 1";
            using var reader = cmd.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            ushort id = Convert.ToUInt16(reader[0]);
            if (!_zones.TryGetValue(id, out zone))
            {
                zone = new Zone(null,id,"Audit",Convert.ToInt32(reader[1])*8192,Convert.ToInt32(reader[2])*8192,Convert.ToInt32(reader[3])*8192,Convert.ToInt32(reader[4])*8192,id,false,0,false,0,0,0,0,0);
                LocalPathfindingMgr.LoadNavMesh(zone);
                _zones.Add(id, zone);
            }
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var from = nav.GetClosestPoint(zone,new Vector3(x,y,z),96,96,256,nav.DefaultFilters);
            var to = nav.GetClosestPoint(zone,new Vector3(tx,ty,tz),96,96,256,nav.DefaultFilters);
            TestContext.WriteLine($"region={region} source={x},{y},{z} floor={from} targetFloor={to}");
            Assert.That(from.HasValue && to.HasValue, Is.True);
            Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
            var result=nav.GetPathStraight(zone,from.Value,to.Value,nav.DefaultFilters,nodes);
            TestContext.WriteLine($"path={result.Status} count={result.NodeCount}");
            for(int i=0;i<Math.Min(12,result.NodeCount);i++) TestContext.WriteLine(nodes[i].Position);
            Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,from.Value,to.Value),Is.True);
        }
        finally { Environment.CurrentDirectory=previous; }
    }
}
