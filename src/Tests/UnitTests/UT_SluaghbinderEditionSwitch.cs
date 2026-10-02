using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    /// <summary>
    /// Offline DAoC 0.33 editions share one server. classes/enable_sluaghbinder is on for 0.33b
    /// (and by default) and off for the 0.33 "no custom class" edition.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class UT_SluaghbinderEditionSwitch
    {
        private bool _original;

        [SetUp]
        public void Remember() => _original = Properties.ENABLE_SLUAGHBINDER;

        [TearDown]
        public void Restore() => Properties.ENABLE_SLUAGHBINDER = _original;

        [Test]
        public void EnabledByDefault()
        {
            Assert.That(_original, Is.True);
            Assert.That(AutonomousBotIdentityGenerator.GetEraClasses(eRealm.Hibernia), Does.Contain(eCharacterClass.Sluaghbinder));
        }

        [Test]
        public void EnabledRollsAreUnchanged()
        {
            Properties.ENABLE_SLUAGHBINDER = true;
            var first = Roll(new Random(4242));
            var second = Roll(new Random(4242));
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Does.Contain(eCharacterClass.Sluaghbinder), "an enabled Hibernian pool still rolls the class");
        }

        [Test]
        public void DisabledNeverRollsListsOrHelpsWithTheClass()
        {
            Properties.ENABLE_SLUAGHBINDER = false;
            Assert.That(Roll(new Random(4242)), Does.Not.Contain(eCharacterClass.Sluaghbinder));
            Assert.That(AutonomousBotIdentityGenerator.GetEraClasses(eRealm.Hibernia), Does.Not.Contain(eCharacterClass.Sluaghbinder));
            Assert.That(AutonomousBotIdentityGenerator.GetEraClasses(eRealm.Hibernia), Has.Count.EqualTo(13));
            Assert.That(AutonomousBotIdentityGenerator.IsClassAvailable(eCharacterClass.Sluaghbinder), Is.False);
            Assert.That(AutonomousBotIdentityGenerator.IsClassAvailable(eCharacterClass.Druid), Is.True);
            Assert.Throws<ArgumentException>(() =>
                AutonomousBotIdentityGenerator.GenerateForClass(eRealm.Hibernia, eGender.Male, eCharacterClass.Sluaghbinder));
        }

        [Test]
        public void OtherRealmsAreUnaffected()
        {
            Properties.ENABLE_SLUAGHBINDER = true;
            var albion = AutonomousBotIdentityGenerator.GetEraClasses(eRealm.Albion).ToArray();
            var midgard = AutonomousBotIdentityGenerator.GetEraClasses(eRealm.Midgard).ToArray();
            Properties.ENABLE_SLUAGHBINDER = false;
            Assert.That(AutonomousBotIdentityGenerator.GetEraClasses(eRealm.Albion), Is.EqualTo(albion));
            Assert.That(AutonomousBotIdentityGenerator.GetEraClasses(eRealm.Midgard), Is.EqualTo(midgard));
        }

        private static List<eCharacterClass> Roll(Random random)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return Enumerable.Range(0, 700)
                .Select(index => AutonomousBotIdentityGenerator.Generate(eRealm.Hibernia,
                    index % 2 == 0 ? eGender.Male : eGender.Female, names, random).CharacterClass)
                .ToList();
        }
    }
}
