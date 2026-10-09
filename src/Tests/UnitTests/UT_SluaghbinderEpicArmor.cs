using System.Linq;
using DOL.Database;
using DOL.GS;
using DOL.GS.Quests.Hibernia;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SluaghbinderEpicArmor
    {
        [Test]
        public void SetHasTenUniquePiecesOnPrivateModels()
        {
            Assert.That(SluaghbinderEpicArmor.Set, Has.Length.EqualTo(10));
            Assert.That(SluaghbinderEpicArmor.Set.Select(p => p.Model), Is.Unique);
            Assert.That(SluaghbinderEpicArmor.Set.Select(p => p.Slot), Is.Unique);
            Assert.That(SluaghbinderEpicArmor.Set.All(p => p.Model >= 4826 && p.Model <= 4835), Is.True);
        }

        [TestCase("helm", 82)] [TestCase("hauberk", 96)] [TestCase("vambraces", 83)] [TestCase("gauntlets", 86)]
        [TestCase("greaves", 87)] [TestCase("sabatons", 88)] [TestCase("mantle", 90)]
        [TestCase("mace", 94)] [TestCase("shield", 85)] [TestCase("scythe", 100)]
        public void EachPieceMatchesTheBestItemInItsSlot(string key, int utility) =>
            Assert.That(SluaghbinderEpicArmor.Utility(SluaghbinderEpicArmor.Set.Single(p => p.Key == key)), Is.EqualTo(utility));

        [Test]
        public void TemplatesAreSluaghbinderOnlyUntradableAndWorthOneCopper()
        {
            int ratio = GS.ServerProperties.Properties.ITEM_SELL_RATIO;
            try
            {
                GS.ServerProperties.Properties.ITEM_SELL_RATIO = 50;
                foreach (var piece in SluaghbinderEpicArmor.Set)
                {
                    var t = SluaghbinderEpicArmor.BuildTemplate(piece);
                    Assert.That(t.Id_nb, Does.StartWith(SluaghbinderEpicArmor.Prefix));
                    Assert.That(t.Level, Is.EqualTo(51));
                    Assert.That(t.Quality, Is.EqualTo(100));
                    Assert.That(t.AllowedClasses, Is.EqualTo(((int)eCharacterClass.Sluaghbinder).ToString()));
                    Assert.That(t.Realm, Is.EqualTo((int)eRealm.Hibernia));
                    Assert.That(t.IsTradable, Is.False);
                    Assert.That(t.CanDropAsLoot, Is.False);
                    Assert.That(t.Price * GS.ServerProperties.Properties.ITEM_SELL_RATIO / 100, Is.EqualTo(1), "sells for 1 copper");
                    Assert.That(SluaghbinderEpicArmor.IsSetItem(t), Is.True);
                }
            }
            finally
            {
                GS.ServerProperties.Properties.ITEM_SELL_RATIO = ratio;
            }
        }

        [Test]
        public void WeaponsAreRealEpicWeapons()
        {
            var mace = SluaghbinderEpicArmor.Set.Single(p => p.Key == "mace");
            var scythe = SluaghbinderEpicArmor.Set.Single(p => p.Key == "scythe");
            var shield = SluaghbinderEpicArmor.Set.Single(p => p.Key == "shield");
            Assert.That((mace.ObjectType, mace.Slot, mace.DpsAf), Is.EqualTo((eObjectType.Blunt, eInventorySlot.RightHandWeapon, 165)));
            Assert.That((scythe.ObjectType, scythe.Slot, scythe.DpsAf, scythe.Hand), Is.EqualTo((eObjectType.Scythe, eInventorySlot.TwoHandWeapon, 165, 1)));
            Assert.That((shield.ObjectType, shield.Slot, shield.TypeDamage), Is.EqualTo((eObjectType.Shield, eInventorySlot.LeftHandWeapon, 3)));
        }

        [Test]
        public void SetPiecesCarryNoItemEffect()
        {
            foreach (var piece in SluaghbinderEpicArmor.Set)
            {
                var stale = new DbItemTemplate { Effect = 639 };
                Assert.That(SluaghbinderEpicArmor.BuildTemplate(piece, stale).Effect, Is.Zero, piece.Key);
            }
        }

        [Test]
        public void OnlySetIdsCountAsSetItems()
        {
            Assert.That(SluaghbinderEpicArmor.IsSetItem("sluagh_epic_helm"), Is.True);
            Assert.That(SluaghbinderEpicArmor.IsSetItem("offline50_20260920_c63_s21_t38"), Is.False);
            Assert.That(SluaghbinderEpicArmor.IsSetItem((string)null), Is.False);
        }
    }
}
