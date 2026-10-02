using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.IO;
using System.Data.SQLite;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

public class UT_RealmRaidRecruitment
{
    [TestCase(false, 60, false)] [TestCase(false, 89, false)] [TestCase(false, 90, true)]
    [TestCase(true, 89, false)] [TestCase(true, 90, true)]
    public void AutomaticRallyNowHasNinetyMinutesWhileForcedLimitIsUnchanged(bool forced, int minutes, bool expired) =>
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(forced, minutes * 60_000L), Is.EqualTo(expired));

    [Test]
    public void BattleAndEarlyStartRulesAreUnchanged()
    {
        Assert.That(RealmRaidRecruitmentPolicy.BattleMilliseconds, Is.EqualTo(4 * 60 * 60_000L));
        Assert.That(RealmRaidRecruitmentPolicy.ForcedStagingMilliseconds, Is.EqualTo(45 * 60_000L));
        Assert.That(RealmRaidRecruitmentPolicy.Ready(false,15 * 60_000L,200,true),Is.True);
        Assert.That(RealmRaidRecruitmentPolicy.MaximumBots,Is.EqualTo(300));
    }
    [TestCase(49, true, false, false, false)] [TestCase(50, true, false, false, true)]
    [TestCase(50, false, false, false, false)] [TestCase(50, true, true, false, false)]
    [TestCase(50, true, false, true, false)] [TestCase(1, true, false, false, false)]
    public void OnlyLevelFiftyAutonomousBotsAreEligible(int level, bool autonomous, bool temporary, bool playerLed, bool expected) =>
        Assert.That(RealmRaidRecruitmentPolicy.Eligible(level, autonomous, temporary, playerLed), Is.EqualTo(expected));

    [TestCase(true, 44, 240, true, false)] [TestCase(true, 45, 200, true, true)]
    [TestCase(true, 45, 199, true, false)] [TestCase(true, 50, 240, false, false)]
    [TestCase(false, 14, 240, true, false)] [TestCase(false, 15, 200, true, true)]
    [TestCase(false, 45, 199, true, false)] [TestCase(false, 59, 8, true, false)]
    [TestCase(false, 30, 240, false, false)]
    public void AssaultNeedsRealAttendanceAndLanding(bool forced, int minutes, int present, bool landed, bool expected) =>
        Assert.That(RealmRaidRecruitmentPolicy.Ready(forced, minutes * 60000L, present, landed), Is.EqualTo(expected));

    [TestCase(false,199,false)] [TestCase(false,200,true)] [TestCase(false,203,true)] [TestCase(false,300,true)] [TestCase(true,0,true)]
    public void HubDepartureCountsIndividualsAndDoesNotRecallConvoy(bool departed, int ready, bool expected) =>
        Assert.That(RealmRaidRecruitmentPolicy.DepartHub(departed,ready), Is.EqualTo(expected));

    [TestCase("dragon-albion","Yarley's farm")]
    [TestCase("dragon-midgard","West Skona")]
    [TestCase("dragon-hibernia","Innis Carthaig")]
    public void RequestedDragonHubsAreRetained(string id,string name) =>
        Assert.That(RealmRaidMuster.Hubs.Single(h=>h.Event==id).Name,Is.EqualTo(name));

    [TestCase(false)] [TestCase(true)]
    public void AutomaticAndForcedPartiesReceiveTheSameSimultaneousHubOrder(bool forced)
    {
        var flags=System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var manager=typeof(AutonomousRealmRaid);
        var raid=Activator.CreateInstance(manager.GetNestedType("Raid",System.Reflection.BindingFlags.NonPublic),true);
        var party=Activator.CreateInstance(manager.GetNestedType("Party",System.Reflection.BindingFlags.NonPublic),true);
        var hub=RealmRaidMuster.Hubs[0];
        raid.GetType().GetField("Definition").SetValue(raid,AutonomousRealmRaid.Definitions[0]);
        raid.GetType().GetField("Hub").SetValue(raid,hub);
        raid.GetType().GetField("Forced").SetValue(raid,forced);
        party.GetType().GetField("HubPost").SetValue(party,hub.Center);
        manager.GetMethod("UpdateView",flags).Invoke(null,new[]{raid,party});
        var view=(AutonomousRealmRaid.View)party.GetType().GetField("View").GetValue(party);
        Assert.That(view.Muster,Is.True);
        Assert.That(view.Hold,Is.True);
        Assert.That(view.Camp.ZoneName,Is.EqualTo("Yarley's farm"));
        Assert.That(view.Camp.X,Is.EqualTo((int)hub.Center.X));
    }

    [Test, Explicit("Read-only installed service hub navigation check"), NonParallelizable]
    public void ThirtyOpenConnectedPostsAtAllSixHubs()
    {
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            foreach (var hub in RealmRaidMuster.Hubs)
            {
                var zone = new Zone(null,hub.Zone,hub.Name,hub.OffsetX*8192,hub.OffsetY*8192,65536,65536,hub.Zone,false,0,false,0,0,0,0,0);
                LocalPathfindingMgr.LoadNavMesh(zone);
                try
                {
                    var posts = new List<Vector3>();
                    for (int slot=0;slot<RealmRaidRecruitmentPolicy.MaximumParties;slot++)
                    {
                        bool valid = RealmRaidMuster.TryPost(PathfindingProvider.LocalPathfindingMgr,zone,hub,slot,posts,out var point);
                        Assert.That(valid,Is.True,$"{hub.Name} slot {slot}");
                        posts.Add(point);
                    }
                    TestContext.Progress.WriteLine($"{hub.Name}: 30 connected open posts validated");
                }
                finally { LocalPathfindingMgr.UnloadNavMesh(zone); }
            }
        }
        finally { Environment.CurrentDirectory=previous; }
    }

    [Test, Explicit("Read-only installed database and native expedition approach checks"), NonParallelizable]
    public void ServiceHubsHaveSafeSpaceAndOutboundRoutes()
    {
        string previous = Environment.CurrentDirectory;
        try
        {
            string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
            if (!string.IsNullOrWhiteSpace(native))
                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name, assembly, search) => name == "lib/Detour" ? System.Runtime.InteropServices.NativeLibrary.Load(native) : IntPtr.Zero);
            Environment.CurrentDirectory = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            using var db = new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;");
            db.Open();
            foreach (var hub in RealmRaidMuster.Hubs)
            {
                var region = new Region(new RegionData { Id=hub.Region,Name=hub.Name,Description=hub.Name,Mobs=[] });
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText=$"SELECT ZoneID,Name,OffsetX,OffsetY,Width,Height FROM Zones WHERE RegionID={hub.Region}";
                    using var rows=cmd.ExecuteReader();
                    while(rows.Read())
                    {
                        ushort id=Convert.ToUInt16(rows[0]);
                        var z = new Zone(region,id,rows.GetString(1),Convert.ToInt32(rows[2])*8192,Convert.ToInt32(rows[3])*8192,
                            Convert.ToInt32(rows[4])*8192,Convert.ToInt32(rows[5])*8192,id,false,0,false,0,0,0,0,0) { IsPathfindingEnabled=true };
                        region.Zones.Add(z);
                        LocalPathfindingMgr.LoadNavMesh(z);
                    }
                }
                try
                {
                    var npcs = new List<(Vector3 Point, bool Hostile)>();
                    using (var cmd=db.CreateCommand())
                    {
                        cmd.CommandText=$"SELECT X,Y,Z,Realm,AggroLevel FROM Mob WHERE Region={hub.Region} AND (X-{hub.Center.X})*(X-{hub.Center.X})+(Y-{hub.Center.Y})*(Y-{hub.Center.Y})<3000*3000";
                        using var rows=cmd.ExecuteReader();
                        while(rows.Read()) npcs.Add((new(Convert.ToSingle(rows[0]),Convert.ToSingle(rows[1]),Convert.ToSingle(rows[2])),Convert.ToInt32(rows[3])==0 && Convert.ToInt32(rows[4])>0));
                    }
                    bool Safe(Vector3 p)=>!npcs.Any(n=>Vector3.DistanceSquared(p,n.Point)<(n.Hostile?750*750:100*100));
                    var nav=PathfindingProvider.LocalPathfindingMgr;
                    Zone zone=region.GetZone((int)hub.Center.X,(int)hub.Center.Y);
                    var posts=new List<Vector3>();
                    for(int slot=0;slot<RealmRaidRecruitmentPolicy.MaximumParties;slot++)
                    {
                        Assert.That(RealmRaidMuster.TryPost(nav,zone,hub,slot,posts,out var p,Safe),Is.True,$"{hub.Name}: safe post {slot}");
                        posts.Add(p);
                    }
                    var def=AutonomousRealmRaid.Definitions.Single(d=>d.Id==hub.Event);
                    Vector3 center;
                    if (def.IsDungeon) center=hub.Region switch {51=>new(318175,368845,5242),151=>new(398621,271246,8575),_=>new(394139,243486,4274)};
                    else { var h=DragonLairPlacement.Home(def.Realm); center=new(h.X,h.Y,h.Z); }
                    Zone end=region.GetZone((int)center.X,(int)center.Y);
                    var destinations=new List<Vector3>();
                    for(int slot=0;slot<RealmRaidRecruitmentPolicy.MaximumParties;slot++)
                    {
                        Vector3 p;
                        bool valid=def.IsDungeon
                            ? RealmRaidStaging.TryDungeonPost(nav,end,center,slot,destinations,out p)
                            : RealmRaidStaging.TryDragonPost(nav,end,center,slot,destinations,out p);
                        Assert.That(valid,Is.True,$"{def.Name}: final staging slot {slot}");
                        destinations.Add(p);
                    }
                    Assert.That(RealmRaidMuster.TryRoute(region,nav,def.Realm,posts[0],destinations[0],out var seams,hub.Via),Is.True,$"{hub.Name} -> {def.Name}");
                    // Exercise the same retained seam sequence used at runtime,
                    // for every party's own hub post and final staging post.
                    for(int slot=0;slot<RealmRaidRecruitmentPolicy.MaximumParties;slot++)
                    {
                        Vector3 start=posts[slot];
                        foreach(Vector3 goal in seams.Append(destinations[slot]))
                        {
                            Assert.That(AutonomousRvrRally.HasRoute(region,nav,def.Realm,start,goal),Is.True,$"{hub.Name}: party {slot} leg {start} -> {goal}");
                            start=goal;
                        }
                    }
                    TestContext.Progress.WriteLine($"{hub.Name}: {seams.Length} retained zone crossings");
                    TestContext.Progress.WriteLine($"{hub.Name}: all {posts.Count} safe formations and routes to {def.Name} validated");
                }
                finally { foreach(var zone in region.Zones) LocalPathfindingMgr.UnloadNavMesh(zone); }
            }
        }
        finally { Environment.CurrentDirectory=previous; }
    }

    [Test] public void StagingDoesNotHoldBotsForever()
    {
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(true, 89 * 60000L), Is.False);
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(true, 90 * 60000L), Is.True);
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(false, 60 * 60000L), Is.False);
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(false, 90 * 60000L), Is.True);
        Assert.That(RealmRaidRecruitmentPolicy.JoinExistingChance, Is.GreaterThan(.65));
        Assert.That(RealmRaidRecruitmentPolicy.MaximumBots, Is.EqualTo(300));
        Assert.That(RealmRaidRecruitmentPolicy.MaximumParties, Is.EqualTo(38));
        Assert.That(RealmRaidRecruitmentPolicy.BattleMilliseconds, Is.EqualTo(4*60*60000L));
    }

    [TestCase("wall")] [TestCase("island")] [TestCase("floor")]
    public void OpenStagingRejectsWallsDisconnectedPocketsAndWrongFloors(string problem)
    {
        var nav = new UT_RealmRaidFormation.Mesh { Visible = problem != "wall", Connected = problem != "island", HeightOffset = problem == "floor" ? 500 : 0 };
        Assert.That(RealmRaidStaging.HasOpenPartySpace(nav, UT_RealmRaidFormation.Zone(), new(20000, 20000, 1000)), Is.False);
    }

    [Test] public void OpenStagingChecksAllFourSides()
    {
        var nav = new UT_RealmRaidFormation.Mesh();
        Assert.That(RealmRaidStaging.HasOpenPartySpace(nav, UT_RealmRaidFormation.Zone(), new(20000, 20000, 1000)), Is.True);
        Assert.That(nav.Samples, Is.EqualTo(4));
    }

    [Test, Explicit("Read-only installed dragon navigation check"), NonParallelizable]
    public void ThirtyOpenConnectedStagingPostsForEveryDragon()
    {
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            foreach (var (realm, id, x, y) in new[] { (eRealm.Albion, 4, 43*8192, 85*8192), (eRealm.Midgard, 116, 84*8192, 118*8192), (eRealm.Hibernia, 216, 43*8192, 79*8192) })
            {
                var zone = new Zone(null, (ushort)id, "Raid staging probe", x, y, 65536, 65536, (ushort)id, false, 0, false, 0, 0, 0, 0, 0);
                LocalPathfindingMgr.LoadNavMesh(zone);
                try
                {
                    var h = DragonLairPlacement.Home(realm);
                    var posts = new List<Vector3>();
                    for (int slot = 0; slot < RealmRaidRecruitmentPolicy.MaximumParties; slot++)
                    {
                        bool valid = RealmRaidStaging.TryDragonPost(PathfindingProvider.LocalPathfindingMgr, zone, new(h.X, h.Y, h.Z), slot, posts, out var point);
                        TestContext.Progress.WriteLine($"{realm} slot={slot} valid={valid} point={point}");
                        Assert.That(valid, Is.True, $"{realm} party {slot}: no open connected staging post");
                        posts.Add(point);
                    }
                }
                finally { LocalPathfindingMgr.UnloadNavMesh(zone); }
            }
        }
        finally { Environment.CurrentDirectory = previous; }
    }
}
