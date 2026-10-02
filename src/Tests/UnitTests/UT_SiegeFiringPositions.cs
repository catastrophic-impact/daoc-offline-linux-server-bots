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
using DOL.Database;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture,Explicit("Read-only installed keep firing-position verification"),NonParallelizable]
    public class UT_SiegeFiringPositions
    {
        private static readonly IObjectDatabase Empty=DispatchProxy.Create<IObjectDatabase,UT_UnobservedConcentration.EmptyReads>();
        private sealed class Server : GameServer { protected override IObjectDatabase DataBaseImpl=>Empty; }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public override int X { get; set; }
            public override int Y { get; set; }
            public override int Z { get; set; }
            public override Region CurrentRegion { get; set; }
        }
        private sealed class Target : GameNPC
        {
            public override int MaxHealth=>1000;
            public override int X { get; set; }
            public override int Y { get; set; }
            public override int Z { get; set; }
        }
        [Test]
        public void ThreeRealmOuterGatesHaveConnectedRamPositions()
        {
            string root=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string previous=Environment.CurrentDirectory;
            var previousServer=GameServer.Instance;
            var previousNav=PathfindingProvider.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
            var loaded=new List<Zone>();
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name,assembly,search)=>name=="lib/Detour" ? NativeLibrary.Load(Path.Combine(root,"lib","Detour.dll")) : IntPtr.Zero);
                Environment.CurrentDirectory=root;
                using var db=new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
                db.Open();
                var nav=PathfindingProvider.LocalPathfindingMgr;
                typeof(PathfindingProvider).GetProperty(nameof(PathfindingProvider.Instance)).SetValue(null,nav);
                var choose=typeof(AutonomousWorldBotController).GetMethod("ChooseSiegePosition",BindingFlags.Instance|BindingFlags.NonPublic);
                foreach (var (id,regionId) in new[]{(50,1),(82,100),(110,200)})
                {
                    Region region=UT_AuditedDungeonInstalledMesh.BuildRegion(db,regionId,loaded);
                    using var q=db.CreateCommand();
                    q.CommandText=$"select Name,X,Y,Z from [Keep] where KeepID={id}";
                    string name; Vector3 keep;
                    using (var r=q.ExecuteReader()) { Assert.That(r.Read(),Is.True); name=r.GetString(0); keep=new(r.GetInt32(1),r.GetInt32(2),r.GetInt32(3)); }
                    q.CommandText=$"select X,Y,Z from Door where abs(X-{keep.X})<2200 and abs(Y-{keep.Y})<2200 and IsPostern=0";
                    var doors=new List<Vector3>();
                    using(var r=q.ExecuteReader()) while(r.Read()) doors.Add(new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2)));
                    Vector3 door=doors.OrderByDescending(d=>Vector2.Distance(new(d.X,d.Y),new(keep.X,keep.Y))).First();
                    Vector3 direction=Vector3.Normalize(new(door.X-keep.X,door.Y-keep.Y,0));
                    var zone=region.GetZone((int)door.X,(int)door.Y);
                    var origin=nav.GetClosestPoint(zone,door+direction*900,96,96,4096,nav.DefaultFilters);
                    Assert.That(origin.HasValue,Is.True,name+" exterior floor");
                    var bot=(Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
                    bot.CurrentRegion=region; bot.X=(int)origin.Value.X;bot.Y=(int)origin.Value.Y;bot.Z=(int)origin.Value.Z;
                    var target=new Target { X=(int)door.X,Y=(int)door.Y,Z=(int)door.Z };
                    var point=(Vector3?)choose.Invoke(new AutonomousWorldBotController(),new object[]{bot,target,BotSiegeKind.Ram,Array.Empty<GameSiegeWeapon>()});
                    TestContext.WriteLine($"{name}: door={door} origin={origin} ram={point}");
                    if (!point.HasValue)
                    {
                        for(int degrees=0;degrees<360;degrees+=30)
                        {
                            float a=degrees*MathF.PI/180;
                            var probe=nav.GetClosestPoint(zone,door+new Vector3(MathF.Cos(a)*310,MathF.Sin(a)*310,0),96,96,4096,nav.DefaultFilters);
                            TestContext.WriteLine($"wide {degrees}: {probe}");
                        }
                        float angle=MathF.Atan2(bot.Y-target.Y,bot.X-target.X);
                        for(int i=0;i<13;i++)
                        {
                            float offset=i==0 ? 0 : ((i-1)/2+1)*.22f*(i%2==0 ? -1:1);
                            Vector3 raw=new(target.X+MathF.Cos(angle+offset)*310,target.Y+MathF.Sin(angle+offset)*310,target.Z);
                            var floor=nav.GetClosestPoint(zone,raw,48,48,256,nav.DefaultFilters);
                            if (!floor.HasValue) { TestContext.WriteLine($"candidate {i}: no floor"); continue; }
                            bool path=AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,origin.Value,floor.Value);
                            bool los=nav.HasLineOfSight(zone,floor.Value+new Vector3(0,0,48),door+new Vector3(0,0,48),nav.DefaultFilters);
                            TestContext.WriteLine($"candidate {i}: floor={floor} distance={Vector3.Distance(floor.Value,door)} path={path} los={los}");
                            foreach(var d in new[]{new Vector3(40,0,0),new Vector3(-40,0,0),new Vector3(0,40,0),new Vector3(0,-40,0)})
                            {
                                var edge=nav.GetClosestPoint(zone,floor.Value+d,16,16,48,nav.DefaultFilters);
                                TestContext.WriteLine($"edge {d} {edge} deltaZ={(edge.HasValue?edge.Value.Z-floor.Value.Z:999)}");
                            }
                        }
                    }
                    Assert.That(point.HasValue,Is.True,name+" connected ram footprint within actual attack reach");
                    Assert.That(Vector3.Distance(point.Value,door),Is.LessThanOrEqualTo(375));
                }
            }
            finally { foreach(var zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone); Environment.CurrentDirectory=previous; GameServer.LoadTestDouble(previousServer); typeof(PathfindingProvider).GetProperty(nameof(PathfindingProvider.Instance)).SetValue(null,previousNav); }
        }
    }
}
