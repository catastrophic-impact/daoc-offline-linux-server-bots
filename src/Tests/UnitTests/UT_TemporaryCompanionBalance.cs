using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_TemporaryCompanionBalance
    {
        [TestCase(1)] [TestCase(25)] [TestCase(49)]
        public void LevelingCompanionsFillSlotsButRetainArmorGearRange(int level)
        {
            Assert.That(TemporaryCompanionBalance.IsEndgame(true, level), Is.False);
            Assert.That(TemporaryCompanionBalance.EquipRegularSlot(true, level, 0.499), Is.True);
            Assert.That(TemporaryCompanionBalance.EquipRegularSlot(true, level, 0.999), Is.True);
            Assert.That(TemporaryCompanionBalance.EquipOffhand(true, level, true, 0.899), Is.True);
            Assert.That(TemporaryCompanionBalance.EquipOffhand(true, level, true, 0.999), Is.True);
            Assert.That(TemporaryCompanionBalance.EquipOffhand(true, level, false, 0), Is.False);
            Assert.That(TemporaryCompanionBalance.GearLevel(true, level, 1), Is.EqualTo(Math.Max(1, level - 10)));
            Assert.That(TemporaryCompanionBalance.GearLevel(true, level, 99), Is.EqualTo(level));
        }

        [Test]
        public void EndgameCompanionsNeverRollMissingSlotsOrUnderlevelGear()
        {
            for (int roll = 0; roll < 1000; roll++)
            {
                Assert.That(TemporaryCompanionBalance.EquipRegularSlot(true, 50, roll / 1000d), Is.True);
                Assert.That(TemporaryCompanionBalance.EquipOffhand(true, 50, true, roll / 1000d), Is.True);
                Assert.That(TemporaryCompanionBalance.GearLevel(true, 50, roll % 51), Is.EqualTo(50));
            }
            Assert.That(TemporaryCompanionBalance.EquipOffhand(true, 50, false, 0), Is.False);
        }

        [Test]
        public void PersistentBotsNeverAcquireTheEndgameException()
        {
            Assert.That(TemporaryCompanionBalance.IsEndgame(false, 50), Is.False);
            Assert.That(TemporaryCompanionBalance.IsEndgame(true, 49), Is.False);
            Assert.That(TemporaryCompanionBalance.GearLevel(false, 50, 43), Is.EqualTo(43));
        }

        private static Spell MakeSpell(int id, int level, eSpellType type = eSpellType.DirectDamage,
            double castTime = 2.5, string target = "Enemy", int radius = 0, int subSpellId = 0) =>
            new(new DbSpell { SpellID = id, Name = $"Test {id}", Type = type.ToString(), Target = target,
                CastTime = castTime, Radius = radius, SubSpellID = subSpellId }, level);

        [Test]
        public void RankUpgradeIgnoresChangedCastTimeButPreservesInstantAndAreaRoles()
        {
            Spell low = MakeSpell(1, 10, castTime: 3.5), high = MakeSpell(2, 48),
                instant = MakeSpell(3, 30, castTime: 0), area = MakeSpell(4, 40, radius: 350),
                illegal = MakeSpell(5, 51);
            var ranks = TemporaryCompanionBalance.HighestRanks([low, high, instant, area, illegal], 50);
            Assert.That(ranks, Is.EquivalentTo(new[] { high, instant, area }));
        }

        [Test]
        public void UnavailableSpecializationRanksAreNeverInvented()
        {
            Spell learned = MakeSpell(1, 23);
            Assert.That(TemporaryCompanionBalance.HighestRanks([learned], 50), Is.EqualTo(new[] { learned }));
        }

        [Test]
        public void NecromancerPayloadRolesSurviveRankFiltering()
        {
            Spell low = MakeSpell(1, 10, eSpellType.PetSpell, subSpellId: 101),
                high = MakeSpell(2, 48, eSpellType.PetSpell, subSpellId: 102),
                heal = MakeSpell(3, 35, eSpellType.PetSpell, subSpellId: 103);
            var payloads = new Dictionary<int, Spell>
            {
                [101] = MakeSpell(101, 10), [102] = MakeSpell(102, 48),
                [103] = MakeSpell(103, 35, eSpellType.Heal, target: "Pet")
            };
            Assert.That(TemporaryCompanionBalance.HighestRanks([low, high, heal], 50, id => payloads[id]),
                Is.EquivalentTo(new[] { high, heal }));
        }

        [Test]
        public void EndgamePetChoiceCannotRollLowerRanksAndTiesAreStable()
        {
            Spell low = MakeSpell(1, 7), mid = MakeSpell(2, 32), high = MakeSpell(3, 48), tie = MakeSpell(4, 48);
            for (int attempt = 0; attempt < 500; attempt++)
            {
                var chosen = AutonomousPetSupport.ChooseWeightedByRank(new[] { (low, (SpellLine)null), (high, (SpellLine)null),
                    (mid, (SpellLine)null), (tie, (SpellLine)null) }, true);
                Assert.That(chosen.Spell, Is.SameAs(tie));
                Assert.That(TemporaryCompanionBalance.HighestSpell([low, mid, high, tie]), Is.SameAs(tie));
            }
        }

        [Test]
        public void EndgamePetChoiceSafelyHandlesEmptyPool()
        {
            Assert.That(AutonomousPetSupport.ChooseWeightedByRank([], true).Spell, Is.Null);
        }

        [TestCase(eRealm.Albion)] [TestCase(eRealm.Hibernia)] [TestCase(eRealm.Midgard)]
        public void AllEraClassesCanReceiveCompleteLevel50ArmorAndAccessories(eRealm realm)
        {
            eInventorySlot[] armorSlots = [eInventorySlot.HeadArmor, eInventorySlot.HandsArmor,
                eInventorySlot.FeetArmor, eInventorySlot.TorsoArmor, eInventorySlot.LegsArmor, eInventorySlot.ArmsArmor];
            eInventorySlot[] accessories = [eInventorySlot.Jewelry, eInventorySlot.Cloak, eInventorySlot.Neck,
                eInventorySlot.Waist, eInventorySlot.LeftBracer, eInventorySlot.RightBracer,
                eInventorySlot.LeftRing, eInventorySlot.RightRing];
            foreach (eCharacterClass characterClass in AutonomousBotIdentityGenerator.GetEraClasses(realm))
            {
                eObjectType armor = realm switch
                {
                    eRealm.Albion => GeneratedUniqueItem.GetAlbionArmorType(characterClass, 50),
                    eRealm.Hibernia => GeneratedUniqueItem.GetHiberniaArmorType(characterClass, 50),
                    _ => GeneratedUniqueItem.GetMidgardArmorType(characterClass, 50)
                };
                var inventory = new BotInventory();
                foreach (eInventorySlot slot in armorSlots.Concat(accessories))
                {
                    DbItemTemplate template = BotEquipment.CreateEndgameCompanionItem(realm, characterClass,
                        armorSlots.Contains(slot) ? armor : eObjectType.Magical, slot);
                    Assert.That(template.Level, Is.EqualTo(50), $"{characterClass} {slot}");
                    Assert.That(template.Quality, Is.EqualTo(99));
                    Assert.That(template.Item_Type, Is.EqualTo((int)slot));
                    Assert.That(inventory.AddItem(slot, GameInventoryItem.Create(template)), Is.True);
                }
                Assert.That(armorSlots.Concat(accessories).All(slot => inventory.GetItem(slot) != null), Is.True);
                Assert.That(inventory.IsPersistent, Is.False);
            }
        }

        [TestCase(eObjectType.Blades, eInventorySlot.RightHandWeapon)]
        [TestCase(eObjectType.Blades, eInventorySlot.LeftHandWeapon)]
        [TestCase(eObjectType.Staff, eInventorySlot.TwoHandWeapon)]
        [TestCase(eObjectType.RecurvedBow, eInventorySlot.DistanceWeapon)]
        public void WeaponGenerationPreservesRequestedSlot(eObjectType type, eInventorySlot slot)
        {
            for (int i = 0; i < 30; i++)
            {
                var item = BotEquipment.CreateEndgameCompanionItem(eRealm.Hibernia, eCharacterClass.Ranger, type, slot);
                Assert.That(item.Item_Type, Is.EqualTo((int)slot));
                Assert.That(item.Level, Is.EqualTo(50));
                Assert.That(item.IsTradable, Is.False);
            }
        }

        private static DbItemTemplate Weapon(int level, eRealm realm = eRealm.Albion, string classes = "") =>
            new() { Level = level, Realm = (int)realm, Object_Type = (int)eObjectType.SlashingWeapon,
                Item_Type = (int)eInventorySlot.RightHandWeapon, IsPickable = true, AllowedClasses = classes,
                DPS_AF = 12 + 3 * level, SPD_ABS = 30, Quality = 85, Condition = 50000, MaxCondition = 50000,
                Durability = 50000, MaxDurability = 50000,
                IsTradable = true, IsDropable = true, Name = "Test weapon" };

        [TestCase(20)] [TestCase(19)] [TestCase(18)]
        public void WeaponSelectionPrefersHighestValidLevelAndNeverMutatesTemplates(int best)
        {
            var templates = Enumerable.Range(16, best - 15).Select(level => Weapon(level)).ToArray();
            var chosen = BotEquipment.SelectCompanionWeapon(templates, 20, eRealm.Albion,
                eCharacterClass.Paladin, eObjectType.SlashingWeapon, eInventorySlot.RightHandWeapon);
            Assert.That(chosen.Level, Is.EqualTo(best));
            Assert.That(chosen.IsTradable, Is.False);
            Assert.That(templates.All(item => item.IsTradable && item.IsDropable), Is.True);
            Assert.That(templates, Does.Not.Contain(chosen));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(20)] [TestCase(49)] [TestCase(50)]
        public void MissingWeaponsAndShieldsGenerateUsableCurrentLevelFallbacks(byte level)
        {
            foreach (var (type, slot, size) in new[] {
                (eObjectType.SlashingWeapon, eInventorySlot.RightHandWeapon, 0),
                (eObjectType.SlashingWeapon, eInventorySlot.LeftHandWeapon, 0),
                (eObjectType.Staff, eInventorySlot.TwoHandWeapon, 0),
                (eObjectType.Longbow, eInventorySlot.DistanceWeapon, 0),
                (eObjectType.Shield, eInventorySlot.LeftHandWeapon, 1),
                (eObjectType.Shield, eInventorySlot.LeftHandWeapon, 3) })
            {
                var item = BotEquipment.SelectCompanionWeapon([], level, eRealm.Albion,
                    eCharacterClass.Paladin, type, slot, shieldSize: size);
                var inventory = new BotInventory();
                Assert.That(item.Level, Is.EqualTo(level));
                Assert.That(item.Object_Type, Is.EqualTo((int)type));
                Assert.That(item.Item_Type, Is.EqualTo((int)slot));
                Assert.That(item.Realm, Is.EqualTo((int)eRealm.Albion));
                Assert.That(item.DPS_AF, Is.GreaterThan(0));
                Assert.That(item.Condition, Is.GreaterThan(0));
                if (size > 0) Assert.That(item.Type_Damage, Is.EqualTo(size));
                Assert.That(inventory.AddItem(slot, GameInventoryItem.Create(item)), Is.True);
                Assert.That(inventory.GetItem(slot), Is.Not.Null);
                Assert.That(inventory.IsPersistent, Is.False);
            }
        }

        [Test]
        public void WrongRealmClassLevelHandOrRequiredLevelCannotDefeatFallback()
        {
            var wrongHand = Weapon(20); wrongHand.Hand = 1;
            var requiredLevel = Weapon(20); requiredLevel.LevelRequirement = 30;
            var item = BotEquipment.SelectCompanionWeapon([
                Weapon(17), Weapon(21), Weapon(20, eRealm.Hibernia),
                Weapon(20, classes: ((int)eCharacterClass.Cabalist).ToString()), wrongHand, requiredLevel],
                20, eRealm.Albion, eCharacterClass.Paladin, eObjectType.SlashingWeapon, eInventorySlot.RightHandWeapon);
            Assert.That(item.Level, Is.EqualTo(20));
            Assert.That(item.Name, Is.Not.EqualTo("Test weapon"));
            Assert.That(item.Hand, Is.Not.EqualTo(1));
        }

        [TestCase(1)] [TestCase(10)] [TestCase(30)] [TestCase(49)]
        public void EveryEraClassHasFallbackArmorAndAccessoriesAtLevel(byte level)
        {
            foreach (var realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
            foreach (var cls in AutonomousBotIdentityGenerator.GetEraClasses(realm))
            {
                var armor = realm switch {
                    eRealm.Albion => GeneratedUniqueItem.GetAlbionArmorType(cls, level),
                    eRealm.Hibernia => GeneratedUniqueItem.GetHiberniaArmorType(cls, level),
                    _ => GeneratedUniqueItem.GetMidgardArmorType(cls, level) };
                foreach (var slot in new[] { eInventorySlot.HeadArmor, eInventorySlot.TorsoArmor,
                    eInventorySlot.ArmsArmor, eInventorySlot.HandsArmor, eInventorySlot.LegsArmor, eInventorySlot.FeetArmor,
                    eInventorySlot.Jewelry, eInventorySlot.Cloak, eInventorySlot.Neck, eInventorySlot.Waist,
                    eInventorySlot.LeftBracer, eInventorySlot.RightBracer, eInventorySlot.LeftRing, eInventorySlot.RightRing })
                {
                    bool isArmor = slot is eInventorySlot.HeadArmor or eInventorySlot.TorsoArmor or eInventorySlot.ArmsArmor or
                        eInventorySlot.HandsArmor or eInventorySlot.LegsArmor or eInventorySlot.FeetArmor;
                    var item = BotEquipment.CreateCompanionItem(realm, cls, level, isArmor ? armor : eObjectType.Magical, slot);
                    Assert.That(item.Level, Is.EqualTo(level), $"{cls} {slot}");
                    Assert.That(item.Item_Type, Is.EqualTo((int)slot));
                    Assert.That(new BotInventory().AddItem(slot, GameInventoryItem.Create(item)), Is.True);
                }
            }
        }
    }
}
