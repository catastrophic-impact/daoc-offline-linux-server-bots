using System;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;
using static DOL.GS.CharmCreatureRoles;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_CharmCreatureRoles
    {
        private static Spell MakeSpell(string type, string target, int range) =>
            new(new DbSpell { SpellID = 1, Type = type, Target = target, Range = range }, 1);

        private static readonly Func<string, bool> BowSets = id => id.StartsWith("bow", StringComparison.Ordinal);

        [Test]
        public void RangedDamageSpellMakesACaster()
        {
            var shape = new SpawnShape(new[] { MakeSpell("DirectDamage", "Enemy", 1500) }, new[] { "bow-set" }, 191);
            Assert.That(Evaluate(new[] { shape }, BowSets), Is.EqualTo((CharmCreatureRole.Caster, false)));
        }

        [TestCase("Stun", "Enemy", 1500)]           // ranged crowd control is not a ranged attack
        [TestCase("Heal", "Realm", 1500)]
        [TestCase("OffensiveProc", "Self", 300)]   // clinging soul's snare proc
        [TestCase("DirectDamage", "Enemy", 0)]     // point-blank area
        public void SpellsThatCannotAttackAtRangeStayMelee(string type, string target, int range)
        {
            var shape = new SpawnShape(new[] { MakeSpell(type, target, range) }, Array.Empty<string>(), 191);
            Assert.That(Evaluate(new[] { shape }, BowSets).Roles, Is.EqualTo(CharmCreatureRole.Melee));
        }

        [Test]
        public void BowWithoutRangedSpellMakesAnArcher()
        {
            var shape = new SpawnShape(null, new[] { "bow-set" }, 191);
            Assert.That(Evaluate(new[] { shape }, BowSets).Roles, Is.EqualTo(CharmCreatureRole.Archer));
        }

        [Test]
        public void RandomGearThatOnlySometimesHasABowShowsBoth()
        {
            var shape = new SpawnShape(null, new[] { "sword-set", "bow-set" }, 191);
            CharmCreatureRole roles = Evaluate(new[] { shape }, BowSets).Roles;
            Assert.That(roles, Is.EqualTo(CharmCreatureRole.Archer | CharmCreatureRole.Melee));
            Assert.That(Label(roles), Is.EqualTo("Archer or Melee"));
        }

        [Test]
        public void NoSpellsAndNoGearIsMelee()
        {
            var shape = new SpawnShape(null, Array.Empty<string>(), 191);
            Assert.That(Evaluate(new[] { shape }, BowSets), Is.EqualTo((CharmCreatureRole.Melee, false)));
            Assert.That(Label(CharmCreatureRole.None), Is.EqualTo("Melee"));
            Assert.That(Label(CharmCreatureRole.Caster), Is.EqualTo("Caster"));
        }

        [Test]
        public void SpeedZeroInAnyTemplateVariantIsImmobile()
        {
            var moving = new SpawnShape(null, Array.Empty<string>(), 180);
            var rooted = new SpawnShape(null, Array.Empty<string>(), 0);
            Assert.That(Evaluate(new[] { moving }, BowSets).Immobile, Is.False);
            Assert.That(Evaluate(new[] { rooted }, BowSets).Immobile, Is.True);
            Assert.That(Evaluate(new[] { moving, rooted }, BowSets).Immobile, Is.True);
        }

        [Test]
        public void SpawnWithoutATemplateUsesItsOwnSpeed()
        {
            Assert.That(IsImmobile(new DbMob { Name = "rooted thing", Speed = 0 }), Is.True);
            Assert.That(IsImmobile(new DbMob { Name = "walking thing", Speed = 191 }), Is.False);
            Assert.That(Classify(new DbMob { Name = "walking thing", Speed = 191 }), Is.EqualTo(CharmCreatureRole.Melee));
        }
    }
}
