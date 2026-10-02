using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Linq;
using DOL.Database;
using DOL.GS;
using DOL.GS.Keeps;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, Explicit("Read-only installed Crauchon response incident"), NonParallelizable]
public class UT_CrauchonResponseRoutes
{
    private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    private sealed class Server : GameServer { protected override IObjectDatabase DataBaseImpl => Empty; }
    private sealed class ClosedDoor : GameDoorBase
    {
        public override int X { get; set; }
        public override int Y { get; set; }
        public override int Z { get; set; }
        public override Region CurrentRegion { get; set; }
        public override eDoorState State { get; set; } = eDoorState.Closed;
    }
    private sealed class SiegeBot : GameBot
    {
        private SiegeBot():base((OfflineWorldBotRecord)null){}
        public override eRealm Realm {get;set;}
        public override int X {get;set;}
        public override int Y {get;set;}
        public override int Z {get;set;}
        public override Region CurrentRegion {get;set;}
        public void Class(ICharacterClass value) => m_characterClass = value;
    }
    [Test]
    public void CapturedRespondersReachTheKeep()
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        string previous = Environment.CurrentDirectory;
        var loaded = new List<Zone>();
        var previousServer = GameServer.Instance;
        var previousNav=PathfindingProvider.Instance;
        GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(Path.Combine(root,"lib","Detour.dll")) : IntPtr.Zero);
            Environment.CurrentDirectory = root;
            using var db = new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
            db.Open();
            var region = UT_AuditedDungeonInstalledMesh.BuildRegion(db,200,loaded);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            PathfindingProvider.SetPathfindingMgr(nav);
            var incidentDoors=new List<ClosedDoor>();
            using(var q=db.CreateCommand())
            {
                q.CommandText="select X,Y,Z from Door where abs(X-425877)<2500 and abs(Y-318720)<2500";
                using var r=q.ExecuteReader();
                while(r.Read())
                {
                    var door=(ClosedDoor)RuntimeHelpers.GetUninitializedObject(typeof(ClosedDoor));
                    door.CurrentRegion=region;door.X=r.GetInt32(0);door.Y=r.GetInt32(1);door.Z=r.GetInt32(2);
                    door.State=eDoorState.Closed;incidentDoors.Add(door);
                    TestContext.WriteLine($"DOOR {door.X},{door.Y},{door.Z}: registered={nav.RegisterDoor(door)}");
                }
            }
            var nodes = new WrappedPathfindingNode[512];
            foreach(var (name, realm, start, oldGoal) in new (string,eRealm,Vector3,Vector3)[] {
                ("Isaolyn",eRealm.Albion,new(445970,334336,4462),new(426569,319120,4729)),
                ("Cedoalfrey",eRealm.Albion,new(444006,333722,3772),new(426569,319120,4729)),
                ("Finnaensten",eRealm.Midgard,new(435148,310682,6198),new(426569,318320,4729)),
                ("Ranred",eRealm.Albion,new(449741,337716,5787),new(426569,319120,4729)),
                ("Beroorwin",eRealm.Albion,new(449741,338748,3613),new(426569,319120,4729)),
                ("Ivareunvald",eRealm.Midgard,new(449336,299008,4259),new(426649,318512,4729)),
                ("Sigerun",eRealm.Midgard,new(444518,301226,4289),new(426649,318512,4729)),
                ("Toraildbrand",eRealm.Midgard,new(444692,301980,4600),new(426649,318512,4729)) })
            {
                var zone = region.GetZone((int)start.X,(int)start.Y);
                var raw = nav.GetPathStraight(zone,start,oldGoal,nav.DefaultFilters,nodes);
                var blocked = nav.GetPathStraight(zone,start,oldGoal,nav.BlockingDoorAvoidanceFilters,nodes);
                TestContext.WriteLine($"BLOCKED {name}: {blocked.Status}/{blocked.NodeCount} last={(blocked.NodeCount>0?nodes[blocked.NodeCount-1].Position:default)}");
                bool resolved = AutonomousRvrApproach.TryResolveAcrossZones(nav,region,realm,start,new(425877,318720,4776),out var goal);
                TestContext.WriteLine($"{name}: zone={zone.ID} old={raw.Status}/{raw.NodeCount} last={(raw.NodeCount>0?nodes[raw.NodeCount-1].Position:default)} resolved={resolved} new={goal} floor={nav.GetClosestPoint(zone,start,2,2,128,nav.DefaultFilters)}");
                Assert.That(resolved,Is.True,name);
                Assert.That(blocked.Status,Is.EqualTo(PathfindingStatus.PartialPathFound),name+" reproduces closed-door rejection of the old goal");
                Assert.That(AutonomousRvrRally.HasRoute(region,new AutonomousKeepApproachNavigation(nav,[]),realm,start,goal),Is.True,name);
            }
            var outer=incidentDoors.OrderByDescending(d=>Vector2.DistanceSquared(new(d.X,d.Y),new(425877,318720))).First();
            Vector3 source=new(445970,334336,4462), courtyard=new(426569,319120,4729);
            var friendly=new AutonomousKeepApproachNavigation(nav,[new(outer.X,outer.Y,outer.Z)]);
            foreach (var (name,start) in new (string,Vector3)[] {
                ("Rianaeren",new(335548,624723,4598)), ("Aineaeldra",new(373248,512643,5181)),
                ("Lorcelinren",new(412620,483584,8237)), ("Deiraelse",new(310019,645077,4851)),
                ("Ciarelren",new(332743,484993,5215)), ("Fiaininrin",new(296550,642355,4851)),
                ("Ainelorrin",new(306514,644851,4851)), ("Fiaeina",new(347595,690720,6952)),
                ("Dairaornan",new(329717,625768,4599)), ("Saoribhe",new(343396,591429,5459)),
                ("Caoelina",new(402115,485786,9312)), ("Eileinra",new(345063,528715,5448)) })
                ResolveInSlices(friendly, work => AutonomousRvrApproach.TryResolveAcrossZones(work,region,eRealm.Hibernia,
                    start,new(425877,318720,4776),out var endpoint) &&
                    RvrKeepRoute.TryBuild(region,work,eRealm.Hibernia,start,endpoint,out _), "September15 defense " + name);
            Assert.That(AutonomousRvrRally.HasRoute(region,friendly,eRealm.Hibernia,source,courtyard),Is.True,"Defenders may use their own closed gate");
            outer.State=eDoorState.Open;nav.UpdateDoorFlags(outer);
            Assert.That(AutonomousRvrRally.HasRoute(region,new AutonomousKeepApproachNavigation(nav,[]),eRealm.Albion,source,courtyard),Is.True,"Attackers enter after the real gate opens");
            outer.State=eDoorState.Closed;nav.UpdateDoorFlags(outer);
            Vector3 muiraese=new(364253,699750,6281);
            Vector3[] repaired=[];
            ResolveInSlices(friendly, work => AutonomousRvrApproach.TryResolveAcrossZones(work,region,eRealm.Hibernia,
                muiraese,new(425877,318720,4776),out var endpoint) &&
                RvrKeepRoute.TryBuild(region,work,eRealm.Hibernia,muiraese,endpoint,out repaired), "Muiraese defensive incident");
            Assert.That(repaired,Does.Contain(new Vector3(361354,750434,4944)),"Uses connected Bog of Cullen road");
            Assert.That(repaired,Does.Contain(new Vector3(334820,719979,4296)),"Exits via Innis Carthaig");
            AuditAllKeeps(db,region,nav,loaded,incidentDoors);
        }
        finally { foreach(var zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);Environment.CurrentDirectory=previous; GameServer.LoadTestDouble(previousServer);PathfindingProvider.SetPathfindingMgr(previousNav); }
    }

    private sealed record Keep(int Id,string Name,ushort Region,Vector3 Point,eRealm Realm);
    private sealed class PostLord : GuardLord
    {
        public override int X { get; set; } public override int Y { get; set; } public override int Z { get; set; }
        public override eRealm Realm { get; set; } public override Region CurrentRegion { get; set; }
    }
    private sealed class PostArcher : GuardArcher
    {
        public override int X { get; set; } public override int Y { get; set; } public override int Z { get; set; }
        public override eRealm Realm { get; set; } public override Region CurrentRegion { get; set; }
    }
    private sealed class PostCommander : GuardCommander
    {
        public override int X { get; set; } public override int Y { get; set; } public override int Z { get; set; }
        public override eRealm Realm { get; set; } public override Region CurrentRegion { get; set; }
    }
    private sealed class PostFighter : GuardFighter
    {
        public override int X { get; set; } public override int Y { get; set; } public override int Z { get; set; }
        public override eRealm Realm { get; set; } public override Region CurrentRegion { get; set; }
    }
    private sealed class PostDoor : GameKeepDoor
    {
        public override int X { get; set; } public override int Y { get; set; } public override int Z { get; set; }
        public override Region CurrentRegion { get; set; }
        public override bool IsAttackableDoor => true;
        public override bool IsAlive => State == eDoorState.Closed;
        public override eDoorState State { get; set; }
    }

    private static void VerifyDefensePosts(SQLiteConnection db, Region region, IPathfindingMgr nav, Keep data, Vector3 start, List<Vector3> gates)
    {
        var keep = new GameKeep { DBKeep = new DbKeep { KeepID = data.Id, Region = data.Region,
            X = (int)data.Point.X, Y = (int)data.Point.Y, Z = (int)data.Point.Z, Realm = (byte)data.Realm, Name = data.Name } };
        foreach(var p in gates)
        {
            var d=(PostDoor)RuntimeHelpers.GetUninitializedObject(typeof(PostDoor));
            d.CurrentRegion=region; d.X=(int)p.X; d.Y=(int)p.Y; d.Z=(int)p.Z; d.State=eDoorState.Closed;
            keep.Doors.Add(keep.Doors.Count.ToString(),d);
        }
        using var q = db.CreateCommand();
        q.CommandText = "select X,Y,Z,ClassType from Mob where Region=@r and abs(X-@x)<2400 and abs(Y-@y)<2400 and (ClassType like '%GuardLord' or ClassType like '%GuardCommander' or ClassType like '%Archer' or ClassType like '%GuardFighter')";
        q.Parameters.AddWithValue("@r",data.Region); q.Parameters.AddWithValue("@x",data.Point.X); q.Parameters.AddWithValue("@y",data.Point.Y);
        using var rows = q.ExecuteReader();
        while(rows.Read())
        {
            string type = rows.GetString(3);
            Type guardType = type.EndsWith("GuardLord") ? typeof(PostLord) : type.EndsWith("GuardCommander") ? typeof(PostCommander) :
                type.EndsWith("Archer") ? typeof(PostArcher) : type.EndsWith("GuardFighter") ? typeof(PostFighter) : null;
            if (guardType == null) continue;
            GameKeepGuard guard = (GameKeepGuard)RuntimeHelpers.GetUninitializedObject(guardType);
            guard.CurrentRegion = region; guard.X = rows.GetInt32(0); guard.Y = rows.GetInt32(1); guard.Z = rows.GetInt32(2); guard.Realm = data.Realm;
            keep.Guards.Add(keep.Guards.Count.ToString(),guard);
        }
        if (!keep.Guards.Values.OfType<GuardLord>().Any()) keep.DBKeep.SkinType = 99;
        var bot=(SiegeBot)RuntimeHelpers.GetUninitializedObject(typeof(SiegeBot));
        bot.CurrentRegion=region; bot.Realm=data.Realm; bot.X=(int)start.X; bot.Y=(int)start.Y; bot.Z=(int)start.Z;
        foreach(int phase in new[]{0,1,2})
        foreach(var cls in new ICharacterClass[]{new ClassHero(),new ClassDruid(),new ClassEldritch()})
        {
            if(phase==1) keep.Doors.Values.OrderByDescending(d=>Vector2.DistanceSquared(new(d.X,d.Y),new(keep.X,keep.Y))).First().State=eDoorState.Open;
            if(phase==2) foreach(var d in keep.Doors.Values) d.State=eDoorState.Open;
            bot.Class(cls);
            Vector3 post=default;
            ResolveInSlices(nav,work => AutonomousRvrRally.TryDefenderPost(bot,keep,137,work,start,out post),
                $"DEFENDER INTERIOR {data.Name} {cls.ID} phase={phase}");
            Assert.That(Vector3.DistanceSquared(start,post),Is.GreaterThan(80*80),"Exterior arrival must not be the interior post");
            if(phase==2)
                Assert.That(keep.Guards.Values.Where(g=>g is GuardLord or GuardCommander).Any(g=>Vector3.DistanceSquared(new(g.X,g.Y,g.Z),post)<160*160),
                    Is.True,"Every class protects the lord/commander once both gates are breached");
        }
    }
    private static void ResolveInSlices(IPathfindingMgr nav,Func<RvrPlanningNavigation,bool> resolve,string label)
    {
        var work=new RvrPlanningNavigation(nav);
        double max=0,total=0;
        for(int slice=0;slice<2000;slice++)
        {
            work.BeginSlice();var timer=System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool found=resolve(work);
                max=Math.Max(max,timer.Elapsed.TotalMilliseconds);total+=timer.Elapsed.TotalMilliseconds;
                TestContext.Progress.WriteLine($"SLICED {label} found={found} slices={slice+1} maxMs={max:F2} totalMs={total:F2} queries={work.Queries}");
                Assert.That(found,Is.True,label);
                Assert.That(max,Is.LessThan(500),label+" must not monopolize the game loop");
                return;
            }
            catch(RvrPlanningNavigation.Yield)
            { max=Math.Max(max,timer.Elapsed.TotalMilliseconds);total+=timer.Elapsed.TotalMilliseconds; }
        }
        Assert.Fail(label+" never finished its bounded search");
    }
    private static void AuditAllKeeps(SQLiteConnection db,Region emain,IPathfindingMgr nav,List<Zone> loaded,List<ClosedDoor> incidentDoors)
    {
        var keeps=new List<Keep>();
        using(var q=db.CreateCommand())
        {
            q.CommandText="select KeepID,Name,Region,X,Y,Z,Realm from [Keep] where Region in (1,100,200)";
            using var r=q.ExecuteReader();
            while(r.Read())keeps.Add(new(r.GetInt32(0),r.GetString(1),(ushort)r.GetInt32(2),new(r.GetInt32(3),r.GetInt32(4),r.GetInt32(5)),(eRealm)r.GetInt32(6)));
        }
        int passed=0;var failures=new List<string>();
        foreach(ushort regionId in new ushort[]{1,100,200})
        {
            var region=regionId==200?emain:UT_AuditedDungeonInstalledMesh.BuildRegion(db,regionId,loaded);
            var friendly=new Dictionary<eRealm,List<Vector3>> { [eRealm.Albion]=new(),[eRealm.Midgard]=new(),[eRealm.Hibernia]=new() };
            var gates=new Dictionary<int,List<Vector3>>();
            var registered=new List<ClosedDoor>();
            var attackStarts=new Dictionary<int,Vector3>();
            foreach(var keep in keeps.Where(k=>k.Region==regionId))
            {
                using var q=db.CreateCommand();
                gates[keep.Id]=new();
                q.CommandText="select d.X,d.Y,d.Z,d.IsPostern from Door d join Zones z on cast(d.InternalID/1000000 as integer)=z.ZoneID where z.RegionID=@region and abs(d.X-@x)<2500 and abs(d.Y-@y)<2500";
                q.Parameters.AddWithValue("@region",regionId);q.Parameters.AddWithValue("@x",keep.Point.X);q.Parameters.AddWithValue("@y",keep.Point.Y);
                using var r=q.ExecuteReader();
                while(r.Read())
                {
                    var point=new Vector3(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2));
                    friendly[keep.Realm].Add(point);
                    if(r.GetInt32(3)==0)gates[keep.Id].Add(point);
                    if(keep.Id==100)
                    {
                        registered.Add(incidentDoors.Single(d=>d.X==(int)point.X && d.Y==(int)point.Y && d.Z==(int)point.Z));
                        continue; // Register once; closed polygons no longer carry the Door lookup flag.
                    }
                    var door=(ClosedDoor)RuntimeHelpers.GetUninitializedObject(typeof(ClosedDoor));
                    door.CurrentRegion=region;door.X=(int)point.X;door.Y=(int)point.Y;door.Z=(int)point.Z;
                    door.State=eDoorState.Closed;
                    nav.RegisterDoor(door);
                    registered.Add(door);
                }
            }
            foreach(var keep in keeps.Where(k=>k.Region==regionId && k.Id>=50))
            foreach(var realm in new[]{eRealm.Albion,eRealm.Midgard,eRealm.Hibernia})
            {
                var arrival=AutonomousFrontierTransport.Destination(realm,regionId).Location;
                var start=new Vector3(arrival.X,arrival.Y,arrival.Z);
                var usable=new AutonomousKeepApproachNavigation(nav,friendly[realm].ToArray());
                TestContext.Progress.WriteLine($"START {realm} -> {keep.Name}");
                var clock=System.Diagnostics.Stopwatch.StartNew();
                Vector3 goal=default;
                ResolveInSlices(usable,work => {
                    bool found=realm==keep.Realm
                        ? AutonomousRvrApproach.TryResolveAcrossZones(work,region,realm,start,keep.Point,out goal)
                        : AutonomousRvrApproach.TryGateApproach(work,region,realm,start,keep.Point,gates[keep.Id],out goal);
                    return found && RvrKeepRoute.TryBuild(region,work,realm,start,goal,out _);
                },$"{realm} -> {keep.Name}");
                bool path=true;
                TestContext.Progress.WriteLine($"ALL KEEPS {realm} -> {keep.Name}: {(path?"PASS":"FAIL")} approach={goal} ms={clock.ElapsedMilliseconds}");
                if(path)passed++;else failures.Add($"{realm} -> {keep.Name}");
                if(path && realm!=keep.Realm)
                {
                    attackStarts[keep.Id]=goal;
                    var outer=gates[keep.Id].OrderByDescending(p=>Vector2.DistanceSquared(new(p.X,p.Y),new(keep.Point.X,keep.Point.Y))).First();
                    var bot=(SiegeBot)RuntimeHelpers.GetUninitializedObject(typeof(SiegeBot));
                    bot.CurrentRegion=region;bot.X=(int)goal.X;bot.Y=(int)goal.Y;bot.Z=(int)goal.Z;
                    var target=(ClosedDoor)RuntimeHelpers.GetUninitializedObject(typeof(ClosedDoor));
                    target.CurrentRegion=region;target.X=(int)outer.X;target.Y=(int)outer.Y;target.Z=(int)outer.Z;target.State=eDoorState.Closed;
                    var choose=typeof(AutonomousWorldBotController).GetMethod("ChooseSiegePosition",BindingFlags.Instance|BindingFlags.NonPublic);
                    var ram=(Vector3?)choose.Invoke(new AutonomousWorldBotController(),new object[]{bot,target,BotSiegeKind.Ram,Array.Empty<GameSiegeWeapon>()});
                    TestContext.Progress.WriteLine($"RAM {realm} -> {keep.Name}: {ram}");
                    if(!ram.HasValue || !AutonomousRvrRally.HasRoute(region,usable,realm,goal,ram.Value))failures.Add($"RAM {realm} -> {keep.Name}");
                    for(int side=-480;side<=480;side+=60)
                    {
                        int offset=side;
                        ResolveInSlices(usable,work => AutonomousRvrApproach.TryGateApproach(work,region,realm,goal,keep.Point,gates[keep.Id],out _,offset),
                            $"WAIT {realm} -> {keep.Name} offset={offset}");
                    }
                }
            }
            foreach(var keep in keeps.Where(k=>k.Region==regionId && k.Id>=50))
                VerifyDefensePosts(db,region,new AutonomousKeepApproachNavigation(nav,friendly[keep.Realm].ToArray()),keep,attackStarts[keep.Id],gates[keep.Id]);
            foreach(var keep in keeps.Where(k=>k.Region==regionId && k.Id>=50 && gates[k.Id].Count>1))
            {
                var outer=gates[keep.Id].OrderByDescending(p=>Vector2.DistanceSquared(new(p.X,p.Y),new(keep.Point.X,keep.Point.Y))).First();
                var door=registered.Single(d=>d.X==(int)outer.X && d.Y==(int)outer.Y && d.Z==(int)outer.Z);
                door.State=eDoorState.Open;nav.UpdateDoorFlags(door);
                var remaining=gates[keep.Id].Where(p=>p!=outer).ToArray();
                var enemy=keep.Realm==eRealm.Albion?eRealm.Midgard:eRealm.Albion;
                ResolveInSlices(new AutonomousKeepApproachNavigation(nav,[]),work=>
                    AutonomousRvrApproach.TryGateApproach(work,region,enemy,attackStarts[keep.Id],keep.Point,remaining,out _),
                    $"OUTER BREACH {keep.Name} -> next intact gate");
                door.State=eDoorState.Closed;nav.UpdateDoorFlags(door);
            }
            foreach(var door in registered){door.State=eDoorState.Open;nav.UpdateDoorFlags(door);}
            foreach(var keep in keeps.Where(k=>k.Region==regionId && k.Id>=50))
            {
                using var q=db.CreateCommand();
                q.CommandText="select X,Y,Z,'lord' from Mob where Region=@region and abs(X-@x)<2500 and abs(Y-@y)<2500 and ClassType like '%GuardLord' union all select X,Y,Z,'relic' from Relic where Region=@region and abs(X-@x)<2500 and abs(Y-@y)<2500";
                q.Parameters.AddWithValue("@region",regionId);q.Parameters.AddWithValue("@x",keep.Point.X);q.Parameters.AddWithValue("@y",keep.Point.Y);
                using var r=q.ExecuteReader();int objectives=0;
                while(r.Read())
                {
                    objectives++;
                    Vector3 target=new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2));
                    var targetZone=region.GetZone((int)target.X,(int)target.Y);
                    var diagnosticFloor=nav.GetClosestPoint(targetZone,target,64,64,128,nav.DefaultFilters);
                    var diagnosticNodes=new WrappedPathfindingNode[512];
                    var diagnostic=diagnosticFloor.HasValue?nav.GetPathStraight(targetZone,attackStarts[keep.Id],diagnosticFloor.Value,nav.DefaultFilters,diagnosticNodes):default;
                    TestContext.Progress.WriteLine($"INTERIOR {keep.Name} start={attackStarts[keep.Id]} floor={diagnosticFloor} raw={diagnostic.Status}/{diagnostic.NodeCount} last={(diagnostic.NodeCount>0?diagnosticNodes[diagnostic.NodeCount-1].Position:default)} blocks={diagnosticNodes.Take(diagnostic.NodeCount).Count(n=>(n.Flags&EDtPolyFlags.BlockingDoor)!=0)}");
                    var enemy=keep.Realm==eRealm.Albion?eRealm.Midgard:eRealm.Albion;
                    ResolveInSlices(new AutonomousKeepApproachNavigation(nav,[]), work=> {
                        var floor=work.GetClosestPoint(targetZone,target,64,64,128,work.DefaultFilters);
                        return floor.HasValue && RvrKeepRoute.TryBuild(region,work,enemy,attackStarts[keep.Id],floor.Value,out _);
                    },$"BREACHED {keep.Name} -> {r.GetString(3)} {target}");
                }
                Assert.That(objectives,Is.GreaterThan(0),keep.Name+" has a lord or relic to reach after breach");
            }
        }
        Assert.That(failures,Is.Empty,string.Join("; ",failures));
        Assert.That(passed,Is.EqualTo(81));
    }
}
