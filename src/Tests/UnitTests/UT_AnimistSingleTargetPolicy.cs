using System.Collections.Generic;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_AnimistSingleTargetPolicy
    {
        private static Spell Spell(int id, eSpellType type, int radius = 0, int sub = 0) => new(new DbSpell
        {
            SpellID = id, Name = "test", Type = type.ToString(), Radius = radius,
            Target = eSpellTarget.ENEMY.ToString(), SubSpellID = sub, LifeDrainReturn = 2130,
            Damage = 9,
        }, 1);

        [TestCase(eSpellType.SummonAnimistPet)]
        [TestCase(eSpellType.SummonAnimistFnF)]
        [TestCase(eSpellType.SummonAnimistFnFCustom)]
        public void SingleTargetDamageShroomsRemainAvailable(eSpellType type)
        {
            Spell payload = Spell(2, eSpellType.DirectDamage);
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(Spell(1, type, sub: 2),
                id => id == 2 ? payload : null, _ => null), Is.True);
        }

        [TestCase(350, eSpellType.DirectDamage)]
        [TestCase(350, eSpellType.DamageOverTime)]
        [TestCase(0, eSpellType.TurretPBAoE)]
        public void HiddenAreaPayloadIsRejectedEvenIfSummonHasZeroRadius(int radius, eSpellType type)
        {
            Spell payload = Spell(2, type, radius);
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(Spell(1, eSpellType.SummonAnimistFnF, sub: 2),
                id => id == 2 ? payload : null, _ => null), Is.False);
        }

        [Test] public void TemplateAndNestedSpellsCannotBypassAreaRestriction()
        {
            var spells = new Dictionary<int, Spell>
            {
                [2] = Spell(2, eSpellType.DirectDamage, sub: 3),
                [3] = Spell(3, eSpellType.DirectDamage, 350),
            };
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(Spell(1, eSpellType.SummonAnimistPet),
                id => spells.GetValueOrDefault(id), _ => new[] { spells[2] }), Is.False);
        }

        [Test] public void MissingOrRecursivePayloadFailsClosed()
        {
            Spell summon = Spell(1, eSpellType.SummonAnimistFnF, sub: 2);
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(summon, _ => null, _ => null), Is.False);
            Spell recursive = Spell(2, eSpellType.DirectDamage, sub: 2);
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(summon, _ => recursive, _ => null), Is.False);
        }

        [TestCase(eSpellType.DirectDamageWithDebuff)]
        [TestCase(eSpellType.DamageSpeedDecrease)]
        [TestCase(eSpellType.StrengthDebuff)]
        [TestCase(eSpellType.Taunt)]
        [TestCase(eSpellType.Heal)]
        [TestCase(eSpellType.Bladeturn)]
        [TestCase(eSpellType.DamageOverTime)]
        public void DebuffAndSupportPayloadsAreRejectedEvenWhenTheyHaveDamage(eSpellType type)
        {
            Spell payload = Spell(2, type);
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(Spell(1, eSpellType.SummonAnimistFnF, sub: 2),
                id => payload, _ => null), Is.False);
        }

        [Test] public void PureDamageCannotHideAnExtraDebuff()
        {
            var spells = new Dictionary<int, Spell> {
                [2] = Spell(2, eSpellType.DirectDamage, sub: 3),
                [3] = Spell(3, eSpellType.StrengthDebuff) };
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(Spell(1, eSpellType.SummonAnimistPet, sub: 2),
                id => spells.GetValueOrDefault(id), _ => null), Is.False);
            Assert.That(AnimistSingleTargetPolicy.AllowsSummon(Spell(1, eSpellType.SummonAnimistPet),
                id => spells.GetValueOrDefault(id), _ => new[] { Spell(4, eSpellType.DirectDamage), spells[3] }), Is.False);
        }

        [Test] public void NativeOwnDamageSpellsAndOtherPetClassesAreNotBlocked()
        {
            Assert.That(AnimistSingleTargetPolicy.AllowsAutomatedSpell(Spell(1, eSpellType.Bomber)), Is.True);
            Assert.That(AnimistSingleTargetPolicy.AllowsAutomatedSpell(Spell(2, eSpellType.DirectDamage)), Is.True);
            Assert.That(AnimistSingleTargetPolicy.AllowsAutomatedSpell(Spell(3, eSpellType.SummonTheurgistPet)), Is.True);
            Assert.That(AnimistSingleTargetPolicy.AllowsAutomatedSpell(Spell(4, eSpellType.SummonSimulacrum)), Is.True);
        }

        [Test] public void OwnerGetsCastingOpportunityButCannotBeStuckWaitingForever()
        {
            Assert.That(AnimistSingleTargetPolicy.ShouldYieldToOwner(20000, 5000, true), Is.True);
            Assert.That(AnimistSingleTargetPolicy.ShouldYieldToOwner(20000, 20000, true), Is.False);
            Assert.That(AnimistSingleTargetPolicy.ShouldYieldToOwner(20000, 5000, false), Is.False);
        }
    }
}
