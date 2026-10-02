using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.Database.Handlers;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_BotRangedCombat
{
    private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    private sealed class Server : GameServer { protected override IObjectDatabase DataBaseImpl => Empty; }
    private GameServer _previous;
    [SetUp] public void Setup()
    {
        _previous = GameServer.Instance;
        GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
    }
    [TearDown] public void Cleanup() => GameServer.LoadTestDouble(_previous);
    private sealed class Bot : GameBot
    {
        private Bot() : base((OfflineWorldBotRecord)null) { }
        public override byte Level { get; set; }
        public override int Endurance { get; set; }
        public override short Quickness { get => 60; set { } }
        public override int GetModified(eProperty property) => property == eProperty.MeleeSpeed ? 100 : 0;
        public override bool HasAbilityToUseItem(DbItemTemplate item) => true;
        public override int WeaponSpecLevel(DbInventoryItem item) => 1;
        public override void RefreshItemBonuses() { }
        public void PrimeActiveDistanceWeapon(DbInventoryItem weapon)
        {
            Inventory = new BotInventory();
            Inventory.AddItem(eInventorySlot.DistanceWeapon, weapon);
            rangeAttackComponent = new RangeAttackComponent(this);
            attackComponent = new AttackComponent(this);
            typeof(GameLiving).GetField("_activeWeapon", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, weapon);
            m_activeWeaponSlot = eActiveWeaponSlot.Distance;
        }
    }

    [TestCase(eObjectType.Thrown)] [TestCase(eObjectType.Fired)]
    [TestCase(eObjectType.Longbow)] [TestCase(eObjectType.Crossbow)]
    [TestCase(eObjectType.CompositeBow)] [TestCase(eObjectType.RecurvedBow)]
    public void StarterHasRealModelDamageAndCadenceWithReusableAmmo(eObjectType type)
    {
        using var language = new PetTestLanguageScope();
        var bot = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
        bot.Level = 1; bot.Endurance = 100; bot.Inventory = new BotInventory();
        bot.rangeAttackComponent = new RangeAttackComponent(bot);
        bot.attackComponent = new AttackComponent(bot);
        var weapon = GameInventoryItem.Create(new DbItemUnique(BotRangedCombat.CreateStarter(eRealm.Midgard, eCharacterClass.Warrior, type)));
        bot.Inventory.AddItem(eInventorySlot.DistanceWeapon, weapon);
        Assert.That(BotRangedCombat.CanUse(bot, weapon), Is.True);
        Assert.That(bot.attackComponent.AttackSpeed(weapon), Is.GreaterThanOrEqualTo(1500));
        Assert.That(bot.WeaponDamageWithoutQualityAndCondition(weapon), Is.GreaterThan(0));
        var ammo = bot.rangeAttackComponent.UpdateAmmo(weapon);
        for (int i = 0; i < 10; i++)
        {
            Assert.That(bot.rangeAttackComponent.UpdateAmmo(weapon), Is.SameAs(ammo));
            bot.rangeAttackComponent.RemoveEnduranceAndAmmoOnShot();
        }
        Assert.That(bot.Endurance, Is.EqualTo(50));
        Assert.That(bot.Inventory.AllItems.Single(), Is.SameAs(weapon));
        Assert.That(weapon.Count, Is.EqualTo(1));
    }

    [Test]
    public void MalformedWeaponIsRejectedAndSameTargetDoesNotRestartAim()
    {
        var item = GameInventoryItem.Create(new DbItemTemplate { Object_Type = (int)eObjectType.Thrown, Item_Type = Slot.RANGED });
        Assert.That(BotRangedCombat.IsUsableWeapon(item), Is.False);
        var target = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
        Assert.That(BotRangedCombat.NeedsAttackStart(true, target, target), Is.False);
        Assert.That(BotRangedCombat.NeedsAttackStart(false, target, target), Is.True);
    }

    [Test]
    public void MeleeFirstClassesNeverSelectRangedWhileHunterRetainsBowPolicy()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Savage, eBotStance.Auto, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Savage, eBotStance.Ranged, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Berserker, eBotStance.Auto, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Berserker, eBotStance.Ranged, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Berserker, eBotStance.Auto, true, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Shadowblade, eBotStance.Auto, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Shadowblade, eBotStance.Ranged, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Warrior, eBotStance.Auto, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Warrior, eBotStance.Ranged, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Thane, eBotStance.Ranged, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Shaman, eBotStance.Ranged, false, true), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Scout, eBotStance.Auto, false, true), Is.True);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Hunter, eBotStance.Ranged, false, true), Is.True);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Hunter, eBotStance.Auto, false, true), Is.True);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(
                eCharacterClass.Ranger, eBotStance.Auto, false, true), Is.True);
        });
    }

    [Test]
    public void InstantHunterSpellDoesNotCancelAnExistingBowDraw()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.PreserveDrawAfterInstantSpell(true, false, false,
                eActiveWeaponSlot.Distance, eRangedAttackState.Aim), Is.True);
            Assert.That(BotRangedCombat.PreserveDrawAfterInstantSpell(true, true, false,
                eActiveWeaponSlot.Distance, eRangedAttackState.Aim), Is.False);
            Assert.That(BotRangedCombat.PreserveDrawAfterInstantSpell(true, false, false,
                eActiveWeaponSlot.Standard, eRangedAttackState.None), Is.False);
        });
    }

    [Test]
    public void DedicatedArcherBowCycleOwnsActionUntilRelease()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.BowCycleOwnsAction(eCharacterClass.Hunter, true,
                eActiveWeaponSlot.Distance, eRangedAttackState.Aim), Is.True);
            Assert.That(BotRangedCombat.BowCycleOwnsAction(eCharacterClass.Scout, true,
                eActiveWeaponSlot.Distance, eRangedAttackState.Aim), Is.True);
            Assert.That(BotRangedCombat.BowCycleOwnsAction(eCharacterClass.Ranger, true,
                eActiveWeaponSlot.Distance, eRangedAttackState.Aim), Is.True);
            Assert.That(BotRangedCombat.BowCycleOwnsAction(eCharacterClass.Hunter, true,
                eActiveWeaponSlot.Distance, eRangedAttackState.None), Is.True,
                "The queued NPC attack owns the pre-Aim gap even when a loaded server has not serviced it yet.");
            Assert.That(BotRangedCombat.BowCycleOwnsAction(eCharacterClass.Hunter, false,
                eActiveWeaponSlot.Distance, eRangedAttackState.Aim), Is.False);
            Assert.That(BotRangedCombat.BowCycleOwnsAction(eCharacterClass.Warrior, true,
                eActiveWeaponSlot.Distance, eRangedAttackState.Aim), Is.False);
        });
    }

    [TestCase(eCharacterClass.Scout)]
    [TestCase(eCharacterClass.Hunter)]
    [TestCase(eCharacterClass.Ranger)]
    public void DedicatedArchersFallBackToMeleeWithoutShotEndurance(eCharacterClass characterClass)
    {
        Assert.That(BotRangedCombat.ShouldUseRangedWeapon(characterClass,
            eBotStance.Auto, false, hasUsableRangedWeapon: false), Is.False);
    }

    [Test]
    public void NativeNpcRangedFallbackCannotOverrideMeleeGameBotPolicy()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.AllowsAutomaticNpcRangedSwitch(
                true, eCharacterClass.Shadowblade, true), Is.False);
            Assert.That(BotRangedCombat.AllowsAutomaticNpcRangedSwitch(
                true, eCharacterClass.Warrior, true), Is.False);
            Assert.That(BotRangedCombat.AllowsAutomaticNpcRangedSwitch(
                true, eCharacterClass.Hunter, true), Is.True);
            Assert.That(BotRangedCombat.AllowsAutomaticNpcRangedSwitch(
                true, eCharacterClass.Ranger, false), Is.False);
            Assert.That(BotRangedCombat.AllowsAutomaticNpcRangedSwitch(
                false, eCharacterClass.Unknown, false), Is.True);
        });
    }

    [Test]
    public void HunterClosesOnlyUntilItsBowCanFire()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.ShouldCloseToRangedRange(true, 2000, 1600), Is.True);
            Assert.That(BotRangedCombat.ShouldCloseToRangedRange(true, 1500, 1600), Is.False);
            Assert.That(BotRangedCombat.ShouldCloseToRangedRange(false, 2000, 1600), Is.False);
            Assert.That(BotRangedCombat.BowApproachDistance(125, 1600), Is.EqualTo(1500));
        });
    }

    [Test]
    public void OnlyPersistentAutonomousArchersOwnBowPositioning()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.UsesAutonomousBowPositioning(true, false, false, eCharacterClass.Hunter), Is.True);
            Assert.That(BotRangedCombat.UsesAutonomousBowPositioning(true, false, false, eCharacterClass.Scout), Is.True);
            Assert.That(BotRangedCombat.UsesAutonomousBowPositioning(true, false, false, eCharacterClass.Ranger), Is.True);
            Assert.That(BotRangedCombat.UsesAutonomousBowPositioning(true, false, true, eCharacterClass.Hunter), Is.True,
                "A persistent Hunter keeps bow positioning when temporarily led by a player.");
            Assert.That(BotRangedCombat.UsesAutonomousBowPositioning(true, true, true, eCharacterClass.Hunter), Is.False,
                "The already-working companion/player-led movement path remains unchanged.");
            Assert.That(BotRangedCombat.UsesAutonomousBowPositioning(true, false, false, eCharacterClass.Warrior), Is.False);
        });
    }

    [Test]
    public void ActiveBowDrawSurvivesBrainPulsesUntilArrowRelease()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.BowDrawOwnsDecision(eCharacterClass.Hunter, true,
                eActiveWeaponSlot.Distance, true, true, false, false), Is.True);
            Assert.That(BotRangedCombat.BowDrawOwnsDecision(eCharacterClass.Hunter, true,
                eActiveWeaponSlot.Distance, true, false, false, false), Is.False,
                "A target that really left bow range must still be chased.");
            Assert.That(BotRangedCombat.BowDrawOwnsDecision(eCharacterClass.Hunter, true,
                eActiveWeaponSlot.Distance, true, true, true, false), Is.False,
                "Close incoming damage must still permit melee defense.");
            Assert.That(BotRangedCombat.BowDrawOwnsDecision(eCharacterClass.Warrior, true,
                eActiveWeaponSlot.Distance, true, true, false, false), Is.False);
        });
    }

    [Test]
    public void HunterPetMaintenanceCannotResetAnActiveCombatBowCycle()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotRangedCombat.CombatOwnsArcherPetUpkeep(
                eCharacterClass.Hunter, true, false, false, false), Is.True,
                "The pet reaching a target first releases the Hunter to shoot instead of buffing the pet.");
            Assert.That(BotRangedCombat.CombatOwnsArcherPetUpkeep(
                eCharacterClass.Hunter, false, false, false, true), Is.True,
                "An active bow draw owns the Hunter action through release.");
            Assert.That(BotRangedCombat.CombatOwnsArcherPetUpkeep(
                eCharacterClass.Hunter, false, false, false, false), Is.False,
                "Pet upkeep resumes after combat.");
            Assert.That(BotRangedCombat.CombatOwnsArcherPetUpkeep(
                eCharacterClass.Spiritmaster, true, true, true, false), Is.False,
                "Caster-pet behavior is outside this Hunter-specific correction.");
        });
    }

    [Test]
    public void RepeatingTheAlreadyActiveBowSlotDoesNotCancelItsDraw()
    {
        var bot = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
        var weapon = GameInventoryItem.Create(new DbItemUnique(
            BotRangedCombat.CreateStarter(eRealm.Midgard, eCharacterClass.Hunter, eObjectType.CompositeBow)));
        bot.PrimeActiveDistanceWeapon(weapon);
        bot.attackComponent.AttackState = true;
        bot.rangeAttackComponent.RangedAttackState = eRangedAttackState.Aim;

        bot.SwitchWeapon(eActiveWeaponSlot.Distance);

        Assert.That(bot.attackComponent.AttackState, Is.True);
        Assert.That(bot.rangeAttackComponent.RangedAttackState, Is.EqualTo(eRangedAttackState.Aim));
        Assert.That(bot.ActiveWeapon, Is.SameAs(weapon));
    }

    [Test]
    public void InventorySavePersistsUniqueWeaponRelation()
    {
        string path = Path.Combine(Path.GetTempPath(), "daoc-ranged-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            var db = new SqliteObjectDatabase($"Data Source={path};Version=3;Pooling=False;");
            db.RegisterDataObject(typeof(DbItemTemplate));
            db.RegisterDataObject(typeof(DbItemUnique));
            db.RegisterDataObject(typeof(DbInventoryItem));
            var item = GameInventoryItem.Create(new DbItemUnique(BotRangedCombat.CreateThrowingWeapon(eRealm.Midgard, eCharacterClass.Warrior, 1)));
            item.OwnerID = "ranged-test"; item.SlotPosition = Slot.RANGED;
            Assert.That(db.AddObject(item), Is.True);
            Assert.That(db.SelectAllObjects<DbItemUnique>().Single().SPD_ABS, Is.EqualTo(20));
            Assert.That(db.SelectAllObjects<DbInventoryItem>().Single().Template.Model, Is.EqualTo(333));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
