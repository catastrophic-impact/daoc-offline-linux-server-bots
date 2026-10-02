using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.IO;
using DOL.AI.Brain;
using DOL.AI;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.UnitTests;

[NonParallelizable]
public class UT_DragonEncounterAdds
{
    [Test]
    public void SixTotalAcrossHealthPhasesAndNeverReplacedAfterBeingKilled()
    {
        var budget = new DragonAddBudget();
        for(int i=0;i<6;i++)
        {
            long now=i*20_000L;
            Assert.That(budget.Due(now,86-i*15),Is.False);
            Assert.That(budget.Due(now,85-i*15),Is.True);
            budget.Commit(now);
            Assert.That(budget.Due(now+19_999,1),Is.False,"No burst if several phases are crossed at once");
        }
        Assert.That(budget.Spawned,Is.EqualTo(6));
        Assert.That(budget.Due(4*60*60_000L,1),Is.False,"Killing adds or a long fight never replenishes them");
        budget.Reset();
        Assert.That(budget.Spawned,Is.Zero);
        Assert.That(budget.Due(0,85),Is.True,"A genuinely new fight gets a fresh budget");
    }

    [Test]
    public void FailedAttemptDoesNotConsumeQuotaAndFightsHaveIndependentBudgets()
    {
        var a=new DragonAddBudget(); var b=new DragonAddBudget();
        for(int i=0;i<20;i++) Assert.That(a.Due(100_000,50),Is.True);
        Assert.That(a.Spawned,Is.Zero);
        a.Commit(100_000);
        Assert.That(a.Spawned,Is.EqualTo(1)); Assert.That(b.Spawned,Is.Zero);
    }

    [TestCase("wall")] [TestCase("island")] [TestCase("floor")]
    public void SpawnRejectsBadGeometry(string reason)
    {
        var nav=new UT_RealmRaidFormation.Mesh {Visible=reason!="wall",Connected=reason!="island",HeightOffset=reason=="floor"?500:0};
        Assert.That(DragonEncounterAdds.TryPosition(nav,UT_RealmRaidFormation.Zone(),new(10000,10000,1000),0,[],out _),Is.False);
        Assert.That(nav.Samples,Is.LessThanOrEqualTo(13),"Bounded work per target");
    }

    [Test]
    public void SixSpawnPointsDoNotStackAndStayCloseToTheirVictim()
    {
        var nav=new UT_RealmRaidFormation.Mesh(); var positions=new List<Vector3>();
        for(int slot=0;slot<6;slot++)
        {
            Assert.That(DragonEncounterAdds.TryPosition(nav,UT_RealmRaidFormation.Zone(),new(10000,10000,1000),slot,positions,out var point),Is.True);
            foreach(var other in positions) Assert.That(Vector3.Distance(point,other),Is.GreaterThanOrEqualTo(160));
            Assert.That(Vector3.Distance(point,new(10000,10000,1000)),Is.LessThan(500));
            positions.Add(point);
        }
    }

    private sealed class Bot : GameBot
    {
        private Bot() : base((OfflineWorldBotRecord)null) { }
        public override bool IsAlive=>true;
        public override byte Level {get=>50;set{}}
        public override eRealm Realm {get;set;}
        public override ushort CurrentRegionID {get=>1;set{}}
        public override int X=>0; public override int Y=>0; public override int Z=>0;
        public override ICharacterClass CharacterClass=>new ClassArmsman();
    }
    private sealed class Add : GameNPC
    {
        public override bool IsAlive=>true;
        public override byte Level {get=>64;set{}}
        public override int EffectiveLevel=>64;
        public override ushort CurrentRegionID {get=>1;set{}}
        public override int X=>0; public override int Y=>0; public override int Z=>0;
    }
    private static T Actor<T>() where T:GameNPC
    {
        var actor=(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        actor.ObjectState=GameObject.eObjectState.Active;
        typeof(GameNPC).GetField("m_brains",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(actor,new ArrayList());
        typeof(GameLiving).GetField("<TempProperties>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(actor,new PropertyCollection());
        actor.effectListComponent=EffectListComponent.Create(actor);
        return actor;
    }
    private static void Brain(GameNPC actor,ABrain brain)
    {
        brain.Body=actor;
        typeof(GameNPC).GetField("m_ownBrain",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(actor,brain);
    }

    [TestCase(typeof(GolestandtSpawnedAdBrain),eRealm.Albion)]
    [TestCase(typeof(GjalpinulvaSpawnedAdBrain),eRealm.Midgard)]
    [TestCase(typeof(CuuldurachSpawnedAdBrain),eRealm.Hibernia)]
    public void EveryRealmAddCanTargetBotsAndBotsCanRetaliate(Type brainType,eRealm realm)
    {
        using var server=new EpicTestServerScope();
        var bot=Actor<Bot>(); bot.Realm=realm;
        var add=Actor<Add>();
        var addBrain=(StandardMobBrain)Activator.CreateInstance(brainType); Brain(add,addBrain);
        var botBrain=new BotBrain(); Brain(bot,botBrain);
        try
        {
            Assert.That(addBrain.CanAggroTarget(bot),Is.True);
            Assert.That(addBrain.CanBaf,Is.False,"An approved add cannot recruit another unbounded wave");
            addBrain.CanBaf=true; Assert.That(addBrain.CanBaf,Is.False);
            Assert.That(GameServer.ServerRules.IsAllowedToAttack(bot,add,true),Is.True);
            addBrain.AddToAggroList(bot,100);
            Assert.That(addBrain.HasAggro,Is.True);
            botBrain.OnAttackedByEnemy(new AttackData {Attacker=add,Target=bot,Damage=1,AttackResult=eAttackResult.HitUnstyled});
            Assert.That(botBrain.GetBaseAggroAmount(add),Is.GreaterThan(0));
            Assert.That((bool)typeof(BotBrain).GetMethod("ShouldBeRemovedFromAggroList",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(botBrain,new object[]{add}),Is.False);
            add.Flags=GameNPC.eFlags.PEACE;
            Assert.That(addBrain.CanAggroTarget(bot),Is.False,"Keep the normal safety and attack-legality rules");
        }
        finally {ServiceObjectStore.Remove(bot.effectListComponent);ServiceObjectStore.Remove(add.effectListComponent);}
    }

    [Test,Explicit("Read-only installed navmesh probe for six reachable add positions at each dragon" )]
    public void InstalledLairsHaveSixReachableAddPositions()
    {
        string previous=Environment.CurrentDirectory;
        try
        {
            string root=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(Path.Combine(root,"lib","Detour.dll")) : IntPtr.Zero);
            Environment.CurrentDirectory=root;
            foreach(var (realm,id,x,y) in new[]{(eRealm.Albion,4,43*8192,85*8192),(eRealm.Midgard,116,84*8192,118*8192),(eRealm.Hibernia,216,43*8192,79*8192)})
            {
                var zone=new Zone(null,(ushort)id,"Dragon add probe",x,y,65536,65536,(ushort)id,false,0,false,0,0,0,0,0);
                LocalPathfindingMgr.LoadNavMesh(zone);
                try
                {
                    var home=DragonLairPlacement.Home(realm);
                    var positions=new List<Vector3>();
                    for(int slot=0;slot<6;slot++)
                    {
                        Assert.That(DragonEncounterAdds.TryPosition(PathfindingProvider.LocalPathfindingMgr,zone,new(home.X,home.Y,home.Z),slot,positions,out var point),Is.True,$"{realm} add {slot+1}");
                        positions.Add(point);
                        TestContext.Progress.WriteLine($"{realm} add {slot+1}: connected floor {point}");
                    }
                }
                finally {LocalPathfindingMgr.UnloadNavMesh(zone);}
            }
        }
        finally {Environment.CurrentDirectory=previous;}
    }
}
