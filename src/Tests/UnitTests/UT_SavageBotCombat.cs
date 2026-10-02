using DOL.GS;
using DOL.GS.Styles;
using DOL.GS.ServerRules;
using DOL.Database;
using DOL.GS.PlayerClass;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DOL.UnitTests.Gameserver;

[TestFixture, NonParallelizable]
public class UT_SavageBotCombat
{
    private sealed class WeaponTestServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    private static readonly IObjectDatabase EmptyDatabase =
        DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    private GameServer _previousServer;
    private PetTestLanguageScope _language;

    [SetUp]
    public void SetUp()
    {
        _previousServer = GameServer.Instance;
        _language = new PetTestLanguageScope();
        GameServer.LoadTestDouble((WeaponTestServer)RuntimeHelpers.GetUninitializedObject(typeof(WeaponTestServer)));
    }

    [TearDown]
    public void TearDown()
    {
        GameServer.LoadTestDouble(_previousServer);
        _language.Dispose();
    }

    [Test]
    public void InstantSavageBuffContinuesMeleeButARealCastDoesNot()
    {
        var instant = new Spell(new DbSpell
        {
            Type = nameof(eSpellType.SavageDPSBuff),
            Target = "Self",
            CastTime = 0,
        }, 1);
        var castTime = new Spell(new DbSpell
        {
            Type = nameof(eSpellType.SavageDPSBuff),
            Target = "Self",
            CastTime = 2,
        }, 1);

        Assert.Multiple(() =>
        {
            Assert.That(SavageBotCombatPolicy.MayAttackDuringActiveCast(eCharacterClass.Savage, instant), Is.True,
                "The early combat cast guard must permit the native instant buff to reach melee");
            Assert.That(SavageBotCombatPolicy.MayAttackDuringActiveCast(eCharacterClass.Savage, castTime), Is.False);
            Assert.That(SavageBotCombatPolicy.MayAttackDuringActiveCast(eCharacterClass.Berserker, instant), Is.False);
            Assert.That(SavageBotCombatPolicy.ContinueMeleeAfterSpell(
                eCharacterClass.Savage, true, false, null, true, instant), Is.True,
                "A queued instant health-cost buff must not cancel the pull or swing");
            Assert.That(SavageBotCombatPolicy.ContinueMeleeAfterSpell(
                eCharacterClass.Savage, true, true, instant, false, null), Is.True,
                "The casting service's brief active-handler phase is still instant");
            Assert.That(SavageBotCombatPolicy.ContinueMeleeAfterSpell(
                eCharacterClass.Savage, true, true, castTime, false, null), Is.False);
            Assert.That(SavageBotCombatPolicy.ContinueMeleeAfterSpell(
                eCharacterClass.Savage, true, false, null, true, null), Is.False,
                "An unknown queued ability must retain the safe generic behavior");
            Assert.That(SavageBotCombatPolicy.ContinueMeleeAfterSpell(
                eCharacterClass.Berserker, true, false, null, true, instant), Is.False);
        });
    }

    [Test]
    public void SavageUsesOffensiveStaplesBeforeDefensiveHealthTrades()
    {
        Assert.That(SavageBotCombatPolicy.BuffPriority(eSpellType.SavageDPSBuff), Is.EqualTo(0));
        Assert.That(SavageBotCombatPolicy.BuffPriority(eSpellType.SavageCombatSpeedBuff), Is.EqualTo(1));
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 70, 0), Is.True);
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageEvadeBuff, 70, 1), Is.False);
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageEvadeBuff, 90, 2), Is.True);
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 50, 0), Is.False);
    }

    [Test]
    public void SavageTradesHealthForEnduranceOnlyWithASafeReserve()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SavageBotCombatPolicy.ShouldUseEnduranceHeal(90, 20), Is.True);
            Assert.That(SavageBotCombatPolicy.ShouldUseEnduranceHeal(75, 30), Is.True);
            Assert.That(SavageBotCombatPolicy.ShouldUseEnduranceHeal(74, 20), Is.False,
                "The bot must not compound a dangerous health deficit");
            Assert.That(SavageBotCombatPolicy.ShouldUseEnduranceHeal(90, 31), Is.False,
                "Ordinary rest recovery remains preferable when endurance is not critically low");
        });
    }

    [Test]
    public void PersistedWeaponLineDoesNotRerollOnRestart()
    {
        Assert.That(SavageBotSpec.WeaponFromPersistedSpecs(
            $"{Specs.Savagery}|12;{Specs.Sword}|9;{Specs.Parry}|2"), Is.EqualTo(eObjectType.Sword));
        Assert.That(SavageBotSpec.WeaponFromPersistedSpecs(
            $"{Specs.Axe}|3;{Specs.HandToHand}|11"), Is.EqualTo(eObjectType.HandToHand));
    }

    [Test]
    public void BaselineOrTiedWeaponLinesDoNotOverrideTheSavedSavagePlan()
    {
        var saved = new SavageBotSpec(eSpecType.DualWield, eObjectType.HandToHand);
        string baseline = $"{Specs.Axe}|1;{Specs.Hammer}|1;{Specs.Sword}|1;{Specs.HandToHand}|1";
        string tied = $"{Specs.Axe}|8;{Specs.Hammer}|8;{Specs.Sword}|1;{Specs.HandToHand}|1";

        Assert.That(SavageBotSpec.WeaponFromPersistedSpecs(baseline), Is.EqualTo((eObjectType)0));
        Assert.That(SavageBotSpec.AlignWithPersistedSpecs(saved, baseline), Is.False);
        Assert.That(saved.WeaponOneType, Is.EqualTo(eObjectType.HandToHand));
        Assert.That(saved.SpecType, Is.EqualTo(eSpecType.DualWield));
        Assert.That(SavageBotSpec.WeaponFromPersistedSpecs(tied), Is.EqualTo((eObjectType)0));
        Assert.That(SavageBotSpec.AlignWithPersistedSpecs(saved, tied), Is.False);
        Assert.That(saved.WeaponOneType, Is.EqualTo(eObjectType.HandToHand));
    }

    [Test]
    public void SavedSavageBuildCannotOverrideTheWeaponLineActuallyTrained()
    {
        var restored = new SavageBotSpec(eSpecType.TwoHanded, eObjectType.Hammer);
        var obsolete = new SavageBotSpec(eSpecType.TwoHanded, eObjectType.Axe);
        Assert.That(BotLifetimeBuild.Restore(restored, BotLifetimeBuild.Encode(obsolete)), Is.True);
        Assert.That(restored.WeaponOneType, Is.EqualTo(eObjectType.Axe));

        Assert.That(SavageBotSpec.AlignWithPersistedSpecs(restored,
            $"{Specs.Hammer}|9;{Specs.Savagery}|5"), Is.True);
        Assert.That(restored.WeaponOneType, Is.EqualTo(eObjectType.Hammer));
        Assert.That(restored.SpecType, Is.EqualTo(eSpecType.Mid));
        Assert.That(restored.Is2H, Is.True);
        Assert.That(restored.SpecLines.Exists(line => line.Spec == Specs.Hammer), Is.True);
        Assert.That(restored.SpecLines.Exists(line => line.Spec == Specs.Axe), Is.False);
        Assert.That(SavageBotSpec.AlignWithPersistedSpecs(restored,
            $"{Specs.Hammer}|9;{Specs.Savagery}|5"), Is.False);
    }

    [Test]
    public void TrainedHammerSavageUsesStrongerOneHandUntilARealTwoHandUpgradeExists()
    {
        var bot = WeaponBot(eObjectType.Hammer, 5);
        bot.Inventory.AddItem(eInventorySlot.RightHandWeapon, Weapon(eObjectType.Hammer,
            eInventorySlot.RightHandWeapon, 27, 5));
        bot.Inventory.AddItem(eInventorySlot.TwoHandWeapon, Weapon(eObjectType.Hammer,
            eInventorySlot.TwoHandWeapon, 20, 3));

        Assert.That(SavageBotWeaponPolicy.TryBestActiveSlot(bot, out eActiveWeaponSlot slot), Is.True);
        Assert.That(slot, Is.EqualTo(eActiveWeaponSlot.Standard));
        Assert.That(SavageBotWeaponPolicy.CombatScore(bot,
            Weapon(eObjectType.HandToHand, eInventorySlot.RightHandWeapon, 50, 5),
            eInventorySlot.RightHandWeapon), Is.Zero, "An untrained H2H item cannot win by raw DPS");

        DbInventoryItem bronzeGreatHammer = Weapon(eObjectType.Hammer,
            eInventorySlot.TwoHandWeapon, 27, 5);
        Assert.That(SavageBotWeaponPolicy.IsUpgrade(bot, bronzeGreatHammer,
            eInventorySlot.TwoHandWeapon), Is.True);
        bot.Inventory.AddItem(eInventorySlot.FirstBackpack, bronzeGreatHammer);
        bot.Inventory.MoveItem(eInventorySlot.FirstBackpack, eInventorySlot.TwoHandWeapon, 1);
        Assert.That(SavageBotWeaponPolicy.TryBestActiveSlot(bot, out slot), Is.True);
        Assert.That(slot, Is.EqualTo(eActiveWeaponSlot.TwoHanded));
    }

    [Test]
    public void SmallVendorValueGainStillBuysARealSavageWeaponDpsUpgrade()
    {
        var savage = WeaponBot(eObjectType.Hammer, 5);
        var old = Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 20, 3, 100);
        old.Bonus1 = 2;
        var replacement = Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 27, 5, 80);
        savage.Inventory.AddItem(eInventorySlot.TwoHandWeapon, old);
        Assert.That(AutonomousBotEconomy.EquipmentValue(replacement) -
            AutonomousBotEconomy.EquipmentValue(old), Is.LessThanOrEqualTo(8));
        Assert.That(AutonomousBotEconomy.TryGetEquipmentUpgrade(savage, replacement, out var slot), Is.True);
        Assert.That(slot, Is.EqualTo(eInventorySlot.TwoHandWeapon));

        var warrior = (WarriorWeaponBot)RuntimeHelpers.GetUninitializedObject(typeof(WarriorWeaponBot));
        warrior.Level = 5;
        warrior.Inventory = new BotInventory();
        var warriorOld = Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 20, 3, 100);
        warriorOld.Bonus1 = 2;
        warrior.Inventory.AddItem(eInventorySlot.TwoHandWeapon, warriorOld);
        SetPlan(warrior, new BotSpec { WeaponOneType = eObjectType.Hammer });
        Assert.That(AutonomousBotEconomy.TryGetEquipmentUpgrade(warrior, replacement, out _), Is.False,
            "Other classes retain the ordinary equipment-value threshold");
    }

    [Test]
    public void HandToHandSavageKeepsItsPairedWeaponsAndRejectsUntrainedHammer()
    {
        var bot = WeaponBot(eObjectType.HandToHand, 5);
        bot.Inventory.AddItem(eInventorySlot.RightHandWeapon, Weapon(eObjectType.HandToHand,
            eInventorySlot.RightHandWeapon, 20, 3));
        bot.Inventory.AddItem(eInventorySlot.LeftHandWeapon, Weapon(eObjectType.HandToHand,
            eInventorySlot.LeftHandWeapon, 20, 3));
        bot.Inventory.AddItem(eInventorySlot.TwoHandWeapon, Weapon(eObjectType.Hammer,
            eInventorySlot.TwoHandWeapon, 50, 5));

        Assert.That(SavageBotWeaponPolicy.CombatScore(bot,
            bot.Inventory.GetItem(eInventorySlot.LeftHandWeapon), eInventorySlot.LeftHandWeapon), Is.GreaterThan(0));
        Assert.That(SavageBotWeaponPolicy.TryBestActiveSlot(bot, out eActiveWeaponSlot slot), Is.True);
        Assert.That(slot, Is.EqualTo(eActiveWeaponSlot.Standard));
        Assert.That(SavageBotWeaponPolicy.IsUpgrade(bot,
            Weapon(eObjectType.HandToHand, eInventorySlot.LeftHandWeapon, 27, 5),
            eInventorySlot.LeftHandWeapon), Is.True);
    }

    // Generated claws are always Hand=2 ("usable in left hand"), including the
    // one a level-1 Savage wears in its main hand. The native inventory accepts
    // them there, so the Savage scorer must too. (This used to be rejected, which
    // left every claw-only level-1 Savage with "no legal melee weapon".)
    [Test]
    public void GeneratedEitherHandClawIsAValidSavageMainhand()
    {
        var bot = WeaponBot(eObjectType.HandToHand, 1);
        var mainClaw = Weapon(eObjectType.HandToHand, eInventorySlot.RightHandWeapon, 15, 1);
        mainClaw.Hand = 2;
        var offClaw = Weapon(eObjectType.HandToHand, eInventorySlot.LeftHandWeapon, 15, 1);
        offClaw.Hand = 2;
        bot.Inventory.AddItem(eInventorySlot.RightHandWeapon, mainClaw);
        bot.Inventory.AddItem(eInventorySlot.LeftHandWeapon, offClaw);

        Assert.Multiple(() =>
        {
            Assert.That(SavageBotWeaponPolicy.CombatScore(bot, mainClaw,
                eInventorySlot.RightHandWeapon), Is.GreaterThan(0));
            Assert.That(SavageBotWeaponPolicy.CombatScore(bot, offClaw,
                eInventorySlot.LeftHandWeapon), Is.GreaterThan(0));
            Assert.That(SavageBotWeaponPolicy.TryBestActiveSlot(bot, out var slot), Is.True);
            Assert.That(slot, Is.EqualTo(eActiveWeaponSlot.Standard));
        });
    }

    [Test]
    public void NonClawLeftHandWeaponStillCannotBeTheSavageMainhand()
    {
        var bot = WeaponBot(eObjectType.Sword, 5);
        var offhandSword = Weapon(eObjectType.Sword, eInventorySlot.LeftHandWeapon, 27, 5);
        offhandSword.Hand = 2;
        Assert.That(SavageBotWeaponPolicy.CombatScore(bot, offhandSword,
            eInventorySlot.RightHandWeapon), Is.Zero);
    }

    [Test]
    public void SavagePurchaseRanksCombatGainAboveGenericItemValue()
    {
        var bot = WeaponBot(eObjectType.Hammer, 5);
        bot.Inventory.AddItem(eInventorySlot.TwoHandWeapon,
            Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 20, 3, 80));
        var stronger = Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 27, 5, 95);
        var highBonus = Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 24, 5, 90);
        highBonus.Bonus1 = 100;
        Assert.That(AutonomousBotEconomy.EquipmentValue(highBonus),
            Is.GreaterThan(AutonomousBotEconomy.EquipmentValue(stronger)));

        var scorePurchase = typeof(AutonomousBotEconomy).GetMethod("ScorePurchase",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var strongerPurchase = (AutonomousBotEconomy.PurchaseCandidate)scorePurchase.Invoke(
            null, new object[] { bot, stronger, false })!;
        var highBonusPurchase = (AutonomousBotEconomy.PurchaseCandidate)scorePurchase.Invoke(
            null, new object[] { bot, highBonus, false })!;

        Assert.That(strongerPurchase, Is.Not.Null);
        Assert.That(highBonusPurchase, Is.Not.Null);
        Assert.That(strongerPurchase.Utility, Is.GreaterThan(highBonusPurchase.Utility));
        Assert.That(SavageBotWeaponPolicy.UpgradeGain(bot, stronger,
            eInventorySlot.TwoHandWeapon), Is.GreaterThan(
            SavageBotWeaponPolicy.UpgradeGain(bot, highBonus, eInventorySlot.TwoHandWeapon)));
    }

    [Test]
    public void RealmExchangeWeaponUpgradeRefreshesTheSavagesActiveItem()
    {
        var bot = (ExchangeSavageBot)RuntimeHelpers.GetUninitializedObject(typeof(ExchangeSavageBot));
        bot.Level = 5;
        bot.Inventory = new BotInventory();
        SetPlan(bot, new SavageBotSpec(eSpecType.TwoHanded, eObjectType.Hammer));
        DbInventoryItem old = Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 20, 3);
        DbInventoryItem upgrade = Weapon(eObjectType.Hammer, eInventorySlot.TwoHandWeapon, 27, 5);
        bot.Inventory.AddItem(eInventorySlot.TwoHandWeapon, old);
        bot.Inventory.AddItem(eInventorySlot.FirstBackpack, upgrade);
        bot.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
        Assert.That(bot.ActiveWeapon, Is.SameAs(old));

        var purchase = new AutonomousBotEconomy.PurchaseCandidate(
            upgrade, 1_000, eInventorySlot.TwoHandWeapon, true, false);
        Assert.That(AutonomousBotEconomy.TryEquipPurchasedUpgrade(bot, purchase), Is.True);
        Assert.That(bot.ActiveWeapon, Is.SameAs(upgrade));
        Assert.That(bot.Inventory.GetItem(eInventorySlot.FirstBackpack), Is.SameAs(old));
    }

    private static SavageWeaponBot WeaponBot(eObjectType type, byte level)
    {
        var bot = (SavageWeaponBot)RuntimeHelpers.GetUninitializedObject(typeof(SavageWeaponBot));
        bot.Level = level;
        bot.Inventory = new BotInventory();
        SetPlan(bot, new SavageBotSpec(type == eObjectType.HandToHand
            ? eSpecType.DualWield : eSpecType.TwoHanded, type));
        return bot;
    }

    private static void SetPlan(GameBot bot, BotSpec plan) =>
        typeof(GameBot).GetProperty(nameof(GameBot.BotSpec),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(bot, plan);

    private static DbInventoryItem Weapon(eObjectType type, eInventorySlot slot,
        int dps, int level, int quality = 89)
    {
        var template = new DbItemTemplate
        {
            Id_nb = Guid.NewGuid().ToString(), Name = "Savage test weapon", Level = level,
            Object_Type = (int)type, Item_Type = (int)slot,
            Hand = slot == eInventorySlot.TwoHandWeapon ? 1 : 0,
            DPS_AF = dps, SPD_ABS = slot == eInventorySlot.TwoHandWeapon ? 40 : 30,
            Quality = quality, Condition = 50000, MaxCondition = 50000,
            Durability = 50000, MaxDurability = 50000, AllowedClasses = "0"
        };
        return GameInventoryItem.Create(template);
    }

    private sealed class SavageWeaponBot : GameBot
    {
        private SavageWeaponBot() : base((OfflineWorldBotRecord)null) { }
        public override byte Level { get; set; }
        public override ICharacterClass CharacterClass => new ClassSavage();
        public override bool HasAbility(string keyName) => true;
        public override bool HasAbilityToUseItem(DbItemTemplate item) => true;
        public override int WeaponSpecLevel(eObjectType type, int slotPosition) => 5;
    }

    private sealed class WarriorWeaponBot : GameBot
    {
        private WarriorWeaponBot() : base((OfflineWorldBotRecord)null) { }
        public override byte Level { get; set; }
        public override ICharacterClass CharacterClass => new ClassWarrior();
        public override bool HasAbilityToUseItem(DbItemTemplate item) => true;
    }

    private sealed class ExchangeSavageBot : GameBot
    {
        private ExchangeSavageBot() : base((OfflineWorldBotRecord)null) { }
        private DbInventoryItem _selectedWeapon;
        private eActiveWeaponSlot _selectedSlot;
        public override byte Level { get; set; }
        public override ICharacterClass CharacterClass => new ClassSavage();
        public override bool HasAbilityToUseItem(DbItemTemplate item) => true;
        public override int WeaponSpecLevel(eObjectType type, int slotPosition) => 5;
        public override void RefreshItemBonuses() { }
        public override DbInventoryItem ActiveWeapon => _selectedWeapon;
        public override eActiveWeaponSlot ActiveWeaponSlot => _selectedSlot;
        public override void SwitchWeapon(eActiveWeaponSlot slot)
        {
            _selectedSlot = slot;
            _selectedWeapon = Inventory.GetItem(slot == eActiveWeaponSlot.TwoHanded
                ? eInventorySlot.TwoHandWeapon : eInventorySlot.RightHandWeapon);
        }
    }

    [Test]
    public void SavageStylePolicyPrefersRealDamageAndRecognizesAnytimeFallbacks()
    {
        var utility = new Style(new DbStyle { OpeningRequirementType = 0, OpeningRequirementValue = 0,
            AttackResultRequirement = 0, GrowthRate = 0, SpecLevelRequirement = 6 }, null);
        var damaging = new Style(new DbStyle { OpeningRequirementType = 0, OpeningRequirementValue = 0,
            AttackResultRequirement = 0, GrowthRate = 0.57, SpecLevelRequirement = 2 }, null);
        var positional = new Style(new DbStyle { OpeningRequirementType = 2, OpeningRequirementValue = 0,
            AttackResultRequirement = 0, GrowthRate = 1.1, SpecLevelRequirement = 8 }, null);

        Assert.Multiple(() =>
        {
            Assert.That(SavageBotCombatPolicy.StylePriority(damaging), Is.GreaterThan(SavageBotCombatPolicy.StylePriority(utility)));
            Assert.That(SavageBotCombatPolicy.IsReliableAnytimeStyle(damaging), Is.True);
            Assert.That(SavageBotCombatPolicy.IsReliableAnytimeStyle(positional), Is.False);
            Assert.That(SavageBotCombatPolicy.NeedsWeaponTrainingRepair(5, 9), Is.True);
            Assert.That(SavageBotCombatPolicy.NeedsWeaponTrainingRepair(9, 7), Is.False);
        });
    }

    [Test]
    public void SavageNeverSelectsTheAutomaticRangedCombatPath()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SavageBotCombatPolicy.MustMeleePull(eCharacterClass.Savage), Is.True);
            Assert.That(SavageBotCombatPolicy.MustMeleePull(eCharacterClass.Berserker), Is.False);
            Assert.That(BotRangedCombat.IsDedicatedArcher(eCharacterClass.Savage), Is.False);
            Assert.That(BotRangedCombat.ShouldUseRangedWeapon(eCharacterClass.Savage,
                eBotStance.Auto, false, true), Is.False);
            Assert.That(BotRangedCombat.AllowsAutomaticNpcRangedSwitch(
                true, eCharacterClass.Savage, true), Is.False);
        });
    }

    [Test]
    public void UntouchedSavageMeleePullRetriesThenReleasesItsTarget()
    {
        var origin = new Vector3(100, 200, 300);
        Assert.Multiple(() =>
        {
            Assert.That(SavageBotCombatPolicy.MadeMeleePullProgress(origin,
                origin + new Vector3(63, 0, 0), 500, 437), Is.False);
            Assert.That(SavageBotCombatPolicy.MadeMeleePullProgress(origin,
                origin + new Vector3(64, 0, 0), 500, 500), Is.True);
            Assert.That(SavageBotCombatPolicy.MadeMeleePullProgress(origin,
                origin, 500, 436), Is.True);
            Assert.That(SavageBotCombatPolicy.EvaluateMeleePull(8_999, 1_000, 1_000, false),
                Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.Continue));
            Assert.That(SavageBotCombatPolicy.EvaluateMeleePull(9_000, 1_000, 1_000, false),
                Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.RetryApproach));
            Assert.That(SavageBotCombatPolicy.EvaluateMeleePull(20_999, 1_000, 1_000, true),
                Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.Continue));
            Assert.That(SavageBotCombatPolicy.EvaluateMeleePull(21_000, 1_000, 1_000, true),
                Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.GiveUp));
            Assert.That(SavageBotCombatPolicy.EvaluateMeleePull(21_000, 1_000, 20_000, false),
                Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.Continue),
                "Actual movement resets the stall clock");
            Assert.That(SavageBotCombatPolicy.EvaluateMeleePull(61_000, 1_000, 60_000, false),
                Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.GiveUp),
                "Even a moving pull cannot remain pending indefinitely");
        });
    }

    [Test]
    public void MeleePullStopsTrackingDeadOrMissingTargetsAndProtectsRealCombat()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SavageBotCombatPolicy.IsMeleePullTargetValid(true, true, true, true), Is.True);
            Assert.That(SavageBotCombatPolicy.IsMeleePullTargetValid(false, true, true, true), Is.False);
            Assert.That(SavageBotCombatPolicy.IsMeleePullTargetValid(true, false, true, true), Is.False);
            Assert.That(SavageBotCombatPolicy.IsMeleePullTargetValid(true, true, false, true), Is.False);
            Assert.That(SavageBotCombatPolicy.IsMeleePullTargetValid(true, true, true, false), Is.False);
            Assert.That(SavageBotCombatPolicy.HasMeleePullContact(10, 10, 20, 20, 100, 100), Is.False);
            Assert.That(SavageBotCombatPolicy.HasMeleePullContact(10, 11, 20, 20, 100, 100), Is.True,
                "A real melee swing may miss yet must not be cancelled as a stalled pull");
            Assert.That(SavageBotCombatPolicy.HasMeleePullContact(10, 10, 20, 21, 100, 100), Is.True);
            Assert.That(SavageBotCombatPolicy.HasMeleePullContact(10, 10, 20, 20, 100, 99), Is.True);
        });
    }

    [Test]
    public void SoloSavageUsesStrictPullProofOnlyWhereTheCampRequiresIt()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SavageBotCombatPolicy.NeedsVerifiedSoloPullRoute(
                eCharacterClass.Savage, false, false, false), Is.False,
                "A normal outdoor Savage should not be rejected by dungeon-grade route proof");
            Assert.That(SavageBotCombatPolicy.NeedsVerifiedSoloPullRoute(
                eCharacterClass.Savage, false, true, false), Is.True);
            Assert.That(SavageBotCombatPolicy.NeedsVerifiedSoloPullRoute(
                eCharacterClass.Savage, false, false, true), Is.True);
            Assert.That(SavageBotCombatPolicy.NeedsVerifiedSoloPullRoute(
                eCharacterClass.Savage, true, false, true), Is.False);
            Assert.That(SavageBotCombatPolicy.NeedsVerifiedSoloPullRoute(
                eCharacterClass.Berserker, false, true, true), Is.False);
        });
    }

    [Test]
    public void FailedSoloPullRouteIsCachedOnlyBrieflyWhileBothActorsStayPut()
    {
        Vector3 origin = new(554470, 562177, 5100);
        Vector3 target = new(555000, 562600, 5100);
        var failed = new SavageBotCombatPolicy.FailedSoloPullRoute(
            1_000, 100, 5, 5, origin, target);

        bool Delayed(long tick, ushort region, int originZone, int targetZone,
            Vector3 from, Vector3 to) =>
            SavageBotCombatPolicy.ShouldDelayFailedSoloPullRetry(
                failed, tick, region, originZone, targetZone, from, to);

        Assert.Multiple(() =>
        {
            Assert.That(Delayed(1_000, 100, 5, 5, origin, target), Is.True);
            Assert.That(Delayed(6_999, 100, 5, 5, origin, target), Is.True);
            Assert.That(Delayed(7_000, 100, 5, 5, origin, target), Is.False,
                "A stationary unreachable NPC gets a fresh route check after six seconds");
            Assert.That(Delayed(999, 100, 5, 5, origin, target), Is.False,
                "A clock reset cannot preserve stale failures");
            Assert.That(Delayed(2_000, 100, 5, 5, origin + new Vector3(500, 0, 0), target), Is.True,
                "An ordinary short camp patrol should not re-probe the same failed path each scan");
            Assert.That(Delayed(2_000, 100, 5, 5, origin + new Vector3(513, 0, 0), target), Is.False);
            Assert.That(Delayed(2_000, 100, 5, 5, origin, target + new Vector3(65, 0, 0)), Is.False);
            Assert.That(Delayed(2_000, 101, 5, 5, origin, target), Is.False);
            Assert.That(Delayed(2_000, 100, 6, 5, origin, target), Is.False);
            Assert.That(Delayed(2_000, 100, 5, 6, origin, target), Is.False);
        });
    }

    [Test]
    public void OnlyDanglingGameBotGeneratedUniqueRelationsAreReplaceable()
    {
        var broken = new DbInventoryItem
        {
            Creator = nameof(GameBot),
            UTemplate_Id = "missing_gamebot_unique",
            ITemplate_Id = null,
        };
        var valid = new DbInventoryItem
        {
            Creator = nameof(GameBot),
            UTemplate_Id = "valid_gamebot_unique",
            Template = new DbItemUnique { Id_nb = "valid_gamebot_unique" },
        };
        var loot = new DbInventoryItem
        {
            Creator = "loot",
            UTemplate_Id = "missing_loot_unique",
        };

        Assert.Multiple(() =>
        {
            Assert.That(BotWeaponStats.IsBrokenGeneratedFallback(broken), Is.True);
            Assert.That(BotWeaponStats.IsBrokenGeneratedFallback(valid), Is.False);
            Assert.That(BotWeaponStats.IsBrokenGeneratedFallback(loot), Is.False);
        });
    }
}
