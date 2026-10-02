using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_DragonRallyRoute
    {
        private sealed class Attendee : GameBot
        {
            private Attendee():base((OfflineWorldBotRecord)null){}
            public Vector3 Position;
            public bool Alive=true;
            public ushort RegionId=100;
            public override byte Level {get=>50;set{}}
            public override bool IsAlive=>Alive;
            public override int X=>(int)Position.X;
            public override int Y=>(int)Position.Y;
            public override int Z=>(int)Position.Z;
            public override ushort CurrentRegionID {get=>RegionId;set=>RegionId=value;}
        }

        [Test,NonParallelizable]
        public void ProductionAttendanceCountsOnlyPresentIndividualsAndProtectsTheirWait()
        {
            using var server=new EpicTestServerScope();
            const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            Type manager=typeof(AutonomousRealmRaid);
            object raid=Activator.CreateInstance(manager.GetNestedType("Raid",BindingFlags.NonPublic),true);
            object party=Activator.CreateInstance(manager.GetNestedType("Party",BindingFlags.NonPublic),true);
            var home=DragonLairPlacement.Home(eRealm.Midgard);
            Vector3 own=new(home.X+4000,home.Y,home.Z),other=new(home.X-4000,home.Y,home.Z);
            var members=Enumerable.Range(0,8).Select(_=>
            {
                var bot=(Attendee)RuntimeHelpers.GetUninitializedObject(typeof(Attendee));
                bot.Alive=true;bot.RegionId=100;bot.Position=other;
                typeof(GameNPC).GetField("m_brains",hidden).SetValue(bot,new ArrayList());
                typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot,true);
                return bot;
            }).ToArray();
            var group=new Group(members[0]);
            typeof(Group).GetField("_groupMembers",hidden).SetValue(group,members.Cast<GameLiving>().ToList());
            foreach(var member in members)member.Group=group;
            raid.GetType().GetField("Definition").SetValue(raid,AutonomousRealmRaid.Definitions.Single(d=>d.Id=="dragon-midgard"));
            raid.GetType().GetField("HubDeparted").SetValue(raid,true);
            var boss=(GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
            raid.GetType().GetField("Boss").SetValue(raid,boss);
            var posts=(Dictionary<int,Vector3>)raid.GetType().GetField("StagingPosts").GetValue(raid);
            posts.Add(0,own);posts.Add(1,other);
            party.GetType().GetField("Members").SetValue(party,members.Cast<GameBot>().ToArray());
            party.GetType().GetField("Staging").SetValue(party,own);
            ((IDictionary)raid.GetType().GetField("Parties").GetValue(raid)).Add(group,party);
            var membership=(IDictionary)manager.GetField("Membership",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            membership.Add(group,raid);
            try
            {
                members[0].Position=new(home.X,home.Y,home.Z);
                members[1].Alive=false;
                members[2].RegionId=1;
                members[3].Position=other+new Vector3(0,1000,0);
                typeof(GameBot).GetProperty(nameof(GameBot.IsOnStableMasterRoute)).SetValue(members[4],true);
                int present=(int)manager.GetMethod("PresentAtStaging",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{raid});
                Assert.That(present,Is.EqualTo(3),"Three arrivals count without the other five; even at another party's post.");
                Assert.That(AutonomousRealmRaid.Protects(members[5]),Is.True);
                Assert.That(AutonomousRealmRaid.IsPendingDragonTarget(members[5],boss),Is.True);
                Assert.That(AutonomousRealmRaid.IsPendingDragonTarget(members[5],members[0]),Is.False,"Other targets must not be filtered.");
                raid.GetType().GetField("Started").SetValue(raid,true);
                Assert.That(AutonomousRealmRaid.IsPendingDragonTarget(members[5],boss),Is.False,"Battle order releases the dragon pull.");
            }
            finally{membership.Remove(group);}
            Assert.That(AutonomousRealmRaid.IsPendingDragonTarget(members[5],boss),Is.False,"Normal PvE has no expedition gate.");
        }

        [Test]
        public void RouteGoesAroundDragonInsteadOfCrossingIt()
        {
            Vector3 home=new(20000,20000,1000), start=home+new Vector3(-6000,0,0), goal=home+new Vector3(6000,0,0);
            Assert.That(DragonRallyRoute.TryBuild(new UT_RealmRaidFormation.Mesh(),UT_RealmRaidFormation.Zone(),start,goal,home,out var route),Is.True);
            Assert.That(route.Length,Is.GreaterThan(1));
            foreach(var step in route)
            {
                Assert.That(DragonRallyRoute.SegmentDistanceSquared(start,step,home),Is.GreaterThanOrEqualTo(DragonRallyRoute.Clearance*DragonRallyRoute.Clearance));
                start=step;
            }
            Assert.That(start,Is.EqualTo(goal));
        }

        [Test]
        public void PartialPartyMemberAtAnotherExpeditionPostCountsButBossPileDoesNot()
        {
            Vector3 home=new(20000,20000,1000);
            Vector3[] posts=[home+new Vector3(4000,0,0),home+new Vector3(-4000,0,0)];
            Assert.That(DragonRallyRoute.AtAssembly(posts[1]+new Vector3(500,0,0),posts,home),Is.True);
            Assert.That(DragonRallyRoute.AtAssembly(home,posts,home),Is.False);
            Assert.That(DragonRallyRoute.AtAssembly(posts[1]+new Vector3(0,700,0),posts,home),Is.False);
        }

        [Test]
        public void ADisplacedBotCanLeaveTheMarginButCannotCutDeeperIntoIt()
        {
            Assert.That(DragonRallyRoute.SafeSegment(new(1000,0,0),new(4000,0,0),Vector3.Zero),Is.True);
            Assert.That(DragonRallyRoute.SafeSegment(new(1000,0,0),new(-4000,0,0),Vector3.Zero),Is.False);
            Assert.That(DragonRallyRoute.TryBuild(new UT_RealmRaidFormation.Mesh(),UT_RealmRaidFormation.Zone(),new(1000,0,0),new(4000,0,0),Vector3.Zero,out _),Is.True);
        }

        [Test]
        public void DisconnectedRoutesAndUnsafeDestinationsAreNotAccepted()
        {
            Assert.That(DragonRallyRoute.TryBuild(new UT_RealmRaidFormation.Mesh{Connected=false},UT_RealmRaidFormation.Zone(),new(6000,0,0),new(-6000,0,0),Vector3.Zero,out _),Is.False);
            Assert.That(DragonRallyRoute.TryBuild(new UT_RealmRaidFormation.Mesh(),UT_RealmRaidFormation.Zone(),new(6000,0,0),Vector3.Zero,Vector3.Zero,out _),Is.False);
        }

        [Test,Explicit("Read-only installed dragon meshes"),NonParallelizable]
        public void AllDragonStagingPostsHaveBossAvoidingApproaches()
        {
            string prior=Environment.CurrentDirectory;
            try
            {
                string native=Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name,assembly,search)=>name=="lib/Detour"?System.Runtime.InteropServices.NativeLibrary.Load(native):IntPtr.Zero);
                Environment.CurrentDirectory=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
                foreach(var (realm,id,x,y) in new[]{(eRealm.Albion,4,43*8192,85*8192),(eRealm.Midgard,116,84*8192,118*8192),(eRealm.Hibernia,216,43*8192,79*8192)})
                {
                    var zone=new Zone(null,(ushort)id,"Dragon rally check",x,y,65536,65536,(ushort)id,false,0,false,0,0,0,0,0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    try
                    {
                        var h=DragonLairPlacement.Home(realm);Vector3 home=new(h.X,h.Y,h.Z);
                        var posts=new List<Vector3>();var nav=PathfindingProvider.LocalPathfindingMgr;
                        for(int slot=0;slot<RealmRaidRecruitmentPolicy.MaximumParties;slot++)
                        {
                            Assert.That(RealmRaidStaging.TryDragonPost(nav,zone,home,slot,posts,out var p),Is.True,$"{realm} staging {slot}");
                            posts.Add(p);
                        }
                        for(int slot=0;slot<posts.Count;slot++)
                        {
                            // Cross-lair starts reproduce the unsafe shortcuts.
                            Vector3 start=posts[(slot+19)%posts.Count];
                            var work=new RvrPlanningNavigation(nav);Vector3[] route=null;bool found=false;
                            for(int slice=0;slice<1000 && route==null;slice++)
                            {
                                work.BeginSlice();
                                try{found=DragonRallyRoute.TryBuild(work,zone,start,posts[slot],home,out var answer);route=answer;}
                                catch(RvrPlanningNavigation.Yield){}
                                finally{work.EndSlice();}
                            }
                            Assert.That(found,Is.True,$"{realm} safe approach {slot} from={start} to={posts[slot]} queries={work.Queries}");
                            TestContext.Progress.WriteLine($"{realm} post={slot} safeWaypoints={route.Length} queries={work.Queries}");
                        }
                        int entrances=0;
                        for(int direction=0;direction<12;direction++)
                        {
                            double angle=direction*Math.PI/6;
                            Vector3 raw=home+new Vector3((float)Math.Cos(angle)*12500,(float)Math.Sin(angle)*12500,0);
                            var entry=nav.GetClosestPoint(zone,raw,256,256,4096,nav.DefaultFilters);
                            if(!entry.HasValue || !AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,entry.Value,posts[0])) continue;
                            entrances++;
                            for(int slot=0;slot<posts.Count;slot++)
                            {
                                var work=new RvrPlanningNavigation(nav);bool done=false,found=false;
                                for(int slice=0;slice<1000 && !done;slice++)
                                {
                                    work.BeginSlice();
                                    try{found=DragonRallyRoute.TryBuild(work,zone,entry.Value,posts[slot],home,out _);done=true;}
                                    catch(RvrPlanningNavigation.Yield){}
                                    finally{work.EndSlice();}
                                }
                                Assert.That(found,Is.True,$"{realm} incoming direction={direction} slot={slot} start={entry} queries={work.Queries}");
                            }
                        }
                        Assert.That(entrances,Is.GreaterThan(0),$"{realm} needs at least one checked incoming approach");
                        TestContext.Progress.WriteLine($"{realm} incomingApproaches={entrances} toAllPosts={entrances*posts.Count}");
                    }
                    finally{LocalPathfindingMgr.UnloadNavMesh(zone);}
                }
            }
            finally{Environment.CurrentDirectory=prior;}
        }
    }
}
