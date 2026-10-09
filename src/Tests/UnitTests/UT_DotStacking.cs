using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_DotStacking
    {
        private static Spell Dot(int id, int effectGroup) =>
            new(new DbSpell { SpellID = id, Type = "DamageOverTime", Target = "Enemy", EffectGroup = effectGroup }, 1);

        [Test]
        public void SluaghbinderRotBaneAndGraveRotStack()
        {
            Spell rot = Dot(59104, 59110), bane = Dot(59018, 59111), graveRot = Dot(59070, 59070);
            Assert.That(BotBrain.DotsConflict(bane, rot), Is.False);
            Assert.That(BotBrain.DotsConflict(graveRot, rot), Is.False);
            Assert.That(BotBrain.DotsConflict(graveRot, bane), Is.False);
        }

        [Test]
        public void SameGroupOrUngroupedDotsStillConflict()
        {
            Assert.That(BotBrain.DotsConflict(Dot(59014, 59111), Dot(59018, 59111)), Is.True);
            Assert.That(BotBrain.DotsConflict(Dot(1, 0), Dot(2, 0)), Is.True);
            Assert.That(BotBrain.DotsConflict(Dot(1, 0), Dot(1, 0)), Is.True);
        }
    }
}
