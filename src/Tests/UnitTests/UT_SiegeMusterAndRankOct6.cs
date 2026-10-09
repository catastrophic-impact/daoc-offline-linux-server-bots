using System.Linq;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SiegeMusterAndRankOct6
    {
        [TestCase(eRealm.Midgard, (ushort)1, (ushort)1, 655200f, TestName = "Midgard attacking Albion musters at its Hadrian's Wall outpost")]
        [TestCase(eRealm.Hibernia, (ushort)1, (ushort)1, 605743f, TestName = "Hibernia attacking Albion musters at its own outpost")]
        [TestCase(eRealm.Albion, (ushort)200, (ushort)200, 475835f, TestName = "Albion attacking Hibernia musters in Emain Macha")]
        [TestCase(eRealm.Midgard, (ushort)100, (ushort)100, 766235f, TestName = "A home-region target falls back to the border keep")]
        public void SiegeArmiesMusterInsideTheTargetFrontier(eRealm realm, ushort target, ushort region, float x)
        {
            Assert.That(AutonomousRvrStaging.TryGetSiegeMuster(realm, target, out var muster), Is.True);
            Assert.That(muster.RegionId, Is.EqualTo(region));
            Assert.That(muster.Position.X, Is.EqualTo(x));
        }

        [Test]
        public void WeightedRankChoiceFavoursTheHighestRankButKeepsLowerOnes()
        {
            Spell low = Spell(1, 10), high = Spell(2, 40), high2 = Spell(3, 40);
            var pool = new[] { (low, (SpellLine)null), (high, (SpellLine)null), (high2, (SpellLine)null) };
            var picks = Enumerable.Range(0, 4000).Select(_ => AutonomousPetSupport.ChooseWeightedByRank(pool).Spell).ToArray();
            int lows = picks.Count(spell => spell == low);
            // Lower rank weight 0.22 of the top: about 18% of picks.
            Assert.That(lows, Is.InRange(500, 950));
            Assert.That(picks.Count(spell => spell == high), Is.GreaterThan(1000));
            Assert.That(picks.Count(spell => spell == high2), Is.GreaterThan(1000));
            Assert.That(AutonomousPetSupport.ChooseWeightedByRank(new (Spell, SpellLine)[0]).Spell, Is.Null);
        }

        private static Spell Spell(int id, int level) =>
            new(new DOL.Database.DbSpell { SpellID = id, Name = $"s{id}", Type = "SummonCommander", Target = "Self" }, level);
    }
}
