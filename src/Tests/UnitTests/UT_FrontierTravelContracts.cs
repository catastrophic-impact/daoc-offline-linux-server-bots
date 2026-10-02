using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.Keeps;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable]
public class UT_FrontierTravelContracts
{
    [SetUp] public void ResetEvents() => RvrEventTestState.Clear();
    [TearDown] public void ClearEvents() => RvrEventTestState.Clear();
    [Test]
    public void DefenderQueueIsPrioritizedDeduplicatedAndDoesNotStarveOrdinaryTravel()
    {
        var queue=new FrontierBoardingQueue<object>();var ordinary=new object();var first=new object();
        queue.Enqueue(ordinary);queue.EnqueuePriority(first);queue.EnqueuePriority(first);
        for(int i=0;i<239;i++)queue.EnqueuePriority(new object());
        Assert.That(queue.Count,Is.EqualTo(241));
        Assert.That(queue.TryDequeue(out var next,out bool priority),Is.True);
        Assert.That(next,Is.SameAs(first));Assert.That(priority,Is.True);
        queue.TryDequeue(out _,out _);queue.TryDequeue(out _,out _);
        queue.TryDequeue(out next,out priority);
        Assert.That(next,Is.SameAs(ordinary));Assert.That(priority,Is.False);
        while(queue.TryDequeue(out _,out _)){}
        Assert.That(queue.Count,Is.Zero);
        queue.EnqueuePriority(first);Assert.That(queue.Count,Is.EqualTo(1),"Retry possible after a rejected or consumed request");
        queue.Clear();Assert.That(queue.Count,Is.Zero);
    }

    [TestCase(eRealm.Albion)]
    [TestCase(eRealm.Midgard)]
    [TestCase(eRealm.Hibernia)]
    public void PriorityRequiresActiveDefenderAssignmentAndCorrectDestination(eRealm realm)
    {
        var bot=(Traveler)RuntimeHelpers.GetUninitializedObject(typeof(Traveler));
        bot.Realm=realm;
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot,true);
        typeof(GameBot).GetProperty(nameof(GameBot.PersistentRecord)).SetValue(bot,new OfflineWorldBotRecord{ObjectiveKind="RvR"});
        typeof(GameLiving).GetField("<TempProperties>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bot,new PropertyCollection());
        ushort home=realm==eRealm.Albion?(ushort)1:realm==eRealm.Midgard?(ushort)100:(ushort)200;
        var passage=AutonomousFrontierTransport.Destination(realm,home);
        string force="defender-test";bot.TempProperties.SetProperty("RvrEventForce",force);
        var target=new AutonomousRvrEventLayer.LiveObjective("rvr-keep-test","Test keep",AutonomousRvrEventLayer.Intent.AssaultKeep,realm,home,0,0,0,false,10,0,1,1);
        long now=GameLoop.GameLoopTime;
        Assert.That(AutonomousFrontierTransport.HasDefenderPriority(bot,passage),Is.False);
        eRealm attacker=realm==eRealm.Albion?eRealm.Midgard:eRealm.Albion;
        Assert.That(AutonomousRvrEventLayer.BeginDefenseResponse(target,attacker,"test",now),Is.True);
        // Recruitment is separately covered by the defense-response suite.
        // Install the exact commitment that PulseDefense assigns, not the
        // ordinary autonomous ChooseOrJoin path (which excludes player alarms).
        var events=(System.Collections.IDictionary)typeof(AutonomousRvrEventLayer).GetField("Events",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        object active=events[target.Id];
        var defenders=(Dictionary<string,int>)active.GetType().GetField("Defenders").GetValue(active);
        defenders.Add(force,8);
        Assert.That(AutonomousFrontierTransport.HasDefenderPriority(bot,passage),Is.True);
        Assert.That(AutonomousFrontierTransport.PassageMatchesSiege(bot,passage),Is.True);
        Assert.That(AutonomousFrontierTransport.PassageMatchesSiege(bot,AutonomousFrontierTransport.Destination(realm,home==100?(ushort)200:(ushort)100)),Is.False,"An old medallion request must not send a defender away from its newly assigned home siege");
        Assert.That(AutonomousFrontierTransport.HasDefenderPriority(bot,AutonomousFrontierTransport.Destination(realm,home==100?(ushort)200:(ushort)100)),Is.False);
        bot.Group=new Group(bot);
        ((List<GameLiving>)typeof(Group).GetField("_groupMembers",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bot.Group)).AddRange([bot,(Traveler)RuntimeHelpers.GetUninitializedObject(typeof(Traveler))]);
        Assert.That(AutonomousFrontierTransport.BoardingParty(bot,passage),Is.EqualTo(new[]{bot}),"A missing warband member must not hold the defender");
        bot.PersistentRecord.ObjectiveKind="GroupPve";
        Assert.That(AutonomousFrontierTransport.HasDefenderPriority(bot,passage),Is.False,"PvE must not inherit priority");
        bot.PersistentRecord.ObjectiveKind="RvR";
        RvrEventTestState.Clear();
        Assert.That(AutonomousFrontierTransport.HasDefenderPriority(bot,passage),Is.False,"No stale priority after the siege ends");
    }

    [Test]
    public void WaitingOffsetsAreStableSpreadOutAndLeaveNpcClear()
    {
        var points = Enumerable.Range(1, 10000).Select(id => AutonomousFrontierTransport.WaitingOffset(id)).ToArray();
        Assert.That(points.Distinct().Count(), Is.EqualTo(10000));
        foreach (var point in points)
        {
            Assert.That(point.Length(), Is.InRange(219.9f, 410.1f));
            Assert.That(point.Length() + 40, Is.LessThan(AutonomousFrontierTransport.BoardingRadius));
        }
        Assert.That(AutonomousFrontierTransport.WaitingOffset(42), Is.EqualTo(AutonomousFrontierTransport.WaitingOffset(42)));
        Assert.That(AutonomousFrontierTransport.WaitingOffset(42,1), Is.Not.EqualTo(AutonomousFrontierTransport.WaitingOffset(42)));
    }

    [TestCase(0, 0, false)]
    [TestCase(7, 2.9, false)]
    [TestCase(8, 0, true)]
    [TestCase(1, 3, true)]
    [TestCase(1, 50, true)]
    public void BoardingYieldsAfterPassengerCapOrOneExpensiveTransfer(int count, double elapsed, bool expected)
    {
        Assert.That(AutonomousFrontierTransport.SliceFull(count, elapsed), Is.EqualTo(expected));
    }

    private static readonly IObjectDatabase Empty=DispatchProxy.Create<IObjectDatabase,DOL.UnitTests.UT_UnobservedConcentration.EmptyReads>();
    private sealed class Server:GameServer { protected override IObjectDatabase DataBaseImpl=>Empty; }
    private GameServer _previous;
    [SetUp] public void Setup() { _previous=GameServer.Instance;GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server))); }
    [TearDown] public void Cleanup() { GameServer.LoadTestDouble(_previous); }
    private sealed class Traveler:GameBot
    {
        private Traveler():base((OfflineWorldBotRecord)null){}
        public override eRealm Realm {get;set;}
        public override bool IsAlive=>true;
        public bool Fighting;
        public int PositionX;
        public override bool InCombat=>Fighting;
        public override bool IsAttacking=>false;
        public override int X=>PositionX;
        public override int Y=>0;
        public override int Z=>0;
        public override ushort CurrentRegionID {get=>1;set{}}
    }
    private sealed class Porter:DOL.GS.Scripts.OFTeleporter
    {
        public int WakeCount;
        public override void OnAutonomousBotNearby() { WakeCount++; }
        public override eRealm Realm {get;set;}
        public override int X=>0;
        public override int Y=>0;
        public override int Z=>0;
        public override ushort CurrentRegionID {get=>1;set{}}
    }
    [TestCase(eRealm.Albion)]
    [TestCase(eRealm.Midgard)]
    [TestCase(eRealm.Hibernia)]
    public void PorterRequiresTheRealTicketAndRejectsCombatWrongRealmAndAbsentPassengers(eRealm realm)
    {
        var bot=(Traveler)RuntimeHelpers.GetUninitializedObject(typeof(Traveler));
        bot.Realm=realm;bot.ObjectState=GameObject.eObjectState.Active;
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot,true);
        typeof(GameBot).GetProperty(nameof(GameBot.PersistentRecord)).SetValue(bot,new OfflineWorldBotRecord{ObjectiveKind="RvR"});
        typeof(GameLiving).GetField("<TempProperties>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bot,new PropertyCollection());
        typeof(GameNPC).GetField("m_brains",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bot,new System.Collections.ArrayList());
        bot.Inventory=new BotInventory();
        var porter=(Porter)RuntimeHelpers.GetUninitializedObject(typeof(Porter));porter.Realm=realm;
        var passage=AutonomousFrontierTransport.Destination(realm,realm==eRealm.Midgard?(ushort)200:(ushort)100);
        var request=new AutonomousFrontierTransport.Request(porter,passage,"rvr-0");
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,request),Is.False);
        Assert.That(AutonomousFrontierTransport.WakeBoardingPorter(bot,request),Is.False);
        var ticket=GameInventoryItem.Create(new DbItemTemplate{Id_nb=passage.Medallion,PackSize=1,MaxCount=1});
        Assert.That(bot.Inventory.AddItem(eInventorySlot.FirstBackpack,ticket),Is.True);
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,request),Is.True);
        Assert.That(AutonomousFrontierTransport.WakeBoardingPorter(bot,request),Is.True);
        Assert.That(porter.WakeCount,Is.EqualTo(1));
        bot.Fighting=true;
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,request),Is.False);
        bot.Fighting=false;bot.PositionX=1000;
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,request),Is.False);
        bot.PositionX=0;bot.Realm=realm==eRealm.Hibernia?eRealm.Albion:eRealm.Hibernia;
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,request),Is.False);
        Assert.That(ticket.Count,Is.EqualTo(1),"Rejected boarding must preserve the real ticket");
        bot.Realm=realm;bot.PersistentRecord.ObjectiveKind="SoloPve";
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,request),Is.False);
        ushort home=realm==eRealm.Albion?(ushort)1:realm==eRealm.Midgard?(ushort)100:(ushort)200;
        var homePassage=AutonomousFrontierTransport.Destination(realm,home);
        var homeRequest=new AutonomousFrontierTransport.Request(porter,homePassage,"rvr-0");
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,homeRequest),Is.False,"No free home passage");
        Assert.That(bot.Inventory.AddItem(eInventorySlot.FirstBackpack+1,
            GameInventoryItem.Create(new DbItemTemplate{Id_nb=homePassage.Medallion,PackSize=1,MaxCount=1})),Is.True);
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,homeRequest),Is.True);
        bot.Fighting=true;
        Assert.That(AutonomousFrontierTransport.Ready(bot,porter,homeRequest),Is.False,"Returners cannot escape active combat");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EventProtectionRequiresPhysicalAttendanceOrBattleCombatAndEndsWithEvent(bool relic)
    {
        var bot=(Traveler)RuntimeHelpers.GetUninitializedObject(typeof(Traveler));
        bot.Realm=eRealm.Albion;bot.ObjectState=GameObject.eObjectState.Active;
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot,true);
        typeof(GameBot).GetProperty(nameof(GameBot.PersistentRecord)).SetValue(bot,new OfflineWorldBotRecord{ObjectiveKind="RvR"});
        typeof(GameLiving).GetField("<TempProperties>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bot,new PropertyCollection());
        string id=Guid.NewGuid().ToString();long now=GameLoop.GameLoopTime;
        var target=new AutonomousRvrEventLayer.LiveObjective(id,"Watchdog test",
            relic?AutonomousRvrEventLayer.Intent.AssaultRelicKeep:AutonomousRvrEventLayer.Intent.AssaultKeep,
            eRealm.Midgard,1,0,0,0,relic,0,0,10,2);
        string force=id+":0";bot.TempProperties.SetProperty("RvrEventForce",force);
        int perRealm=relic?24:16;
        try
        {
            for(int i=0;i<perRealm*3;i++)
                AutonomousRvrEventLayer.ChooseOrJoin(new(id+":"+i,(eRealm)(i/perRealm+1),8,50,2),[target],now,0);
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now),Is.False,"Reservation is not arrival");
            AutonomousRvrEventLayer.ReportAttendance(id,force,bot.Realm,bot.DatabaseID,true,now,bot,new(0,0,0));
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now),Is.True);
            bot.PositionX=101;
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now),Is.False,"Leaving the post restores recovery");
            bot.PositionX=0;
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now+15001),Is.False,"Stale attendance is not protection");
            // Incidental combat must not bypass the physical muster requirements.
            AutonomousRvrEventLayer.ReportAttendance(id,force,bot.Realm,bot.DatabaseID,false,now);
            AutonomousRvrEventLayer.ChooseOrJoin(new(force,eRealm.Albion,8,50,2),[target with { UnderAttack=true }],now,0);
            Assert.That(AutonomousRvrEventLayer.IsBattleForce(force,now),Is.True,"Keep assaults now begin without a formation rally");
            // Exercise battle protection separately from recruitment/attendance policy.
            var events=(System.Collections.IDictionary)typeof(AutonomousRvrEventLayer)
                .GetField("Events",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            typeof(AutonomousRvrEventLayer).GetMethod("StartBattle",BindingFlags.NonPublic|BindingFlags.Static)
                .Invoke(null,new object[]{events[id],now,"test battle protection"});
            Assert.That(AutonomousRvrEventLayer.IsBattleForce(force,now),Is.True);
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now),Is.False,"Idle en-route participant is not fighting");
            var fighter=(Traveler)RuntimeHelpers.GetUninitializedObject(typeof(Traveler));
            fighter.Fighting=true;fighter.PositionX=100;
            bot.Group=new Group(bot);
            var members=(List<GameLiving>)typeof(Group).GetField("_groupMembers",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(bot.Group);
            members.Add(bot);members.Add(fighter);
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now),Is.True,"Nearby support shares its warband's battle");
            fighter.PositionX=3000;
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now),Is.False,"A distant fighter cannot shelter a stranded member");
            bot.Group=null;
            bot.Fighting=true;
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now+46*60000),Is.True);
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now+AutonomousRvrEventLayer.BattleLifetimeMilliseconds),Is.False);
            AutonomousRvrEventLayer.EndTarget(id,now+1);
            Assert.That(AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot,now+1),Is.False);
        }
        finally { AutonomousRvrEventLayer.EndTarget(id,now+1); }
    }
    [Test]
    public void FriendlyRvRDoorDoesNotGrantEnemyOrOrdinaryNpcAccess()
    {
        var bot=(Traveler)RuntimeHelpers.GetUninitializedObject(typeof(Traveler));
        bot.Realm=eRealm.Midgard;
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot,true);
        typeof(GameBot).GetProperty(nameof(GameBot.PersistentRecord)).SetValue(bot,new OfflineWorldBotRecord{ObjectiveKind="RvR"});
        var keep=new GameKeep {DBKeep=new DbKeep{Realm=2}};
        var door=new GameKeepDoor{Component=new GameKeepComponent{Keep=keep}};
        Assert.That(Pathfinder.CanUseFriendlyKeepDoor(bot,door),Is.True);
        bot.Realm=eRealm.Albion;
        Assert.That(Pathfinder.CanUseFriendlyKeepDoor(bot,door),Is.False);
        bot.Realm=eRealm.Midgard;
        bot.PersistentRecord.ObjectiveKind="SoloPve";
        Assert.That(Pathfinder.CanUseFriendlyKeepDoor(bot,door),Is.False);
        Assert.That(Pathfinder.CanUseFriendlyKeepDoor((GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC)),door),Is.False);
    }
    [Test]
    public void EveryRealmHasDistinctNativeArrivalsInBothEnemyFrontiers()
    {
        foreach(ushort region in new ushort[]{1,100,200})
        {
            eRealm owner=region==1?eRealm.Albion:region==100?eRealm.Midgard:eRealm.Hibernia;
            var arrivals=new[]{eRealm.Albion,eRealm.Midgard,eRealm.Hibernia}.Where(realm=>realm!=owner)
                .Select(realm=>AutonomousFrontierTransport.Destination(realm,region)).ToArray();
            Assert.That(arrivals.All(p=>p!=null && p.Region==region),Is.True);
            double dx=arrivals[0].Location.X-arrivals[1].Location.X,dy=arrivals[0].Location.Y-arrivals[1].Location.Y;
            Assert.That(Math.Sqrt(dx*dx+dy*dy),Is.GreaterThan(40000));
        }
    }
    [Test]
    public void TargetRotationVisitsEveryEligibleKeepBeforeRepeating()
    {
        var choices=Enumerable.Range(0,14).Select(i=>new AutonomousRvrEventLayer.LiveObjective(i.ToString(),"Keep"+i,
            AutonomousRvrEventLayer.Intent.AssaultKeep,eRealm.Midgard,100,0,0,0,false,0,0,i*100,2)).ToArray();
        var used=new HashSet<string>();var random=new Random(34);
        for(int i=0;i<choices.Length;i++)
        {
            var selected=AutonomousRvrEventLayer.ChooseVariedTarget(choices,used,random);
            Assert.That(used.Add(selected.Id),Is.True);
        }
        Assert.That(used.Count,Is.EqualTo(14));
        Assert.That(AutonomousRvrEventLayer.ChooseVariedTarget(choices,used,random),Is.Not.Null);
    }
}
