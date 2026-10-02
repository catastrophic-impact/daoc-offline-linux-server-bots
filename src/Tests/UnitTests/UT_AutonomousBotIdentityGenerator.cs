using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public class UT_AutonomousBotIdentityGenerator
    {
        [TestCase(eRealm.Albion)]
        [TestCase(eRealm.Midgard)]
        [TestCase(eRealm.Hibernia)]
        public void Generate_UsesOnlyClassicSiClassRacePairs(eRealm realm)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var random = new Random((int)realm * 1776);

            for (int index = 0; index < 250; index++)
            {
                eGender gender = index % 2 == 0 ? eGender.Male : eGender.Female;
                var identity = AutonomousBotIdentityGenerator.Generate(realm, gender, names, random);
                Assert.Multiple(() =>
                {
                    Assert.That(identity.Realm, Is.EqualTo(realm));
                    Assert.That(identity.Gender, Is.EqualTo(gender));
                    Assert.That(AutonomousBotIdentityGenerator.GetEraClasses(realm), Does.Contain(identity.CharacterClass));
                    Assert.That(AutonomousBotIdentityGenerator.GetEligibleRaces(identity.CharacterClass), Does.Contain(identity.Race));
                    Assert.That(identity.Name, Does.Match("^[A-Z][a-z]{2,18}$"));
                });
            }

            Assert.That(names.Count, Is.EqualTo(250), "Generated bot names must be unique within a population.");
        }

        [Test]
        public void EraCatalog_ExcludesEveryPostShroudedIslesClassAndRace()
        {
            eCharacterClass[] postSiClasses =
            [
                eCharacterClass.Heretic, eCharacterClass.Valkyrie, eCharacterClass.Bainshee,
                eCharacterClass.Vampiir, eCharacterClass.Warlock,
                eCharacterClass.MaulerAlb, eCharacterClass.MaulerMid, eCharacterClass.MaulerHib,
            ];
            eRace[] postSiRaces =
            [
                eRace.HalfOgre, eRace.Frostalf, eRace.Shar,
                eRace.AlbionMinotaur, eRace.MidgardMinotaur, eRace.HiberniaMinotaur,
            ];

            var classes = new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }
                .SelectMany(AutonomousBotIdentityGenerator.GetEraClasses).ToArray();
            var races = classes.SelectMany(AutonomousBotIdentityGenerator.GetEligibleRaces).Distinct().ToArray();

            Assert.That(classes, Has.None.Matches<eCharacterClass>(postSiClasses.Contains));
            Assert.That(races, Has.None.Matches<eRace>(postSiRaces.Contains));
        }

        [TestCase(eRealm.Albion, eGender.Male)]
        [TestCase(eRealm.Albion, eGender.Female)]
        [TestCase(eRealm.Midgard, eGender.Male)]
        [TestCase(eRealm.Midgard, eGender.Female)]
        [TestCase(eRealm.Hibernia, eGender.Male)]
        [TestCase(eRealm.Hibernia, eGender.Female)]
        public void NamePool_SupportsThousandsWithoutDuplicates(eRealm realm, eGender gender)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var random = new Random((int)realm * 100 + (int)gender);
            for (int index = 0; index < 2_000; index++)
                AutonomousBotIdentityGenerator.Generate(realm, gender, names, random);

            Assert.That(names, Has.Count.EqualTo(2_000));
            Assert.That(names, Has.All.Matches<string>(name => name.Length is >= 3 and <= 19));
        }
    }
}
