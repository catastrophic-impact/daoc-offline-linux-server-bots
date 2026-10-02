using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.PropertyCalc;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public sealed class UT_BonedancerSupportCastLifecycle
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly SpellLine Line = new("Bone Army", "Bone Army", "Bone Army", true);
    private readonly List<GameLiving> _actors = [];
    private GameServer _oldServer;
    private long _oldTime;
    private Region _region;
    private PetTestLanguageScope _language;

    private sealed class EmptyServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl =>
            DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        protected override GS.ServerRules.IServerRules ServerRulesImpl => new GS.ServerRules.NormalServerRules();
    }

    private abstract class TestNpc : GameNPC
    {
        public byte TestLevel = 19;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
        public override bool IsAlive => true;
        public override bool IsCrowdControlled => false;
        public override bool IsBeingInterrupted => false;
        public override int Health { get; set; } = 100;
        public override int Mana { get; set; } = 100;
        public override int Endurance { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int MaxMana => 100;
        public override int MaxEndurance => 100;
        public override int GetModified(eProperty property) => property == eProperty.SpellRange ? 100 : 100;
    }

    private sealed class RootBot : GameBot
    {
        private RootBot() : base((OfflineWorldBotRecord)null) { }
        public byte TestLevel = 19;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
        public override bool IsAlive => true;
        public override bool IsCrowdControlled => false;
        public override bool IsBeingInterrupted => false;
        public override int Health { get; set; } = 100;
        public override int Mana { get; set; } = 100;
        public override int Endurance { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int MaxMana => 100;
        public override int MaxEndurance => 100;
        public override int GetModified(eProperty property) => property == eProperty.SpellRange ? 100 : 100;
    }

    private sealed class Player : GamePlayer
    {
        private Player() : base(null, null) { }
        public override bool IsAlive => true;
        public override bool IsCrowdControlled => false;
        public override bool IsBeingInterrupted => false;
        public override int Health { get; set; } = 100;
        public override int Mana { get; set; } = 100;
        public override int Endurance { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int MaxMana => 100;
        public override int MaxEndurance => 100;
        public override int GetModified(eProperty property) => property == eProperty.SpellRange ? 100 : 100;
    }

    private sealed class Commander : TestNpc
    {
        public byte TestLevel = 15;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
        public override bool IsAlive => true;
        public override bool IsCrowdControlled => false;
        public override bool IsBeingInterrupted => false;
        public override int Health { get; set; } = 100;
        public override int Mana { get; set; } = 100;
        public override int Endurance { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int MaxMana => 100;
        public override int MaxEndurance => 100;
        public override int GetModified(eProperty property) => property == eProperty.SpellRange ? 100 : 100;
    }

    private sealed class Patroller : BdSubPet
    {
        private Patroller() : base(null) { }
        public byte TestLevel = 15;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
        public override bool IsAlive => true;
        public override bool IsCrowdControlled => false;
        public override bool IsBeingInterrupted => false;
        public override int Health { get; set; } = 100;
        public override int Mana { get; set; } = 100;
        public override int Endurance { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int MaxMana => 100;
        public override int MaxEndurance => 100;
        public override int GetModified(eProperty property) => property == eProperty.SpellRange ? 100 : 100;
    }

    private sealed class BoneMage : BdSubPet
    {
        private BoneMage() : base(null) { }
        public override bool IsAlive => true;
        public override bool IsCrowdControlled => false;
        public override bool IsBeingInterrupted => false;
        public override int Health { get; set; } = 100;
        public override int Mana { get; set; } = 100;
        public override int Endurance { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int MaxMana => 100;
        public override int MaxEndurance => 100;
        public override int GetModified(eProperty property) => 100;
    }

    private sealed class Enemy : TestNpc
    {
        public override void TakeDamage(AttackData ad) { }
        public override void StartInterruptTimer(int duration, AttackData.eAttackType attackType, GameLiving attacker) { }
    }

    private sealed class BufferBrain : BdBufferBrain
    {
        public BufferBrain(GameLiving owner) : base(owner) { }
        public GameLiving Choose(Spell spell) => FindTargetForDefensiveSpell(spell);
    }

    [SetUp]
    public void SetUp()
    {
        _oldServer = GameServer.Instance;
        _oldTime = GameLoop.GameLoopTime;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        _language = new PetTestLanguageScope();
        _region = new Region(new RegionData { Id = 100, Name = "Bone test", Description = "Bone test", Mobs = [] });
        SetTime(100_000);
        ScriptMgr.ClearSpellHandlerCache();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (GameLiving actor in _actors)
        {
            ServiceObjectStore.Remove(actor.castingComponent);
            ServiceObjectStore.Remove(actor.effectListComponent);
        }
        _actors.Clear();
        _language.Dispose();
        GameServer.LoadTestDouble(_oldServer);
        SetTime(_oldTime);
    }

    [TestCase(false, "Bladeturn", "Self", 1.0, 10305)]
    [TestCase(false, "CombatSpeedBuff", "Realm", 2.5, 10306)]
    [TestCase(false, "DamageShield", "Realm", 2.0, 10304)]
    [TestCase(true, "Bladeturn", "Self", 1.0, 10305)]
    [TestCase(true, "CombatSpeedBuff", "Realm", 2.5, 10306)]
    [TestCase(true, "DamageShield", "Realm", 2.0, 10304)]
    public void PatrollerCompletesOneRealCastWithoutQueueSpam(
        bool temporaryCompanion, string type, string target, double castSeconds, int spellId)
    {
        (RootBot root, Commander commander, Patroller patroller, BufferBrain brain) = Graph(temporaryCompanion);
        Spell spell = new(new DbSpell
        {
            SpellID = spellId,
            Name = type,
            Type = type,
            Target = target,
            Range = target == "Self" ? 0 : 1500,
            CastTime = castSeconds,
            Duration = 600,
            Value = 5,
            Damage = type == "DamageShield" ? 5 : 0,
            EffectGroup = type switch { "DamageShield" => 5, "Bladeturn" => 52, _ => 100 }
        }, 1);

        patroller.Spells = [spell];
        patroller.SortSpells();

        Assert.That(brain.Choose(spell), Is.Not.Null,
            $"Patroller target selection failed: bodyRealm={patroller.Realm}, rootRealm={root.Realm}, bodyRegion={patroller.CurrentRegionID}, rootRegion={root.CurrentRegionID}");

        Assert.That(patroller.CanCastMiscSpells, Is.True,
            $"{type} must be classified as a defensive miscellaneous spell " +
            $"(spellCast={spell.CastTime}, source={patroller.Spells?.Count ?? -1}, " +
            $"misc={patroller.MiscSpells?.Count ?? -1}, instantMisc={patroller.InstantMiscSpells?.Count ?? -1}, " +
            $"harmful={spell.IsHarmful}, healing={spell.IsHealing}).");
        brain.Think();
        Assert.That(patroller.castingComponent.HasPendingSkillRequests, Is.True,
            "The Patroller must submit exactly one asynchronous cast request.");
        Assert.That(((NpcCastingComponent)patroller.castingComponent).HasPendingLosCheckRequests, Is.False,
            "Support casts must never wait for a real player's client LoS response.");

        patroller.castingComponent.Tick();
        SpellHandler handler = patroller.castingComponent.SpellHandler;
        Assert.That(handler, Is.Not.Null);
        Assert.That(handler.CastState, Is.EqualTo(eCastState.Casting));

        for (int i = 0; i < 5; i++)
        {
            brain.Think();
            Assert.That(patroller.castingComponent.HasPendingSkillRequests, Is.False,
                "AI ticks during the cast must not enqueue duplicate buffs.");
            Assert.That(patroller.castingComponent.QueuedSpellHandler, Is.Null,
                "A second Patroller spell must not replace or queue behind the active cast.");
            Assert.That(patroller.castingComponent.SpellHandler, Is.SameAs(handler));
        }

        SetTime(100_000 + (long)(castSeconds * 1000) + 50);
        patroller.castingComponent.Tick();
        patroller.effectListComponent.BeginTick();
        commander.effectListComponent.BeginTick();
        root.effectListComponent.BeginTick();

        GameLiving expected = target == "Self" ? patroller : patroller;
        Assert.That(EffectListService.GetEffectOnTarget(expected, EffectHelper.GetEffectFromSpell(spell)), Is.Not.Null,
            "The real spell handler must finish and apply the requested Patroller effect.");
        Assert.That(patroller.castingComponent.SpellHandler, Is.Null,
            "The completed non-focus cast must leave no latched handler.");
        Assert.That(patroller.castingComponent.QueuedSpellHandler, Is.Null);
    }

    [Test]
    public void BoneMageStopsFormationMovementAndCompletesAutonomousCast()
    {
        (RootBot _, Commander commander, _, _) = Graph(false);
        BoneMage mage = Actor<BoneMage>();
        TestNpc enemy = Actor<Enemy>();
        mage.Name = "bone mage";
        enemy.Name = "test enemy";
        Set(typeof(GameObject), commander, "<Realm>k__BackingField", eRealm.Midgard);
        Set(typeof(GameObject), mage, "<Realm>k__BackingField", eRealm.Midgard);
        Set(typeof(GameObject), enemy, "<Realm>k__BackingField", eRealm.Albion);

        BdCasterBrain brain = new(commander) { Body = mage };
        Set(typeof(GameNPC), mage, "m_ownBrain", brain);
        commander.InitControlledBrainArray(1);
        commander.ControlledNpcList[0] = brain;

        Spell spell = new(new DbSpell
        {
            SpellID = 60119,
            Name = "Pet Lifetap",
            Type = "DirectDamage",
            Target = "Enemy",
            Range = 1500,
            CastTime = 3.0,
            Damage = 25,
            DamageType = (int)eDamageType.Body,
        }, 1);
        mage.Spells = [spell];
        // This focused lifecycle fixture bypasses the full NPC-template loader;
        // production Bone Mage templates populate the same sorted list.
        mage.HarmfulSpells = [spell];
        mage.TargetObject = enemy;

        Assert.That(mage.CanCastHarmfulSpells, Is.True);
        Assert.That(mage.IsWithinRadius(enemy, spell.CalculateEffectiveRange(mage)), Is.True);

        mage.movementComponent.CurrentSpeed = 100;
        Assert.That(mage.IsMoving, Is.True);

        Assert.That(brain.CheckSpells(StandardMobBrain.eCheckSpellType.Offensive), Is.True);
        Assert.That(mage.IsMoving, Is.False,
            "The caster must settle before its asynchronous cast is enqueued.");
        Assert.That(mage.castingComponent.HasPendingSkillRequests, Is.True);
        Assert.That(((NpcCastingComponent)mage.castingComponent).HasPendingLosCheckRequests, Is.False,
            "Bone Mage casts must not depend on a nearby player's LoS reply.");

        mage.castingComponent.Tick();
        SpellHandler handler = mage.castingComponent.SpellHandler;
        Assert.That(handler, Is.Not.Null);
        Assert.That(handler.CastState, Is.EqualTo(eCastState.Casting));

        brain.FollowOwner();
        Assert.That(mage.IsMoving, Is.False,
            "Commander formation refreshes must not restart movement during the cast.");
        Assert.That(mage.castingComponent.SpellHandler, Is.SameAs(handler));

        SetTime(103_050);
        mage.castingComponent.Tick();
        Assert.That(mage.castingComponent.SpellHandler, Is.Null,
            "The uninterrupted Bone Mage cast must finish instead of restarting forever.");
    }

    private (RootBot, Commander, Patroller, BufferBrain) Graph(bool temporaryCompanion)
    {
        RootBot root = Actor<RootBot>();
        root.Level = 19;
        Set(typeof(GameObject), root, "<Realm>k__BackingField", eRealm.Midgard);
        if (temporaryCompanion)
        {
            Player player = Actor<Player>();
            Set(typeof(GameObject), player, "<Realm>k__BackingField", eRealm.Midgard);
            Set(typeof(GameBot), root, "<Owner>k__BackingField", player);
            Set(typeof(GameBot), root, "<IsTemporaryGroupHelper>k__BackingField", true);
            Group group = new(player);
            var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", Hidden).GetValue(group);
            members.Add(player);
            members.Add(root);
            player.Group = group;
            root.Group = group;
        }
        else
            Set(typeof(GameBot), root, "<IsAutonomousWorldBot>k__BackingField", true);

        Commander commander = Actor<Commander>();
        commander.Level = 15;
        CommanderBrain commanderBrain = new(root) { Body = commander };
        Set(typeof(GameNPC), commander, "m_ownBrain", commanderBrain);
        root.InitControlledBrainArray(1);
        Set(typeof(GameLiving), root, "m_controlledBrain", new IControlledBrain[] { commanderBrain });

        Patroller patroller = Actor<Patroller>();
        patroller.Level = 15;
        BufferBrain bufferBrain = new(commander) { Body = patroller };
        Set(typeof(GameNPC), patroller, "m_ownBrain", bufferBrain);
        commander.InitControlledBrainArray(1);
        commander.ControlledNpcList[0] = bufferBrain;
        return (root, commander, patroller, bufferBrain);
    }

    private T Actor<T>() where T : GameLiving
    {
        T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        SetNew(typeof(GameObject), actor, "_objectInRadiusCachesLock");
        SetNew(typeof(GameObject), actor, "_objectsInRadiusCaches");
        foreach (string field in new[] { "_changeHealthLock", "_changeManaLock", "_changeEnduranceLock", "_abilitiesLock" })
            Set(typeof(GameLiving), actor, field, new System.Threading.Lock());
        Set(typeof(GameLiving), actor, "_disabledSkillsLock", new System.Threading.Lock());
        Set(typeof(GameLiving), actor, "m_disabledSkills",
            new Dictionary<KeyValuePair<int, Type>, KeyValuePair<long, Skill>>());
        foreach (string property in new[] { "ItemBonus", "AbilityBonus", "BaseBuffBonusCategory", "SpecBuffBonusCategory", "OtherBonus", "DebuffCategory", "SpecDebuffCategory" })
            Set(typeof(GameLiving), actor, $"<{property}>k__BackingField", new PropertyIndexer());
        Set(typeof(GameLiving), actor, "<TempProperties>k__BackingField", new PropertyCollection());
        Set(typeof(GameLiving), actor, "m_effects", new GameEffectList(actor));
        Set(typeof(GameLiving), actor, "m_abilities", new Dictionary<string, Ability>());
        Set(typeof(GameLiving), actor, "<ActivePulseSpells>k__BackingField", new ConcurrentDictionary<eSpellType, Spell>());
        if (actor is GameNPC)
        {
            Set(typeof(GameNPC), actor, "m_brains", new ArrayList());
            Set(typeof(GameNPC), actor, "m_spells", new List<Spell>());
        }

        actor.CurrentRegion = _region;
        actor.ObjectState = GameObject.eObjectState.Active;
        actor.castingComponent = CastingComponent.Create(actor);
        if (actor is GameNPC npc)
            npc.castingComponent = (NpcCastingComponent)actor.castingComponent;
        actor.effectListComponent = EffectListComponent.Create(actor);
        actor.attackComponent = new AttackComponent(actor);
        if (actor is GameNPC movingNpc)
        {
            movingNpc.movementComponent = new NpcMovementComponent(movingNpc);
            actor.movementComponent = movingNpc.movementComponent;
            movingNpc.movementComponent.ForceUpdatePosition();
        }
        _actors.Add(actor);
        return actor;
    }

    private static void Set(Type type, object target, string field, object value) =>
        type.GetField(field, Hidden).SetValue(target, value);

    private static void SetNew(Type type, object target, string field)
    {
        FieldInfo info = type.GetField(field, Hidden);
        info.SetValue(target, Activator.CreateInstance(info.FieldType));
    }

    private static void SetTime(long value) =>
        typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, value);
}
