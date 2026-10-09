using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>New Skald bots can roll a two-handed build.</summary>
    [TestFixture]
    public class UT_SkaldTwoHanded
    {
        [Test]
        public void SkaldsChooseBetweenOneHandAndShieldAndTwoHanded()
        {
            Assert.That(BotSpec.GetSpecializationChoices(eCharacterClass.Skald),
                Is.EqualTo(new[] { eSpecType.OneHandAndShield, eSpecType.TwoHanded }));
        }

        [Test]
        public void WarriorsCanRollATwoHandedBuildToo()
        {
            Assert.That(BotSpec.GetSpecializationChoices(eCharacterClass.Warrior),
                Is.EqualTo(new[] { eSpecType.OneHandAndShield, eSpecType.TwoHanded }));
            for (int i = 0; i < 20; i++)
            {
                Assert.That(new WarriorBotSpec(eSpecType.TwoHanded).Is2H, Is.True);
                Assert.That(new WarriorBotSpec(eSpecType.OneHandAndShield).Is2H, Is.False);
            }
        }

        [Test]
        public void ATwoHandedBuildFightsWithItsOneHanderUntilItHasAUsableTwoHander()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DOL.AI.Brain.BotBrain.PreferTwoHandedSlot(false, true, true), Is.False, "no two-hander yet: one-hander");
                Assert.That(DOL.AI.Brain.BotBrain.PreferTwoHandedSlot(true, true, true), Is.True, "has a two-hander: uses it");
                Assert.That(DOL.AI.Brain.BotBrain.PreferTwoHandedSlot(true, true, false), Is.False, "shield build keeps its one-hander");
                Assert.That(DOL.AI.Brain.BotBrain.PreferTwoHandedSlot(true, false, false), Is.True, "only a two-hander: uses it");
                Assert.That(GameBot.PlannedTwoHandedUnlockLevel(eCharacterClass.Skald, eObjectType.Sword), Is.EqualTo(1),
                    "Skalds can use two-handers from level 1");
                Assert.That(GameBot.ShouldUsePlannedTwoHandedPrimary(eCharacterClass.Paladin, 4, true), Is.False,
                    "Paladins wait for their two-handed ability");
            });
        }

        [Test]
        public void TheTwoHandedSkaldBuildUsesATwoHander()
        {
            for (int i = 0; i < 20; i++)
            {
                var twoHanded = new SkaldBotSpec(eSpecType.TwoHanded);
                var shield = new SkaldBotSpec(eSpecType.OneHandAndShield);
                Assert.Multiple(() =>
                {
                    Assert.That(twoHanded.Is2H, Is.True);
                    Assert.That(twoHanded.WeaponOneType, Is.AnyOf(eObjectType.Sword, eObjectType.Axe, eObjectType.Hammer));
                    Assert.That(shield.Is2H, Is.False);
                });
            }
        }
    }
}
