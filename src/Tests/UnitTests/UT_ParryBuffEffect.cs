using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_ParryBuffEffect
    {
        [Test]
        public void ParryBuffHasItsOwnEffectType()
        {
            // Unmapped buffs become eEffect.Unknown, which breaks "already
            // buffed?" checks (bots recast forever) and the parry gate.
            var spell = new Spell(new DbSpell { SpellID = 59026, Type = "ParryBuff", Target = "Self", Value = 4, Duration = 1200 }, 20);
            Assert.That(EffectHelper.GetEffectFromSpell(spell), Is.EqualTo(eEffect.ParryBuff));
        }

        [Test]
        public void SavageParryBuffKeepsItsSavageEffect()
        {
            var spell = new Spell(new DbSpell { SpellID = 1, Type = "SavageParryBuff", Target = "Self", Value = 10, Duration = 30 }, 20);
            Assert.That(EffectHelper.GetEffectFromSpell(spell), Is.EqualTo(eEffect.SavageBuff));
        }
    }
}
