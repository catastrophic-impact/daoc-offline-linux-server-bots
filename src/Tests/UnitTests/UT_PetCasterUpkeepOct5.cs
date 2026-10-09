using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Pet casters: quick pet buff cadence for every class, prompt self bladeturn.</summary>
    [TestFixture]
    public class UT_PetCasterUpkeepOct5
    {
        private static Spell Make(string type, string target, int duration, double cast, int id = 4771, bool pulse = false) =>
            new(new DbSpell
            {
                SpellID = id, Name = "test", Type = type, Target = target,
                Duration = duration, CastTime = cast, Frequency = pulse ? 60 : 0, Pulse = pulse ? 1 : 0,
            }, 1);

        [TestCase(eCharacterClass.Spiritmaster)]
        [TestCase(eCharacterClass.Enchanter)]
        [TestCase(eCharacterClass.Cabalist)]
        [TestCase(eCharacterClass.Theurgist)]
        [TestCase(eCharacterClass.Bonedancer)]
        [TestCase(eCharacterClass.Hunter)]
        public void LongPetBuffsFollowTheirCastInsteadOfFifteenSeconds(eCharacterClass characterClass)
        {
            Spell dexQui = Make("DexterityQuicknessBuff", "Pet", 1200, 3.0);
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousPetSupport.IsFastRoutinePetBuff(characterClass, eSpecType.None, dexQui), Is.True);
                Assert.That(AutonomousPetSupport.PetBuffActionCooldown(characterClass, eSpecType.None, dexQui, false), Is.EqualTo(3_500));
                Assert.That(AutonomousPetSupport.PetBuffActionCooldown(characterClass, eSpecType.None, dexQui, true), Is.EqualTo(15_000));
            });
        }

        [Test]
        public void ShortTacticalPetEffectsAndHealsKeepTheirRetryWindow()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousPetSupport.IsFastRoutinePetBuff(eCharacterClass.Enchanter, eSpecType.None,
                    Make("DamageShield", "Pet", 8, 2.5)), Is.False, "short shield");
                Assert.That(AutonomousPetSupport.IsFastRoutinePetBuff(eCharacterClass.Spiritmaster, eSpecType.None,
                    Make("Heal", "Pet", 0, 3.0)), Is.False, "pet heal");
                Assert.That(AutonomousPetSupport.IsFastRoutinePetBuff(eCharacterClass.Spiritmaster, eSpecType.None,
                    Make("StrengthConstitutionBuff", "Self", 1200, 3.0)), Is.False, "not a pet buff");
            });
        }

        [Test]
        public void SelfBladeturnIsTheSelfAbsorbShieldOnly()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BotBrain.IsSelfBladeturn(Make("Bladeturn", "Self", 1200, 4.0)), Is.True, "Protecting Spirit");
                Assert.That(BotBrain.IsSelfBladeturn(Make("Bladeturn", "Group", 6, 4.0, pulse: true)), Is.False, "pulsing group chant");
                Assert.That(BotBrain.IsSelfBladeturn(Make("Bladeturn", "Realm", 30, 0)), Is.False, "realm-target ward");
                Assert.That(BotBrain.IsSelfBladeturn(Make("BaseArmorFactorBuff", "Self", 1200, 3.0)), Is.False);
            });
        }

        [TestCase(true, false, false, false, false, 10_000, ExpectedResult = true, TestName = "Quiet after the fight")]
        [TestCase(true, true, false, false, false, 10_000, ExpectedResult = false, TestName = "Still has aggro")]
        [TestCase(true, false, true, false, false, 10_000, ExpectedResult = false, TestName = "Swinging")]
        [TestCase(true, false, false, true, false, 10_000, ExpectedResult = false, TestName = "Already casting")]
        [TestCase(true, false, false, false, true, 10_000, ExpectedResult = false, TestName = "Stunned or mezzed")]
        [TestCase(true, false, false, false, false, 1_000, ExpectedResult = false, TestName = "Hit a second ago")]
        [TestCase(false, false, false, false, false, 10_000, ExpectedResult = false, TestName = "Dead")]
        public bool SelfBladeturnGoesBackUpAsSoonAsItIsSafe(bool alive, bool aggro, bool attacking, bool busy, bool cc, long since) =>
            BotBrain.SelfBladeturnSafe(alive, aggro, attacking, busy, cc, since);
    }
}
