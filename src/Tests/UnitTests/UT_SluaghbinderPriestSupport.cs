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

/// <summary>
/// The Zombie Priest must stop meleeing and heal for both real players and
/// autonomous gamebot Sluaghbinders: heal-over-time below 75%, direct heal
/// below 50%, using the real asynchronous NPC cast path.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class UT_SluaghbinderPriestSupport
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
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

    private sealed class OwnerBot : GameBot
    {
        private OwnerBot() : base((OfflineWorldBotRecord)null) { }
        public byte TestLevel = 50;
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
        public override int GetModified(eProperty property) => 100;
    }

    private sealed class OwnerPlayer : GamePlayer
    {
        private OwnerPlayer() : base(null, null) { }
        public override string Name { get; set; }
        public override GS.PacketHandler.IPacketLib Out =>
            DispatchProxy.Create<GS.PacketHandler.IPacketLib, UT_RealmExchangeEconomy.NoOpPacketLib>();
        public override byte Level { get; set; } = 50;
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

    private sealed class Priest : GameNPC
    {
        public byte TestLevel = 44;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
        public override bool IsAlive => true;
        public override bool IsCrowdControlled => false;
        public override int Health { get; set; } = 100;
        public override int Mana { get; set; } = 100;
        public override int Endurance { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int MaxMana => 100;
        public override int MaxEndurance => 100;
        public override int GetModified(eProperty property) => 100;
    }

    private sealed class Enemy : GameNPC
    {
        public override bool IsAlive => true;
        public override int Health { get; set; } = 100;
        public override int MaxHealth => 100;
        public override int GetModified(eProperty property) => 100;
        public override void TakeDamage(AttackData ad) { }
        public override void StartInterruptTimer(int duration, AttackData.eAttackType attackType, GameLiving attacker) { }
    }

    [SetUp]
    public void SetUp()
    {
        _oldServer = GameServer.Instance;
        _oldTime = GameLoop.GameLoopTime;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        _language = new PetTestLanguageScope();
        _region = new Region(new RegionData { Id = 200, Name = "Priest test", Description = "Priest test", Mobs = [] });
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

    // Same data as the live spells 59037 / 59038 (interruptible, single target).
    private static Spell HealOverTime() => new(new DbSpell
    {
        SpellID = 59037, Name = "Miasma of Renewal", Type = "HealOverTime", Target = "Realm",
        Range = 2000, CastTime = 3.0, Value = 45, Duration = 15, Frequency = 30, Power = 8,
        Uninterruptible = false
    }, 1);

    private static Spell DirectHeal() => new(new DbSpell
    {
        SpellID = 59038, Name = "Priest's Mending", Type = "Heal", Target = "Realm",
        Range = 1500, CastTime = 3.0, Value = 240, Power = 20, Uninterruptible = false
    }, 1);

    public enum Situation { Idle, BeingHit, MidSwing, ScaledSpells }

    [Test]
    public void FactoryPriestHealsItsOwner(
        [Values(60, 40)] int ownerHealth,
        [Values] Situation situation,
        [Values(false, true)] bool playerOwned)
    {
        int expectedSpell = ownerHealth < SluaghbinderPetBrain.PriestDirectHealThreshold ? 59038 : 59037;
        (GameLiving owner, Priest priest, SluaghbinderPetBrain brain, Enemy enemy) = Graph(playerOwned);
        owner.Health = ownerHealth;

        switch (situation)
        {
            case Situation.BeingHit:
                // A gamebot's pet is interrupted for 3 s + 2.5 s by each hit.
                Set(typeof(GameLiving), priest, "<InterruptTime>k__BackingField", GameLoop.GameLoopTime + 5_500);
                break;
            case Situation.MidSwing:
                priest.attackComponent.AttackState = true;
                Set(typeof(GameLiving), priest, "<SelfInterruptTime>k__BackingField", GameLoop.GameLoopTime + 3_500);
                break;
            case Situation.ScaledSpells:
                // Below owner level 50 the castable list holds level-scaled copies.
                for (int i = 0; i < priest.HealSpells.Count; i++)
                    priest.HealSpells[i] = priest.HealSpells[i].Copy();
                break;
        }

        Assert.That(priest.CanCastHealSpells, Is.True, "both heals must be sorted as castable heal spells");

        brain.AttackMostWanted();

        if (situation is Situation.BeingHit or Situation.MidSwing)
        {
            Assert.That(priest.castingComponent.HasPendingSkillRequests, Is.False,
                "normal interrupt timers must be respected");
            SetTime(106_000);
            brain.AttackMostWanted();
        }

        Assert.That(priest.IsAttacking, Is.False, "healing must stop the active melee attack");

        Assert.That(priest.castingComponent.HasPendingSkillRequests, Is.True,
            "the priest must stop meleeing and submit a heal");
        Assert.That(((NpcCastingComponent)priest.castingComponent).HasPendingLosCheckRequests, Is.False,
            "a gamebot owner has no client to answer an LoS request");

        priest.castingComponent.Tick();
        SpellHandler handler = priest.castingComponent.SpellHandler;
        Assert.That(handler, Is.Not.Null);
        Assert.That(handler.Spell.ID, Is.EqualTo(expectedSpell));
        Assert.That(handler.Target, Is.SameAs(owner));
        Assert.That(priest.TargetObject, Is.SameAs(enemy), "her enemy target is restored after queuing the heal");

        for (int i = 0; i < 4; i++)
        {
            Assert.That(brain.CheckSpells(StandardMobBrain.eCheckSpellType.Defensive), Is.True,
                "while the heal is casting the brain must keep holding melee");
            brain.AttackMostWanted();
            Assert.That(priest.castingComponent.SpellHandler, Is.SameAs(handler), "think ticks must not restart the cast");
            Assert.That(priest.castingComponent.QueuedSpellHandler, Is.Null);
            Assert.That(priest.castingComponent.HasPendingSkillRequests, Is.False, "no duplicate heal requests");
        }

        SetTime(GameLoop.GameLoopTime + 3_050);
        priest.castingComponent.Tick();
        Assert.That(priest.castingComponent.SpellHandler, Is.Null, "the cast must complete");
        if (expectedSpell == 59038)
            Assert.That(owner.Health, Is.GreaterThan(ownerHealth), "the direct heal must land on the owner");
    }

    [Test]
    public void PriestWithHealthyPartyKeepsMeleeing()
    {
        (GameLiving _, Priest priest, SluaghbinderPetBrain brain, Enemy _) = Graph();
        Assert.That(brain.CheckSpells(StandardMobBrain.eCheckSpellType.Defensive), Is.False);
        brain.AttackMostWanted();
        Assert.That(priest.castingComponent.HasPendingSkillRequests, Is.False);
    }

    [Test]
    public void PriestHealsTheMostInjuredPartyPetFirst()
    {
        (GameLiving owner, Priest priest, SluaghbinderPetBrain brain, Enemy _) = Graph();
        owner.Health = 45;
        priest.Health = 30;
        brain.AttackMostWanted();
        priest.castingComponent.Tick();
        Assert.That(priest.castingComponent.SpellHandler?.Target, Is.SameAs(priest));
        Assert.That(priest.castingComponent.SpellHandler?.Spell.ID, Is.EqualTo(59038));
    }

    [TestCase(75, 0)]
    [TestCase(74, 59037)]
    [TestCase(50, 59037)]
    [TestCase(49, 59038)]
    public void PlayerOwnedPriestRecognizesInjuredCompanions(int health, int expectedSpell)
    {
        (GameLiving owner, Priest priest, SluaghbinderPetBrain brain, _) = Graph(true);
        OwnerBot companion = Actor<OwnerBot>();
        companion.Health = health;
        Set(typeof(GameObject), companion, "<Realm>k__BackingField", eRealm.Hibernia);
        Group group = new(owner);
        var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", Hidden).GetValue(group);
        members.Add(owner);
        members.Add(companion);
        owner.Group = companion.Group = group;
        brain.AttackMostWanted();
        priest.castingComponent.Tick();
        Assert.That(priest.castingComponent.SpellHandler?.Spell.ID ?? 0, Is.EqualTo(expectedSpell));
        if (expectedSpell != 0)
            Assert.That(priest.castingComponent.SpellHandler.Target, Is.SameAs(companion));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DruidSummonKeepsItsOriginalBrain(bool playerOwned)
    {
        GameLiving owner = playerOwned ? Actor<OwnerPlayer>() : Actor<OwnerBot>();
        Set(playerOwned ? typeof(GamePlayer) : typeof(GameBot), owner, "m_characterClass",
            new GS.PlayerClass.ClassDruid());
        var factory = (SummonDruidPet)RuntimeHelpers.GetUninitializedObject(typeof(SummonDruidPet));
        var brain = typeof(SummonDruidPet).GetMethod("GetPetBrain", Hidden).Invoke(factory, [owner]);
        Assert.That(brain, Is.TypeOf<ControlledMobBrain>());
    }

    [TestCase(60, false)]
    [TestCase(40, false)]
    [TestCase(60, true)]
    [TestCase(40, true)]
    public void BothPriestHealsAreInterruptedByAnAttack(int ownerHealth, bool playerOwned)
    {
        (GameLiving owner, Priest priest, SluaghbinderPetBrain brain, Enemy enemy) = Graph(playerOwned);
        owner.Health = ownerHealth;
        brain.AttackMostWanted();
        priest.castingComponent.Tick();
        SpellHandler handler = priest.castingComponent.SpellHandler;
        Assert.That(handler, Is.Not.Null);
        Assert.That(handler.Spell.ID, Is.EqualTo(ownerHealth < 50 ? 59038 : 59037));
        Assert.That(handler.CasterIsAttacked(enemy), Is.True);
        SetTime(103_050);
        priest.castingComponent.Tick();
        Assert.That(owner.Health, Is.EqualTo(ownerHealth), "interrupted healing must not land");
        Assert.That(priest.castingComponent.SpellHandler, Is.Null);
    }

    [TestCase("walking dead", 115, 0.65, 1283)]
    [TestCase("sturdy zombie", 120, 0.68, 1350)]
    [TestCase("zombie magician", 90, 0.50, 960)]
    [TestCase("zombie guardian", 145, 0.84, 1714)]
    [TestCase("zombie priest", 115, 0.55, 1086)]
    [TestCase("dullahan", 130, 0.69, 1385)]
    public void PlayerPetSharesBotHealthWithoutChangingItsDamage(string role, int conPercent, double factor, int hpAt50)
    {
        (GameLiving player, _, _, _) = Graph(true);
        (GameLiving bot, _, _, _) = Graph(false);
        GameSummonedPet playerPet = Actor<GameSummonedPet>();
        SluaghbinderPet botPet = Actor<SluaghbinderPet>();
        foreach (GameSummonedPet pet in new[] { playerPet, botPet })
        {
            pet.NPCTemplate = new NpcTemplate { Name = role };
            Set(typeof(GameLiving), pet, "m_charStat", new short[8]);
            Set(typeof(GameNPC), pet, "m_ownBrain", new SluaghbinderPetBrain(pet == playerPet ? player : bot) { Body = pet });
        }

        playerPet.DamageFactor = 1.0;
        foreach (byte ownerLevel in new byte[] { 10, 30, 50 })
        {
            byte petLevel = (byte)(ownerLevel * 0.88);
            foreach (GameSummonedPet pet in new[] { playerPet, botPet })
            {
                Set(typeof(GameObject), pet, "m_level", petLevel);
                var stats = (short[])typeof(GameLiving).GetField("m_charStat", Hidden).GetValue(pet);
                stats[eStat.CON - eStat._First] = (short)((30 + petLevel - 1) * (conPercent / 100.0));
            }
            var calculator = new MaxHealthCalculator();
            int playerHp = calculator.CalcValue(playerPet, eProperty.MaxHealth);
            Assert.That(playerPet.MaxHealthScalingFactor, Is.EqualTo(factor));
            Assert.That(playerHp, Is.EqualTo(calculator.CalcValue(botPet, eProperty.MaxHealth)));
            if (ownerLevel == 50)
                Assert.That(playerHp, Is.EqualTo(hpAt50));
            Assert.That(playerPet.DamageFactor, Is.EqualTo(1.0));
        }

        // A normal pet must not inherit the reduction merely by sharing a name.
        Set(typeof(GamePlayer), player, "m_characterClass", new GS.PlayerClass.ClassDruid());
        Assert.That(playerPet.MaxHealthScalingFactor, Is.EqualTo(1.0));
    }

    private (GameLiving, Priest, SluaghbinderPetBrain, Enemy) Graph(bool playerOwned = false)
    {
        GameLiving owner = playerOwned ? Actor<OwnerPlayer>() : Actor<OwnerBot>();
        owner.Name = "Pettest";
        Set(typeof(GameObject), owner, "<Realm>k__BackingField", eRealm.Hibernia);
        Set(playerOwned ? typeof(GamePlayer) : typeof(GameBot), owner, "m_characterClass",
            new GS.PlayerClass.ClassSluaghbinder());
        if (!playerOwned)
            Set(typeof(GameBot), owner, "<IsAutonomousWorldBot>k__BackingField", true);
        else
        {
            var client = (GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient));
            client.Account = new DbAccount { PrivLevel = 1, Language = "EN" };
            Set(typeof(GamePlayer), owner, "m_client", client);
        }

        Priest priest = Actor<Priest>();
        priest.Name = "zombie priest";
        priest.NPCTemplate = new NpcTemplate { Name = "zombie priest" };
        Set(typeof(GameObject), priest, "<Realm>k__BackingField", eRealm.Hibernia);
        // Exercise the production summon selection, not a manually chosen brain.
        var factory = (SummonDruidPet)RuntimeHelpers.GetUninitializedObject(typeof(SummonDruidPet));
        var selected = (IControlledBrain)typeof(SummonDruidPet).GetMethod("GetPetBrain", Hidden)
            .Invoke(factory, [owner]);
        Assert.That(selected, Is.TypeOf<SluaghbinderPetBrain>(),
            "real players and gamebots must both receive the priest's healing brain");
        SluaghbinderPetBrain brain = (SluaghbinderPetBrain)selected;
        brain.Body = priest;
        Set(typeof(GameNPC), priest, "m_ownBrain", brain);
        owner.InitControlledBrainArray(1);
        Set(typeof(GameLiving), owner, "m_controlledBrain", new IControlledBrain[] { brain });

        Enemy enemy = Actor<Enemy>();
        enemy.Name = "test enemy";
        Set(typeof(GameObject), enemy, "<Realm>k__BackingField", eRealm.None);

        owner.Level = 50; priest.TestLevel = 44;
        owner.Health = priest.Health = enemy.Health = 100;
        owner.Mana = priest.Mana = 100;
        priest.Spells = [HealOverTime(), DirectHeal()];
        priest.TargetObject = enemy;
        brain.AddToAggroList(enemy, 100);
        return (owner, priest, brain, enemy);
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
        actor.styleComponent = StyleComponent.Create(actor);
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
