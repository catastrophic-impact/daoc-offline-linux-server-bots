using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.PropertyCalc;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Gamebots and companions use the player parry, block and evade formulas
    /// (the same code path as GamePlayer), not the fixed NPC template chances.
    /// </summary>
    [TestFixture]
    public class UT_BotPlayerDefense
    {
        [Test]
        public void BotsAndPlayersUsePlayerDefenseButOrdinaryNpcsDoNot()
        {
            var bot = (GameBot)RuntimeHelpers.GetUninitializedObject(typeof(GameBot));
            var npc = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
            var player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
            Assert.Multiple(() =>
            {
                Assert.That(PlayerDefenseFormula.UsesPlayerDefense(bot), Is.True);
                Assert.That(PlayerDefenseFormula.UsesPlayerDefense(player), Is.True);
                Assert.That(PlayerDefenseFormula.UsesPlayerDefense(npc), Is.False);
            });
        }

        // The stock player formulas, per mille.
        [TestCase(100, 1, 75)]     // 5% base + dexterity bonus, Parry 1
        [TestCase(100, 30, 220)]   // +0.5% per Parry level above 1
        [TestCase(250, 50, 395)]
        public void ParryMatchesThePlayerFormula(int dex, int spec, int expected) =>
            Assert.That(PlayerDefenseFormula.Parry(dex, true, spec), Is.EqualTo(expected));

        [Test]
        public void NoParrySpecMeansNoParry() =>
            Assert.That(PlayerDefenseFormula.Parry(250, false, 50), Is.Zero);

        [TestCase(100, 1, 75)]
        [TestCase(150, 42, 305)]   // (150*2-100)/4 + (42-1)*5 + 50
        public void BlockMatchesThePlayerFormula(int dex, int shields, int expected) =>
            Assert.That(PlayerDefenseFormula.Block(dex, shields), Is.EqualTo(expected));

        [TestCase(60, 60, 1, 51)]    // (900 + 60 + 60) * 1 * 5 / 100
        [TestCase(150, 120, 5, 292)]
        [TestCase(150, 120, 0, 0)]   // no Evade ability
        public void EvadeMatchesThePlayerFormula(int qui, int dex, int level, int expected) =>
            Assert.That(PlayerDefenseFormula.Evade(qui, dex, level), Is.EqualTo(expected));

        [TestCase(eObjectType.Blades, true)]
        [TestCase(eObjectType.Shield, true)]
        [TestCase(eObjectType.Longbow, false)]
        [TestCase(eObjectType.RecurvedBow, false)]
        [TestCase(eObjectType.CompositeBow, false)]
        [TestCase(eObjectType.Crossbow, false)]
        public void OnlyMeleeWeaponsParry(eObjectType type, bool expected)
        {
            DbInventoryItem item = GameInventoryItem.Create(new DbItemTemplate { Id_nb = "parry_test", Object_Type = (int)type });
            Assert.That(GameLiving.CanParryWith(item), Is.EqualTo(expected));
        }

        [Test]
        public void NoWeaponCannotParry() => Assert.That(GameLiving.CanParryWith(null), Is.False);
    }
}
