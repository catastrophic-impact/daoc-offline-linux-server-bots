using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.Spells;
using DOL.GS.Effects;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_NecromancerPlayerSpellDispatch
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    private GameServer _previousServer;
    private PetTestLanguageScope _language;
    private readonly List<GameLiving> _actors = new();

    private sealed class Server : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }
    private sealed class Player : GamePlayer
    {
        private Player() : base(null, null) { }
        public override bool IsAlive => true;
        public override int Mana { get; set; }
        public override ICharacterClass CharacterClass => new ClassNecromancer();
        public override int GetModified(eProperty property) => 0;
        public override int GetModifiedSpecLevel(string spec) => 1;
        public override IControlledBrain ControlledBrain { get; set; }
        public override int ChangeMana(GameObject source, eManaChangeType type, int amount) { Mana += amount; return amount; }
    }
    private sealed class Bot : GameBot
    {
        private Bot() : base((OfflineWorldBotRecord)null) { }
        public bool Moving;
        public bool Combat;
        public int StopMovingCalls;
        public override bool IsAlive => true;
        public override bool InCombat => Combat;
        public override bool IsAttacking => false;
        public override bool IsCasting => false;
        public override bool IsMoving => Moving;
        public override int Mana { get; set; }
        public override int GetModified(eProperty property) => 0;
        public override IControlledBrain ControlledBrain { get; set; }
        public override int ChangeMana(GameObject source, eManaChangeType type, int amount) { Mana += amount; return amount; }
        public override void StopMoving() { StopMovingCalls++; Moving = false; }
    }
    private sealed class Servant : NecromancerPet
    {
        private Servant() : base(null) { }
        public Spell RequestedSpell;
        public bool RequestedLos;
        public bool Casting;
        public bool Moving;
        public int StopMovingCalls;
        public override int Health { get; set; }
        public override int MaxHealth => 100;
        public override GameObject TargetObject { get; set; }
        public override bool IsAlive => true;
        public override bool IsCasting => Casting;
        public override bool IsMoving => Moving;
        public override bool InCombat => false;
        public override bool IsAttacking => false;
        public override void StopMoving() { StopMovingCalls++; Moving = false; }
        public override bool CastSpell(Spell spell, SpellLine line, bool checkLos)
        {
            RequestedSpell = spell;
            RequestedLos = checkLos;
            // The real casting component awaits client LoS. Keep the command
            // pending to verify dispatch without faking spell damage or a client.
            return false;
        }
    }
    private sealed class SpellIndex : SkillBase
    {
        public static Dictionary<int, Spell> Index => m_spellIndex;
    }

    private sealed class RecordingServantBrain(GameLiving owner) : NecromancerPetBrain(owner)
    {
        public int Disengages;
        public override void Disengage() => Disengages++;
    }

    [Test]
    public void ServantCastingBlocksNewBotUpkeepUntilItCompletes()
    {
        Bot owner = Actor<Bot>();
        Servant pet = Actor<Servant>();
        var brain = new RecordingServantBrain(owner) { Body = pet };
        Assert.That(brain.HasPendingBotCommand, Is.False);
        pet.Casting = true;
        Assert.That(brain.HasPendingBotCommand, Is.True);
        pet.Casting = false;
        Assert.That(brain.HasPendingBotCommand, Is.False, "Completion must release upkeep, not permanently lock the pet");
    }

    [Test]
    public void NecromancerPetSpellsHaveOneDedicatedUpkeepOwner()
    {
        Assert.That(AutonomousPetSupport.OwnsPetUpkeep(eCharacterClass.Necromancer), Is.True,
            "The generic defensive selector must not compete with servant upkeep");
    }

    [TestCase("DexterityBuff", 6021, "Self", 3, true)]
    [TestCase("StrengthBuff", 6421, "Self", 0, false)]
    [TestCase("DamageShield", 6511, "Self", 0, false)]
    [TestCase("HealOverTime", 6461, "Self", 0, false)]
    [TestCase("StrengthConstitutionBuff", 6331, "Self", 3, true)]
    [TestCase("ArmorAbsorptionBuff", 6101, "Realm", 3, true)]
    [TestCase("PowerTransferPet", 6121, "Realm", 3, false)]
    public void OnlyRealCastTimeServantBuffsRequireMovementHold(
        string type, int id, string target, int castSeconds, bool expected)
    {
        var wrapper = new Spell(new DbSpell
        {
            SpellID = id + 3000,
            Type = "PetSpell",
            Target = "Pet",
            SubSpellID = id
        }, 1);
        var payload = new Spell(new DbSpell
        {
            SpellID = id,
            Type = type,
            Target = target,
            CastTime = castSeconds,
            Duration = type == "PowerTransferPet" ? 0 : 1200
        }, 1);
        SpellIndex.Index.TryGetValue(id, out Spell previous);
        try
        {
            SpellIndex.Index[id] = payload;
            Assert.That(AutonomousPetSupport.TryGetNecromancerPetPayload(wrapper, out Spell resolved), Is.True);
            Assert.That(resolved.ID, Is.EqualTo(payload.ID));
            Assert.That(resolved.SpellType, Is.EqualTo(payload.SpellType));
            Assert.That(AutonomousPetSupport.IsInterruptibleNecromancerServantBuff(wrapper), Is.EqualTo(expected));
        }
        finally
        {
            if (previous == null) SpellIndex.Index.Remove(id);
            else SpellIndex.Index[id] = previous;
        }
    }

    [TestCase("DexterityBuff", 6021, 1200)]
    [TestCase("StrengthBuff", 6421, 1200)]
    [TestCase("DamageShield", 6511, 30)]
    [TestCase("HealOverTime", 6461, 15)]
    public void PetCommandRecognizesItsRealEffectAndOnlyRecastsAfterRemoval(string type, int id, int seconds)
    {
        Servant pet = Actor<Servant>();
        pet.Health = 50;
        var wrapper = new Spell(new DbSpell { SpellID = id + 3000, Type = "PetSpell", Target = "Pet", Duration = seconds, SubSpellID = id }, 1);
        var payload = new Spell(new DbSpell { SpellID = id, Type = type, Target = "Self", Duration = seconds }, 1);
        SpellIndex.Index.TryGetValue(id, out Spell previous);
        try
        {
            SpellIndex.Index[id] = payload;
            Assert.That(AutonomousPetSupport.NeedsNecromancerPetCommand(pet, wrapper), Is.True);
            eEffect category = EffectHelper.GetEffectFromSpell(payload);
            Assert.That(category, Is.Not.EqualTo(EffectHelper.GetEffectFromSpell(wrapper)), "The wrapper is not the installed effect");
            var effects = (Dictionary<eEffect, List<ECSGameEffect>>)typeof(EffectListComponent).GetField("_effects", Hidden).GetValue(pet.effectListComponent);
            effects[category] = new List<ECSGameEffect> { (ECSGameSpellEffect)RuntimeHelpers.GetUninitializedObject(typeof(ECSGameSpellEffect)) };
            for (int retry = 0; retry < 20; retry++)
                Assert.That(AutonomousPetSupport.NeedsNecromancerPetCommand(pet, wrapper), Is.False, "An active payload must suppress repeated wrapper casts");
            effects.Remove(category);
            Assert.That(AutonomousPetSupport.NeedsNecromancerPetCommand(pet, wrapper), Is.True, "Expiration/dispel must allow a real recast");
            if (type == "HealOverTime")
            {
                pet.Health = 100;
                Assert.That(AutonomousPetSupport.NeedsNecromancerPetCommand(pet, wrapper), Is.False, "Do not heal a full-health pet every fifteen seconds");
            }
        }
        finally
        {
            if (previous == null) SpellIndex.Index.Remove(id);
            else SpellIndex.Index[id] = previous;
        }
    }

    [Test]
    public void PendingServantSelfBuffSurvivesStaleAggroCleanup()
    {
        Bot owner = Actor<Bot>();
        Servant pet = Actor<Servant>();
        var brain = new RecordingServantBrain(owner) { Body = pet };
        typeof(GameNPC).GetField("m_ownBrain", Hidden).SetValue(owner, new BotBrain { Body = owner });
        pet.attackComponent = new AttackComponent(pet);
        typeof(AttackAction).GetField("_nextMeleeTick", Hidden).SetValue(pet.attackComponent.attackAction, long.MaxValue);
        var payload = new Spell(new DbSpell { SpellID = 6021, Name = "Servant of Death", Type = "DexterityBuff", Target = "Self", CastTime = 3, Duration = 1200 }, 1);
        pet.TargetObject = pet;
        brain.OnOwnerFinishPetSpellCast(payload, new SpellLine("Deathsight", "Deathsight", "Deathsight", true), pet);
        pet.TargetObject = owner;
        Assert.That(brain.HasPendingSelfBuffCommand, Is.True,
            "Follow targeting the shade must not hide the queued servant self-buff");
        brain.AddToAggroList(Actor<Player>(), 1);
        Assert.That(brain.HasPendingSelfBuffCommand, Is.True, "The native queue proves this is a real helpful self-command");
        Assert.That(AutonomousPetSupport.IsNecromancerSelfBuffInProgress(owner, brain), Is.True);
        var cleanup = typeof(AutonomousPetSupport).GetMethod("ClearStalePetCombat", BindingFlags.Static | BindingFlags.NonPublic);
        for (int tick = 0; tick < 20; tick++)
            cleanup.Invoke(null, new object[] { owner, brain, null, false });
        Assert.That(brain.Disengages, Is.Zero, "A stale combat flag must not cancel a verified servant self-buff");
        brain.ClearSpellQueue();
        pet.TargetObject = Actor<Player>();
        cleanup.Invoke(null, new object[] { owner, brain, null, false });
        Assert.That(brain.Disengages, Is.EqualTo(1), "Stale living enemy targets must still be cleared");
    }

    [Test]
    public void OwnerAndServantHoldMovementUntilQueuedBuffCompletesButNotInCombat()
    {
        Bot owner = Actor<Bot>();
        Servant pet = Actor<Servant>();
        var servantBrain = new RecordingServantBrain(owner) { Body = pet };
        var ownerBrain = new BotBrain { Body = owner };
        owner.ControlledBrain = servantBrain;
        typeof(GameNPC).GetField("m_ownBrain", Hidden).SetValue(owner, ownerBrain);
        pet.attackComponent = new AttackComponent(pet);
        typeof(AttackAction).GetField("_nextMeleeTick", Hidden)
            .SetValue(pet.attackComponent.attackAction, long.MaxValue);

        var payload = new Spell(new DbSpell
        {
            SpellID = 6021,
            Name = "Servant of Death",
            Type = "DexterityBuff",
            Target = "Self",
            CastTime = 3,
            Duration = 1200
        }, 1);
        servantBrain.OnOwnerFinishPetSpellCast(
            payload,
            new SpellLine("Deathsight", "Deathsight", "Deathsight", true),
            pet);
        owner.Moving = true;
        pet.Moving = true;

        var hold = typeof(BotBrain).GetMethod(
            "TryHoldForNecromancerServantSelfBuff",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(hold.Invoke(ownerBrain, null), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(owner.StopMovingCalls, Is.EqualTo(1));
            Assert.That(pet.StopMovingCalls, Is.EqualTo(1));
        });

        owner.Moving = true;
        pet.Moving = true;
        owner.Combat = true;
        Assert.That(hold.Invoke(ownerBrain, null), Is.False,
            "Combat must immediately release the maintenance hold");
        Assert.Multiple(() =>
        {
            Assert.That(owner.StopMovingCalls, Is.EqualTo(1));
            Assert.That(pet.StopMovingCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void SelfBuffExceptionDoesNotCoverHumanPetsOrEnemyTargets()
    {
        Bot owner = Actor<Bot>();
        Servant pet = Actor<Servant>();
        var brain = new RecordingServantBrain(owner) { Body = pet };
        pet.TargetObject = owner;
        Assert.That(AutonomousPetSupport.IsNecromancerSelfBuffInProgress(owner, brain), Is.False);
        pet.TargetObject = pet;
        Player human = Actor<Player>();
        Assert.That(AutonomousPetSupport.IsNecromancerSelfBuffInProgress(human, new RecordingServantBrain(human) { Body = pet }), Is.False);
        Assert.That(AutonomousPetSupport.IsNecromancerSelfBuffInProgress(owner, new ControlledMobBrain(owner) { Body = pet }), Is.False);
    }

    [SetUp]
    public void Setup()
    {
        _language = new PetTestLanguageScope();
        _previousServer = GameServer.Instance;
        GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
    }
    [TearDown]
    public void Teardown()
    {
        foreach (GameLiving actor in _actors) ServiceObjectStore.Remove(actor.effectListComponent);
        _actors.Clear();
        GameServer.LoadTestDouble(_previousServer);
        _language.Dispose();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HealthEvacuationIsDispatchedByHumanAndBotOwners(bool botOwner)
    {
        GameLiving owner = botOwner ? Actor<Bot>() : Actor<Player>();
        owner.Mana = 20;
        Servant pet = Actor<Servant>();
        var brain = new NecromancerPetBrain(owner) { Body = pet };
        owner.ControlledBrain = brain;
        typeof(GameNPC).GetField("m_ownBrain", Hidden).SetValue(pet, brain);
        pet.attackComponent = new AttackComponent(pet);
        pet.styleComponent = StyleComponent.Create(pet);
        var line = new SpellLine("Deathsight", "Deathsight", "Deathsight", true);
        var wrapper = new Spell(new DbSpell { SpellID = 9011, Name = "Health Evacuation", Type = "PetSpell", Target = "Enemy", Power = 2, SubSpellID = 6011 }, 1);
        var subSpell = new Spell(new DbSpell { SpellID = 6011, Name = "Health Evacuation", Type = "Lifedrain", Target = "Enemy", Power = 2, CastTime = 3, Range = 1500 }, 17);
        SpellIndex.Index.TryGetValue(6011, out Spell previous);
        try
        {
            SpellIndex.Index[6011] = subSpell;
            var handler = (SpellHandler)Activator.CreateInstance(typeof(SpellHandler).Assembly.GetType("DOL.GS.Spells.PetSpellHandler"), owner, wrapper, line);
            handler.FinishSpellCast(pet);
            Assert.Multiple(() =>
            {
                Assert.That(owner.Mana, Is.EqualTo(18), "The shade command must complete for humans as well as bots");
                Assert.That(brain.HasSpellsQueued(), Is.True, "The servant receives the real subspell command");
                Assert.That(brain.HasPendingBotCommand, Is.True, "Upkeep must wait even before the queued servant cast starts");
                Assert.That(pet.RequestedSpell?.ID, Is.EqualTo(6011));
                Assert.That(pet.RequestedSpell?.Level, Is.EqualTo(1));
                Assert.That(pet.RequestedLos, Is.True, "Normal servant line-of-sight checks remain enabled");
                Assert.That(subSpell.Level, Is.EqualTo(17), "Only the dispatched clone receives the wrapper's level");
            });
        }
        finally
        {
            if (previous == null) SpellIndex.Index.Remove(6011);
            else SpellIndex.Index[6011] = previous;
        }
    }

    private T Actor<T>() where T : GameLiving
    {
        T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        actor.ObjectState = GameObject.eObjectState.Active;
        typeof(GameLiving).GetField("<TempProperties>k__BackingField", Hidden).SetValue(actor, new PropertyCollection());
        typeof(GameLiving).GetField("m_effects", Hidden).SetValue(actor, new GameEffectList(actor));
        if (actor is GameNPC) typeof(GameNPC).GetField("m_brains", Hidden).SetValue(actor, new ArrayList());
        actor.effectListComponent = EffectListComponent.Create(actor);
        _actors.Add(actor);
        return actor;
    }
}
