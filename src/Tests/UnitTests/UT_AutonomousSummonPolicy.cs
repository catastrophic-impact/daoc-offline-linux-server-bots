using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_AutonomousSummonPolicy
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<GameLiving> _actors = new();
    private GameServer _previous;
    private PetTestLanguageScope _language;
    private long _time;
    private sealed class Rules : NormalServerRules
    {
        public override bool IsAllowedToAttack(GameLiving a, GameLiving b, bool quiet) =>
            b != null && b.IsAlive && b.ObjectState == GameObject.eObjectState.Active && a.Realm != b.Realm;
    }
    private sealed class Server : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        protected override IServerRules ServerRulesImpl => new Rules();
    }
    private sealed class Bot : GameBot
    {
        private Bot() : base((OfflineWorldBotRecord)null) { }
        public ICharacterClass Class;
        public IControlledBrain TestPet;
        public bool Moving, Casting;
        public override ICharacterClass CharacterClass => Class;
        public override bool IsAlive => true;
        public override bool IsMoving => Moving;
        public override bool IsCasting => Casting;
        public override bool IsCrowdControlled => false;
        public override bool IsOnHorse => false;
        public override byte Level => 20;
        public override int EffectiveLevel => 20;
        public override eRealm Realm { get => eRealm.Hibernia; set { } }
        public override GameObject TargetObject { get; set; }
        public override IControlledBrain ControlledBrain { get => TestPet; set => TestPet=value; }
        public override bool RemoveControlledBrain(IControlledBrain brain) { if(TestPet!=brain)return false;TestPet=null;return true; }
    }
    private sealed class Pet : GameSummonedPet
    {
        private Pet() : base((INpcTemplate)null) { }
        public bool Alive = true, Visible;
        public int Handshakes;
        public override bool IsAlive => Alive;
        public override bool IsVisibleToPlayersOrBots => Visible;
        public override void OnUpdateOrCreateForPlayer() { Handshakes++; }
    }
    private sealed class CharmPlayer : GamePlayer
    {
        private CharmPlayer() : base(null, null) { }
        public override IControlledBrain ControlledBrain { get; set; }
        public override bool AddControlledBrain(IControlledBrain brain)
        {
            ControlledBrain = brain;
            return true;
        }
    }

    [Test]
    public void PlayerCharmRegistersRealPlayerOwnershipBeforePendingCleanup()
    {
        var player = Actor<CharmPlayer>();
        var pet = Actor<CharmedNpc>();
        var brain = new ControlledMobBrain(player) { Body = pet };
        Assert.That(player, Is.Not.InstanceOf<IGamePlayer>(),
            "The bot-only interface was the original reason player charms were deleted");
        var register = typeof(CharmECSGameEffect).GetMethod("RegisterOwner",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(register.Invoke(null, new object[] { player, brain }), Is.True);
        Assert.That(player.ControlledBrain.Body, Is.SameAs(pet),
            "The pending-candidate cleanup must recognize the successfully registered pet");
    }
    private sealed class FieldPet : TurretFnfPet
    {
        private FieldPet() : base((INpcTemplate)null) { }
        public override bool IsAlive => true;
        public override eRealm Realm { get => eRealm.Hibernia; set { } }
    }
    private sealed class Enemy : GameNPC
    {
        public byte TestLevel;
        public override bool IsAlive => true;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
        public override int EffectiveLevel => TestLevel;
        public override eRealm Realm { get; set; }
        public override bool IsVisibleToPlayersOrBots => false;
    }
    private sealed class CharmedNpc : GameNPC
    {
        public int Handshakes;
        public override bool IsAlive=>true;
        public override bool IsVisibleToPlayersOrBots=>false;
        public override void OnUpdateOrCreateForPlayer(){Handshakes++;}
    }
    private sealed class FnfBrain : TurretFNFBrain
    {
        public FnfBrain(GameLiving owner) : base(owner) { }
        public bool Eligible(GameLiving target) => CanConsiderProximityTarget(target);
    }
    private sealed class MainTurret : TurretPet
    {
        private MainTurret():base((INpcTemplate)null){}
        public int TestX;
        public override int X=>TestX;
        public override int Y=>0;
        public override int Z=>0;
        public override bool IsAlive=>true;
    }
    private sealed class MainBrain : TurretMainPetCasterBrain
    {
        public int Releases;
        public MainBrain(GameLiving owner):base(owner){}
        public override void OnRelease(){Releases++;}
    }
    [SetUp] public void Setup()
    {
        _previous = GameServer.Instance;
        _language = new PetTestLanguageScope();
        GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        _time = GameLoop.GameLoopTime;
        typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, 120000L);
    }
    [TearDown] public void Cleanup()
    {
        foreach (GameLiving actor in _actors)
        {
            ServiceObjectStore.Remove(actor.effectListComponent);
            if (actor is GameNPC npc && npc.Brain != null) ServiceObjectStore.Remove(npc.Brain);
        }
        typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, _time);
        GameServer.LoadTestDouble(_previous);
        _language.Dispose();
    }
    private T Actor<T>() where T : GameLiving
    {
        var actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        actor.ObjectState = GameObject.eObjectState.Active;
        actor.effectListComponent = EffectListComponent.Create(actor);
        typeof(GameLiving).GetField("<TempProperties>k__BackingField", Hidden).SetValue(actor, new PropertyCollection());
        if (actor is GameNPC npc)
        {
            typeof(GameNPC).GetField("m_brains", Hidden).SetValue(npc, new ArrayList());
            npc.movementComponent = new NpcMovementComponent(npc);
        }
        if (actor is Pet pet) pet.Alive = true;
        _actors.Add(actor);
        return actor;
    }
    private Bot Owner(bool autonomous = true)
    {
        Bot bot = Actor<Bot>(); bot.Class = new ClassAnimist();
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot, autonomous);
        return bot;
    }
    private static void Attach(GameNPC pet, GameLiving owner) => typeof(GameNPC).GetField("m_ownBrain", Hidden)
        .SetValue(pet, new ControlledMobBrain(owner) { Body = pet });

    [TestCase(0,1500)] [TestCase(100,750)] [TestCase(1500,1500)] [TestCase(9000,2500)]
    public void DeploymentRangeUsesNativeRangeWithBounds(int range, int expected) =>
        Assert.That(BotAnimistPolicy.UsefulRadius(range), Is.EqualTo(expected));

    // The old 35% power / 6.5 s / three-shroom field cap is replaced by
    // AnimistShroomPlanner (see UT_AnimistShroomPlanner): a 20% reserve and
    // the server's own turret caps.
    [TestCase(100,100,10,true)] [TestCase(30,100,10,true)] [TestCase(29,100,10,false)] [TestCase(100,0,10,false)]
    public void FieldPowerKeepsReserve(int mana, int maxMana, int cost, bool expected) =>
        Assert.That(AnimistShroomPlanner.KeepsReserve(mana,maxMana,cost), Is.EqualTo(expected));

    [Test] public void AllLearnedAnimistSummonKindsBypassOnlyTheOldBotFilter()
    {
        var bot = Owner();
        foreach (eSpellType type in new[] {eSpellType.SummonAnimistPet,eSpellType.SummonAnimistFnF,eSpellType.SummonAnimistFnFCustom})
        {
            var spell = new Spell(new DbSpell { Type=type.ToString(), Radius=350, Target="Self" }, 20);
            Assert.That(AnimistSingleTargetPolicy.AllowsAutomatedSpell(bot,spell), Is.True);
            Assert.That(AnimistSingleTargetPolicy.AllowsAutomatedSpell(spell), Is.False, "Legacy player-autopilot filter unchanged");
        }
        bot.Class = new ClassCabalist();
        Assert.That(BotAnimistPolicy.AppliesTo(bot), Is.False);
        Assert.That(AnimistSingleTargetPolicy.AppliesTo(bot), Is.True);
    }

    [TestCase("moving")] [TestCase("stable")] [TestCase("return")] [TestCase("sitting")]
    public void TravelAndRestNeverPlantEvenWhenTargetIsHostile(string mode)
    {
        var bot=Owner(); var enemy=Actor<Enemy>(); enemy.Realm=eRealm.None; enemy.TestLevel=20;
        if(mode=="moving") bot.Moving=true;
        if(mode=="sitting") typeof(GameBot).GetField("_recoveryRestLocked", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bot, true);
        if(mode=="stable") typeof(GameBot).GetProperty(nameof(GameBot.IsOnStableMasterRoute)).SetValue(bot,true);
        if(mode=="return") typeof(GameBot).GetProperty(nameof(GameBot.IsReturningAfterRelease)).SetValue(bot,true);
        long next=0;
        Assert.That(BotAnimistPolicy.Maintain(bot,enemy,ref next,out _), Is.False);
        Assert.That(next, Is.Zero);
    }

    [TestCase(false)] [TestCase(true)]
    public void GroundTargetRestoresAfterCastingButNeverOverwritesNewTarget(bool newTarget)
    {
        var bot=Owner(); var enemy=Actor<Enemy>(); enemy.Realm=eRealm.None; enemy.TestLevel=20;
        bot.TargetObject=bot; bot.Casting=true;
        typeof(BotAnimistPolicy).GetMethod("FinishSummonTarget", BindingFlags.Static|BindingFlags.NonPublic)
            .Invoke(null,new object[]{bot,enemy,enemy,true});
        BotAnimistPolicy.RestoreEncounterTarget(bot);
        Assert.That(bot.TargetObject,Is.SameAs(bot));
        GameObject newer=Actor<Enemy>();
        if(newTarget) bot.TargetObject=newer;
        bot.Casting=false;
        BotAnimistPolicy.RestoreEncounterTarget(bot);
        Assert.That(bot.TargetObject,Is.SameAs(newTarget?newer:enemy));
    }

    [Test] public void OutOfCombatSummonAlsoClearsItsSelfTarget()
    {
        var bot=Owner(); bot.TargetObject=bot; bot.Casting=true;
        typeof(BotAnimistPolicy).GetMethod("FinishSummonTarget", BindingFlags.Static|BindingFlags.NonPublic)
            .Invoke(null,new object[]{bot,null,null,true});
        bot.Casting=false;
        BotAnimistPolicy.RestoreEncounterTarget(bot);
        Assert.That(bot.TargetObject,Is.Null);
    }

    [Test] public void StaleMainRelocatesOnlyAfterTravelWithLegalEncounterAndCooldown()
    {
        var bot=Owner(); var main=Actor<MainTurret>(); main.TestX=5000;
        var brain=new MainBrain(bot){Body=main,IsMainPet=true}; bot.TestPet=brain;
        var enemy=Actor<Enemy>(); enemy.Realm=eRealm.None; enemy.TestLevel=20;
        long next=long.MaxValue;
        bot.Moving=true;
        BotAnimistPolicy.Maintain(bot,enemy,ref next,out _);
        Assert.That(brain.Releases,Is.Zero);
        bot.Moving=false;
        BotAnimistPolicy.Maintain(bot,null,ref next,out _);
        Assert.That(brain.Releases,Is.Zero,"A distant main alone is not an encounter");
        BotAnimistPolicy.Maintain(bot,enemy,ref next,out _);
        Assert.That(brain.Releases,Is.EqualTo(1));
        bot.TestPet=brain; // Simulate delayed removal so the cooldown must protect it.
        BotAnimistPolicy.Maintain(bot,enemy,ref next,out _);
        Assert.That(brain.Releases,Is.EqualTo(1));
        typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null,GameLoop.GameLoopTime+BotAnimistPolicy.RelocationCooldownMilliseconds+1);
        main.TestX=0;
        BotAnimistPolicy.Maintain(bot,enemy,ref next,out _);
        Assert.That(brain.Releases,Is.EqualTo(1),"Useful local main remains deployed");
        main.TestX=5000;
        BotAnimistPolicy.Maintain(bot,enemy,ref next,out _);
        Assert.That(brain.Releases,Is.EqualTo(2));
    }

    [Test] public void FnfAllowsOnlyLegalNonGreyAggroWithoutAnOrder()
    {
        Bot bot=Owner(); var pet=Actor<FieldPet>();
        var brain=new FnfBrain(bot){Body=pet,AggroLevel=100};
        var enemy=Actor<Enemy>(); enemy.Realm=eRealm.None; enemy.TestLevel=20;
        Assert.That(brain.Eligible(enemy),Is.True);
        enemy.TestLevel=1;
        Assert.That(brain.Eligible(enemy),Is.False);
        enemy.TestLevel=20; enemy.Realm=eRealm.Hibernia;
        Assert.That(brain.Eligible(enemy),Is.False);
        enemy.Realm=eRealm.None; brain.AggroLevel=0;
        Assert.That(brain.Eligible(enemy),Is.False);
        Assert.That(brain.OrderedAttackTarget,Is.Null);
    }

    [Test] public void OffscreenOwnerResolutionSupportsNestedBonedancerMinionsOnlyForAutonomousBots()
    {
        var bot=Owner(); var commander=Actor<Pet>(); var minion=Actor<Pet>();
        Attach(commander,bot); Attach(minion,commander);
        Assert.That(AutonomousSummonActivity.AutonomousOwner(minion),Is.SameAs(bot));
        Assert.That(minion.Brain.IsActive,Is.True);
        Assert.That(AutonomousSummonActivity.ActivateOnce(minion),Is.True);
        Assert.That(AutonomousSummonActivity.ActivateOnce(minion),Is.False);
        Assert.That(minion.Handshakes,Is.EqualTo(1));
        minion.ObjectState=GameObject.eObjectState.Deleted;
        Assert.That(minion.Brain.IsActive,Is.False);
        Assert.That(AutonomousSummonActivity.ActivateOnce(minion),Is.False);
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot,false);
        Assert.That(commander.Brain.IsActive,Is.False);
        commander.Visible=true;
        Assert.That(commander.Brain.IsActive,Is.True,"Normal visibility still applies");
    }

    [Test] public void StartupOrderingGetsOneHandshakeAndDeadOrRemovedSummonsNeverGetOne()
    {
        var bot=Owner(false); var pet=Actor<Pet>(); Attach(pet,bot);
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.False);
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot,true);
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.True);
        var dead=Actor<Pet>(); Attach(dead,bot); dead.Alive=false;
        Assert.That(AutonomousSummonActivity.ActivateOnce(dead),Is.False);
        dead.Alive=true; dead.ObjectState=GameObject.eObjectState.Inactive;
        Assert.That(AutonomousSummonActivity.ActivateOnce(dead),Is.False);
        var npc=Actor<Enemy>(); Attach(npc,bot);
        Assert.That(npc.Brain.IsActive,Is.False,"Ordinary controlled NPC is not a summoned-pet exception");
    }

    [Test] public void SuccessfulInitialWorldAddCanHandshakeBeforeNativeHealthAssignment()
    {
        var bot=Owner(); var pet=Actor<Pet>(); Attach(pet,bot); pet.Alive=false;
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.False);
        var add=typeof(AutonomousSummonActivity).GetMethod("ActivateAfterWorldAdd",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.That(add.Invoke(null,new object[]{pet}),Is.True);
        Assert.That(pet.Handshakes,Is.EqualTo(1));
        pet.Alive=true;
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.False);
        pet.ObjectState=GameObject.eObjectState.Deleted;
        Assert.That(add.Invoke(null,new object[]{pet}),Is.False);
    }

    [Test] public void RealPlayerPetKeepsOriginalVisibilityRules()
    {
        var player=Actor<GamePlayer>(); var pet=Actor<Pet>(); Attach(pet,player);
        Assert.That(AutonomousSummonActivity.AutonomousOwner(pet),Is.Null);
        Assert.That(pet.Brain.IsActive,Is.False);
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.False);
        pet.Visible=true;
        Assert.That(pet.Brain.IsActive,Is.True);
    }

    [TestCase(typeof(ClassSorcerer))] [TestCase(typeof(ClassMentalist))]
    [TestCase(typeof(ClassMinstrel))] [TestCase(typeof(ClassHunter))]
    public void RegisteredCharmsStayActiveButUnrelatedOrReleasedCreaturesDoNot(Type type)
    {
        var bot=Owner(); bot.Class=(ICharacterClass)Activator.CreateInstance(type);
        var pet=Actor<CharmedNpc>(); Attach(pet,bot);
        Assert.That(pet.Brain.IsActive,Is.False,"Not yet registered as this bot's pet");
        bot.TestPet=(IControlledBrain)pet.Brain;
        Assert.That(pet.Brain.IsActive,Is.True);
        AutonomousSummonActivity.RecoverOwner(bot);
        AutonomousSummonActivity.RecoverOwner(bot);
        Assert.That(pet.Handshakes,Is.EqualTo(1));
        bot.TestPet=null;
        Assert.That(AutonomousSummonActivity.AutonomousOwner(pet),Is.Null);
        Assert.That(pet.Brain.IsActive,Is.False);
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.False);
        Attach(pet,bot); // A later legitimate charm gets a new native brain.
        bot.TestPet=(IControlledBrain)pet.Brain;
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.True);
        Assert.That(AutonomousSummonActivity.ActivateOnce(pet),Is.False);
        Assert.That(pet.Handshakes,Is.EqualTo(2));
    }
}
