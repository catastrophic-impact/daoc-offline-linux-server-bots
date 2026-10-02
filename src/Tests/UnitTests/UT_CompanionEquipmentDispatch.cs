using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.AI.Brain;
using DOL.GS;
using DOL.Logging;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_CompanionEquipmentDispatch
{
    private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    private sealed class Server : GameServer { protected override IObjectDatabase DataBaseImpl => Empty; }
    private sealed class Bot : GameBot
    {
        private Bot() : base((OfflineWorldBotRecord)null) { }
        public ICharacterClass Class;
        public override ICharacterClass CharacterClass => Class;
        public override byte Level { get; set; }
        public override eRealm Realm { get; set; }
        public eActiveWeaponSlot SelectedWeapon;
        public override DbInventoryItem ActiveWeapon => Inventory?.GetItem(SelectedWeapon switch {
            eActiveWeaponSlot.Distance => eInventorySlot.DistanceWeapon,
            eActiveWeaponSlot.TwoHanded => eInventorySlot.TwoHandWeapon,
            _ => eInventorySlot.RightHandWeapon });
        public override void SwitchWeapon(eActiveWeaponSlot slot) { SelectedWeapon = slot; }
    }
    private GameServer _previous;
    private PetTestLanguageScope _language;
    private Logger _logger;
    private object _previousQueue;
    private BlockingCollection<LogEntry> _quietEntries;
    [SetUp] public void Setup()
    {
        _previous = GameServer.Instance;
        _language = new PetTestLanguageScope();
        GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        _logger = (Logger)typeof(BotEquipment).GetField("log", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var queueField = typeof(Logger).GetField("_queueProcessor", BindingFlags.NonPublic | BindingFlags.Instance);
        _previousQueue = queueField.GetValue(_logger);
        var quietQueue = new LogEntryQueueProcessor();
        _quietEntries = new BlockingCollection<LogEntry>();
        typeof(LogEntryQueueProcessor).GetField("_loggingQueue", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(quietQueue, _quietEntries);
        queueField.SetValue(_logger, quietQueue); // No logger thread, disk output, or live server.
    }
    [TearDown] public void Cleanup()
    {
        typeof(Logger).GetField("_queueProcessor", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_logger, _previousQueue);
        _quietEntries.Dispose();
        _language.Dispose();
        GameServer.LoadTestDouble(_previous);
    }

    private static Bot Make(eRealm realm, eCharacterClass cls, byte level, bool temporary)
    {
        var bot = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
        bot.Level = level; bot.Realm = realm; bot.Name = cls.ToString(); bot.Inventory = new BotInventory();
        var type = typeof(GameBot).Assembly.GetTypes().First(t => !t.IsAbstract &&
            typeof(ICharacterClass).IsAssignableFrom(t) && t.GetCustomAttributes<CharacterClassAttribute>(false).Any(a => a.ID == (int)cls));
        bot.Class = (ICharacterClass)Activator.CreateInstance(type);
        typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(bot, temporary);
        return bot;
    }

    [TestCase(1)] [TestCase(20)] [TestCase(49)] [TestCase(50)]
    public void EmptyDatabaseStillEquipsEveryClassAndSpecialization(byte level)
    {
        foreach (var realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        foreach (var cls in AutonomousBotIdentityGenerator.GetEraClasses(realm))
        foreach (var specialization in BotSpec.GetSpecializationChoices(cls))
        {
            var bot = Make(realm, cls, level, true);
            typeof(GameBot).GetProperty(nameof(GameBot.BotSpec)).SetValue(bot, BotSpec.GetSpec(cls, specialization));
            typeof(GameBot).GetMethod("SetWeapons", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(bot, [true]);
            var main = bot.Inventory.GetItem(eInventorySlot.RightHandWeapon) ?? bot.Inventory.GetItem(eInventorySlot.TwoHandWeapon);
            Assert.That(main, Is.Not.Null, $"{cls} {specialization} {level}");
            Assert.That(main.Object_Type, Is.Not.EqualTo((int)eObjectType.Instrument), cls.ToString());
            Assert.That(main.Level, Is.EqualTo(level));
            foreach (var item in bot.Inventory.EquippedItems)
            {
                Assert.That(item.Level, Is.EqualTo(level), $"{cls} {item.Name}");
                if (BotWeaponStats.NeedsCompanionDamageStats((eObjectType)item.Object_Type))
                {
                    if (BotWeaponStats.IsMeleeWeapon((eObjectType)item.Object_Type))
                        Assert.That(BotWeaponStats.MatchesBuild(bot.BotSpec, (eObjectType)item.Object_Type), Is.True,
                            $"Generated weapon disagrees with {cls} {specialization} build: {item.Object_Type}");
                    Assert.That(BotWeaponStats.IsNormalCompanionWeapon(item.Template), Is.True, $"{cls} {specialization} {level} {item.Name}");
                    Assert.That(item.ConditionPercent, Is.EqualTo(100));
                    Assert.That(item.Model, Is.GreaterThan(0));
                }
            }
            var armor = realm switch {
                eRealm.Albion => GeneratedUniqueItem.GetAlbionArmorType(cls, level),
                eRealm.Hibernia => GeneratedUniqueItem.GetHiberniaArmorType(cls, level),
                _ => GeneratedUniqueItem.GetMidgardArmorType(cls, level) };
            BotEquipment.SetArmor(bot, armor);
            BotEquipment.SetJewelryROG(bot, realm, cls, level, eObjectType.Magical);
            foreach (var slot in new[] { eInventorySlot.HeadArmor, eInventorySlot.HandsArmor, eInventorySlot.FeetArmor,
                eInventorySlot.TorsoArmor, eInventorySlot.LegsArmor, eInventorySlot.ArmsArmor,
                eInventorySlot.Jewelry, eInventorySlot.Cloak, eInventorySlot.Neck, eInventorySlot.Waist,
                eInventorySlot.LeftBracer, eInventorySlot.RightBracer, eInventorySlot.LeftRing, eInventorySlot.RightRing })
                Assert.That(bot.Inventory.GetItem(slot), Is.Not.Null, $"{cls} {slot}");
        }
    }

    [Test]
    public void ShieldFallbackAndRepeatedRequestsPreserveTheLoadout()
    {
        var bot = Make(eRealm.Albion, eCharacterClass.Paladin, 20, true);
        BotEquipment.SetMeleeWeapon(bot, eObjectType.SlashingWeapon, eHand.oneHand);
        BotEquipment.SetShield(bot, 3);
        var weapon = bot.Inventory.GetItem(eInventorySlot.RightHandWeapon);
        var shield = bot.Inventory.GetItem(eInventorySlot.LeftHandWeapon);
        Assert.That(shield.Level, Is.EqualTo(20));
        Assert.That(shield.Type_Damage, Is.EqualTo(3));
        BotEquipment.SetMeleeWeapon(bot, eObjectType.SlashingWeapon, eHand.oneHand);
        BotEquipment.SetShield(bot, 3);
        Assert.That(bot.Inventory.GetItem(eInventorySlot.RightHandWeapon), Is.SameAs(weapon));
        Assert.That(bot.Inventory.GetItem(eInventorySlot.LeftHandWeapon), Is.SameAs(shield));
    }

    [Test]
    public void PersistentBotsDoNotGetAnyGeneratedFallbacks()
    {
        var bot = Make(eRealm.Albion, eCharacterClass.Paladin, 20, false);
        BotEquipment.SetMeleeWeapon(bot, eObjectType.SlashingWeapon, eHand.oneHand);
        BotEquipment.SetShield(bot, 3);
        BotEquipment.SetArmor(bot, eObjectType.Plate);
        Assert.That(bot.Inventory.AllItems, Is.Empty);
    }

    [Test]
    public void LastResortTemporaryWeaponUsesTheSameValidatedGenerator()
    {
        var bot = Make(eRealm.Hibernia, eCharacterClass.Champion, 1, true);
        BotEquipment.SetWeaponROG(bot, bot.Realm, eCharacterClass.Champion, 1,
            eObjectType.LargeWeapons, eInventorySlot.TwoHandWeapon, eDamageType.Slash);
        var weapon = bot.Inventory.GetItem(eInventorySlot.TwoHandWeapon);
        Assert.That(BotWeaponStats.IsNormalCompanionWeapon(weapon.Template), Is.True);
        Assert.That(weapon.ConditionPercent, Is.EqualTo(100));
        Assert.That(weapon.Hand, Is.EqualTo(1));
    }

    [Test]
    public void StackableThrowingAmmoIsNeverClonedAsAUniqueCompanionWeapon()
    {
        var stackable = new DbItemTemplate
        {
            Id_nb = "steel_balanced_throwing_axes",
            Name = "steel balanced throwing axes",
            Realm = (int)eRealm.Midgard,
            Level = 15,
            LevelRequirement = 0,
            Object_Type = (int)eObjectType.Thrown,
            Item_Type = Slot.RANGED,
            Type_Damage = (int)eDamageType.Slash,
            DPS_AF = BotWeaponStats.NormalDps(15),
            SPD_ABS = 20,
            Model = 333,
            Quality = 95,
            Condition = 50000,
            MaxCondition = 50000,
            Durability = 50000,
            MaxDurability = 50000,
            IsPickable = true,
            MaxCount = 100,
            PackSize = 20,
        };

        DbItemTemplate selected = BotEquipment.SelectCompanionWeapon(
            [stackable], 15, eRealm.Midgard, eCharacterClass.Berserker,
            eObjectType.Thrown, eInventorySlot.DistanceWeapon);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.TypeOf<DbItemUnique>());
            Assert.That(selected.Id_nb, Does.StartWith("bot_throwing_"));
            Assert.That(selected.MaxCount, Is.EqualTo(1));
            Assert.That(selected.Object_Type, Is.EqualTo((int)eObjectType.Thrown));
            Assert.That(BotRangedCombat.IsUsableTemplate(selected), Is.True);
        });
    }

    [TestCase(eRealm.Hibernia, eCharacterClass.Bard)]
    [TestCase(eRealm.Albion, eCharacterClass.Minstrel)]
    public void StarterKitIsCompleteIdempotentAndRotatesRealInstruments(eRealm realm, eCharacterClass cls)
    {
        var bot = Make(realm, cls, 1, false);
        Assert.That(BotStarterInstruments.Ensure(bot), Is.True);
        var original = bot.Inventory.AllItems.ToArray();
        Assert.That(original.Length, Is.EqualTo(3));
        Assert.That(BotStarterInstruments.Ensure(bot), Is.False);
        foreach (var type in new[] { eInstrumentType.Drum, eInstrumentType.Flute, eInstrumentType.Lute,
            eInstrumentType.Drum, eInstrumentType.Lute, eInstrumentType.Flute })
        {
            Assert.That((bool)typeof(BotBrain).GetMethod("TryEquipRealInstrument", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { bot, (int)type }), Is.True);
            Assert.That(bot.ActiveWeapon.DPS_AF, Is.EqualTo((int)type));
            Assert.That(bot.ActiveWeapon.Model, Is.EqualTo(BotStarterInstruments.ModelFor(type)));
            Assert.That(bot.ActiveWeapon.Level, Is.EqualTo(1));
            Assert.That(bot.ActiveWeapon.IsTradable, Is.False);
            Assert.That(bot.ActiveWeapon.IsDropable, Is.False);
        }
        Assert.That(bot.Inventory.AllItems, Is.EquivalentTo(original));
    }

    [Test] public void FullBagNeverLosesLootAndAnEmptyAlternateWeaponSlotCanBeUsed()
    {
        var bot = Make(eRealm.Hibernia, eCharacterClass.Bard, 1, false);
        BotStarterInstruments.Ensure(bot);
        var drum = bot.Inventory.AllItems.Single(item => item.DPS_AF == (int)eInstrumentType.Drum);
        var flute = bot.Inventory.AllItems.Single(item => item.DPS_AF == (int)eInstrumentType.Flute);
        var lute = bot.Inventory.AllItems.Single(item => item.DPS_AF == (int)eInstrumentType.Lute);
        bot.Inventory.RemoveItem(drum);
        bot.Inventory.RemoveItem(flute);
        bot.Inventory.MoveItem((eInventorySlot)lute.SlotPosition, eInventorySlot.TwoHandWeapon, lute.Count);
        // Drum already exists in a backpack; only the flute is missing.
        bot.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, drum);
        while (bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack) != eInventorySlot.Invalid)
            bot.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, GameInventoryItem.Create(new DbItemTemplate {
                Id_nb = Guid.NewGuid().ToString(), Name = "earned loot", MaxCount = 1 }));
        var loot = bot.Inventory.AllItems.ToArray();
        Assert.That(BotStarterInstruments.Ensure(bot), Is.True);
        Assert.That(bot.Inventory.GetItem(eInventorySlot.DistanceWeapon).DPS_AF, Is.EqualTo((int)eInstrumentType.Flute));
        foreach (var item in loot) Assert.That(bot.Inventory.AllItems.Contains(item), Is.True);
        Assert.That(BotStarterInstruments.Ensure(bot), Is.False);
    }

    [Test] public void SkaldsUseVocalChantsAndAreNotGivenUnusableInstruments()
    {
        var bot = Make(eRealm.Midgard, eCharacterClass.Skald, 1, false);
        Assert.That(BotStarterInstruments.Ensure(bot), Is.False);
        Assert.That(bot.Inventory.AllItems, Is.Empty);
    }

    [TestCase(eInstrumentType.Drum)] [TestCase(eInstrumentType.Flute)] [TestCase(eInstrumentType.Lute)]
    public void GeneratedInstrumentModelMatchesItsActualSubtype(eInstrumentType type)
    {
        var bot = Make(eRealm.Hibernia, eCharacterClass.Bard, 20, true);
        BotEquipment.SetInstrumentROG(bot, bot.Realm, eCharacterClass.Bard, bot.Level,
            eObjectType.Instrument, eInventorySlot.TwoHandWeapon, type);
        Assert.That(bot.Inventory.GetItem(eInventorySlot.TwoHandWeapon).Model, Is.EqualTo(BotStarterInstruments.ModelFor(type)));
    }
}
