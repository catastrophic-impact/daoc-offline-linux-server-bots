using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Linq;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable, Explicit("Read-only installed keep navigation")]
public class UT_InstalledKeepRoutes
{
    [Test]
    public void EveryClassicKeepHasAnEntranceAndReachableInteriorFloors()
    {
        string previous=Environment.CurrentDirectory;
        var zones=new Dictionary<ushort,Zone>();
        try
        {
            Environment.CurrentDirectory=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            using var db=new SQLiteConnection($"Data Source={Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH")};Read Only=True;Pooling=False;");
            db.Open();
            using(var command=db.CreateCommand())
            {
                command.CommandText="select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID in (1,100,200)";
                using var r=command.ExecuteReader();
                while(r.Read()) zones[(ushort)r.GetInt32(0)]=new Zone(null,(ushort)r.GetInt32(0),r.GetString(1),r.GetInt32(2)*8192,r.GetInt32(3)*8192,r.GetInt32(4)*8192,r.GetInt32(5)*8192,(ushort)r.GetInt32(0),false,0,false,0,0,0,0,0);
            }
            var keeps=new List<(string Name,int Region,int X,int Y)>();
            using(var command=db.CreateCommand())
            {
                command.CommandText="select Name,Region,X,Y from [Keep] where Region in (1,100,200) and Name not like '%Portal%'";
                using var r=command.ExecuteReader();
                while(r.Read()) keeps.Add((r.GetString(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3)));
            }
            int tested=0;
            foreach(var keep in keeps)
            {
                var doors=new List<(ushort Zone,Vector3 Point)>();
                using(var command=db.CreateCommand())
                {
                    command.CommandText="select InternalID,X,Y,Z from Door where abs(X-@x)<2500 and abs(Y-@y)<2500 and IsPostern=0";
                    command.Parameters.AddWithValue("@x",keep.X);command.Parameters.AddWithValue("@y",keep.Y);
                    using var r=command.ExecuteReader();
                    while(r.Read()) doors.Add(((ushort)(r.GetInt32(0)/1000000),new(r.GetInt32(1),r.GetInt32(2),r.GetInt32(3))));
                }
                if(doors.Count==0) { Assert.Fail($"{keep.Name}: no authored entrance"); }
                var zone=zones[doors[0].Zone];
                if(!zone.IsPathfindingEnabled) LocalPathfindingMgr.LoadNavMesh(zone);
                var nav=PathfindingProvider.LocalPathfindingMgr;
                int interior=0,wall=0;
                using(var command=db.CreateCommand())
                {
                    command.CommandText="select X,Y,Z,ClassType from Mob where Region=@r and abs(X-@x)<2400 and abs(Y-@y)<2400 and (ClassType like '%GuardLord' or ClassType like '%Archer' or ClassType like '%GuardCommander' or ClassType like '%GuardFighter')";
                    command.Parameters.AddWithValue("@r",keep.Region);command.Parameters.AddWithValue("@x",keep.X);command.Parameters.AddWithValue("@y",keep.Y);
                    using var r=command.ExecuteReader();
                    while(r.Read())
                    {
                        var p=new Vector3(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2));
                        var floor=nav.GetClosestPoint(zone,p,64,64,64,nav.DefaultFilters);
                        if(floor==null || !doors.Any(d=>AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,d.Point,floor.Value)))continue;
                        if(r.GetString(3).EndsWith("Archer"))wall++;else interior++;
                    }
                }
                TestContext.WriteLine($"{keep.Name}: doors={doors.Count}, reachable interior guards={interior}, wall archers={wall}");
                Assert.That(interior,Is.GreaterThan(0),keep.Name);
                Assert.That(wall,Is.GreaterThan(0),keep.Name);
                tested++;
            }
            Assert.That(tested,Is.EqualTo(27));
        }
        finally {foreach(var zone in zones.Values.Where(z=>z.IsPathfindingEnabled))LocalPathfindingMgr.UnloadNavMesh(zone);Environment.CurrentDirectory=previous;}
    }
}
