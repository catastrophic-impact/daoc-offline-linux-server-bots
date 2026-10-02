using DOL.Database;
using DOL.GS;
using NUnit.Framework;
using static DOL.GS.AnimistShroomPlanner;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_AnimistShroomPlanner
    {
        // A fight in progress: main shroom up, one damage shroom planted, the
        // fight still wants four, nothing else needed.
        private static Situation Fight() => new(
            HasMain: true, CanSummonMain: true, MainHealthPercent: 100, CanHealMain: true,
            TargetDying: false, FightWorthBuffing: true,
            DamageShroomsNearFight: 1, DesiredDamageShrooms: 4, CanPlantDamage: true,
            VentNeeded: false, AblativeNeeded: false, BurstReady: false, SporeNeeded: false);

        private static Spell MakeSpell(string type, string target = "Enemy", double damage = 0) =>
            new(new DbSpell { SpellID = 1, Type = type, Target = target, Damage = damage }, 1);

        [Test]
        public void MissingMainShroomComesFirst() =>
            Assert.That(Next(Fight() with { HasMain = false }), Is.EqualTo(AnimistShroomAction.SummonMain));

        [Test]
        public void HurtMainShroomIsHealedBeforePlanting()
        {
            Assert.That(Next(Fight() with { MainHealthPercent = 40 }), Is.EqualTo(AnimistShroomAction.HealMain));
            Assert.That(Next(Fight() with { MainHealthPercent = 40, CanHealMain = false }), Is.EqualTo(AnimistShroomAction.PlantDamage));
        }

        [Test]
        public void OneDamageShroomOpensTheFightBeforeAnyBuffShroom()
        {
            var opening = Fight() with { DamageShroomsNearFight = 0, VentNeeded = true, AblativeNeeded = true };
            Assert.That(Next(opening), Is.EqualTo(AnimistShroomAction.PlantDamage));
            Assert.That(Next(opening with { DamageShroomsNearFight = 1 }), Is.EqualTo(AnimistShroomAction.PlantVent));
        }

        [Test]
        public void MissingVentIsPlantedOnceThenDamageResumes()
        {
            Assert.That(Next(Fight() with { VentNeeded = true }), Is.EqualTo(AnimistShroomAction.PlantVent));
            // Once covered (the vent check excludes a live vent of the same
            // type), the next turn goes straight back to damage shrooms.
            Assert.That(Next(Fight() with { VentNeeded = false }), Is.EqualTo(AnimistShroomAction.PlantDamage));
        }

        [Test]
        public void VentsAreNotSpentOnAFightThatIsAlmostOver() =>
            Assert.That(Next(Fight() with { VentNeeded = true, FightWorthBuffing = false }),
                Is.EqualTo(AnimistShroomAction.PlantDamage));

        [Test]
        public void DamageShroomsStopWhenTheFightHasEnough()
        {
            Assert.That(Next(Fight() with { DamageShroomsNearFight = 3 }), Is.EqualTo(AnimistShroomAction.PlantDamage));
            Assert.That(Next(Fight() with { DamageShroomsNearFight = 4 }), Is.EqualTo(AnimistShroomAction.None));
            Assert.That(Next(Fight() with { CanPlantDamage = false }), Is.EqualTo(AnimistShroomAction.None));
        }

        [Test]
        public void NothingIsPlantedOnADyingLastMonster() =>
            Assert.That(Next(Fight() with { TargetDying = true, DamageShroomsNearFight = 0, VentNeeded = true }),
                Is.EqualTo(AnimistShroomAction.None));

        [Test]
        public void VerdantBurstAndSporeComeBeforeExtraDamageShrooms()
        {
            Assert.That(Next(Fight() with { BurstReady = true }), Is.EqualTo(AnimistShroomAction.TurretBurst));
            Assert.That(Next(Fight() with { SporeNeeded = true }), Is.EqualTo(AnimistShroomAction.PlantSpore));
            Assert.That(Next(Fight() with { AblativeNeeded = true, BurstReady = true }), Is.EqualTo(AnimistShroomAction.PlantAblative));
        }

        [TestCase(0, 179, 10, 1)]        // always at least one
        [TestCase(3000, 179, 10, 3)]     // 179 x 6 hits = 1,074 per shroom
        [TestCase(30000, 179, 10, 10)]   // capped
        [TestCase(30000, 179, 2, 2)]     // crowded camp cap
        [TestCase(300, 37, 10, 2)]       // low level: Forest's Emissary
        public void DesiredDamageShroomsScaleWithTheFight(long health, double damage, int cap, int expected) =>
            Assert.That(DesiredDamageShrooms(health, damage, cap), Is.EqualTo(expected));

        [TestCase(10, 49, true, false)]  // Arboreal/Creeping: damage main outranks taunt
        [TestCase(49, 11, true, true)]   // Verdant: War Herald with Briar burst
        [TestCase(49, 11, false, false)] // no burst learned: a taunt main alone deals no damage
        [TestCase(49, null, false, false)]
        public void TauntMainOnlyForVerdantBurstPlay(int? taunt, int? damage, bool burst, bool expected) =>
            Assert.That(PrefersTauntMain(taunt, damage, burst), Is.EqualTo(expected));

        [Test]
        public void PayloadsClassifyEveryShroomFamily()
        {
            Assert.That(ClassifyPayload(MakeSpell("DirectDamage", damage: 179)), Is.EqualTo(AnimistShroomKind.Damage));
            Assert.That(ClassifyPayload(MakeSpell("HeatColdMatterBuff", "Self")), Is.EqualTo(AnimistShroomKind.ResistVent));
            Assert.That(ClassifyPayload(MakeSpell("BodySpiritEnergyBuff", "Self")), Is.EqualTo(AnimistShroomKind.ResistVent));
            Assert.That(ClassifyPayload(MakeSpell("AblativeArmor", "Realm")), Is.EqualTo(AnimistShroomKind.Ablative));
            Assert.That(ClassifyPayload(MakeSpell("MeleeDamageDebuff")), Is.EqualTo(AnimistShroomKind.MeleeDebuff));
            Assert.That(ClassifyPayload(MakeSpell("SpeedDecrease")), Is.EqualTo(AnimistShroomKind.Snare));
            Assert.That(ClassifyPayload(null), Is.EqualTo(AnimistShroomKind.Unknown));
            Assert.That(ClassifyMainPayload(MakeSpell("Taunt")), Is.EqualTo(AnimistMainKind.Taunt));
            Assert.That(ClassifyMainPayload(MakeSpell("DamageSpeedDecrease", damage: 164)), Is.EqualTo(AnimistMainKind.Damage));
            Assert.That(ClassifyMainPayload(MakeSpell("DirectDamage", damage: 204)), Is.EqualTo(AnimistMainKind.Damage));
        }

        [TestCase(1000, 850)]  // main and damage shrooms reach 1,000: stand at 850
        [TestCase(1350, 1200)]
        [TestCase(200, 300)]   // never closer than 300
        public void AnimistStandsInsideShroomReach(int reach, int expected) =>
            Assert.That(BotAnimistPolicy.StandoffFor(reach), Is.EqualTo(expected));

        [TestCase(30_000, 0, true)]
        [TestCase(15_001, 0, true)]
        [TestCase(15_000, 0, false)] // about to expire: replace it
        public void VentRefreshWindow(long expiresAt, long now, bool covers) =>
            Assert.That(StillCovers(expiresAt, now), Is.EqualTo(covers));
    }
}
