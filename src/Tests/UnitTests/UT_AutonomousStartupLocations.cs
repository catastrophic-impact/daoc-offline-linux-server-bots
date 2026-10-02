using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.GameEvents;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public class UT_AutonomousStartupLocations
    {
        [TestCase(eRealm.Albion, 1, 1, 1, 51)]
        [TestCase(eRealm.Midgard, 5, 15, 100, 151)]
        [TestCase(eRealm.Hibernia, 9, 30, 200, 181)]
        public void FilterClassicSiLocationsForAutonomous_UsesOnlyOwnRealmClassicAndSiStarts(
            eRealm realm, int raceId, int classId, int classicRegion, int siRegion)
        {
            var source = new List<StartupLocation>
            {
                Location(realm, raceId, classId, classicRegion, 10_000, 20_000, 1_000),
                Location(0, 0, 0, siRegion, 30_000, 40_000, 1_100),
                Location((eRealm)(((int)realm % 3) + 1), 0, 0, classicRegion, 50_000, 60_000, 1_200),
                Location(realm, raceId, classId, 27, 70_000, 80_000, 1_300, clientRegion: 27),
                Location(realm, raceId, classId, 10, 90_000, 100_000, 1_400),
            };

            StartupLocation[] result = StartupLocations
                .FilterClassicSiLocationsForAutonomous(source, realm, raceId, classId)
                .ToArray();

            Assert.That(result.Select(location => location.Region), Is.EquivalentTo(new[] { classicRegion, siRegion }));
            Assert.That(result, Has.All.Matches<StartupLocation>(location => location.ClientRegionID != 27));
        }

        [Test]
        public void FilterClassicSiLocationsForAutonomous_DeduplicatesClassAndRaceRowsBeforeRandomSelection()
        {
            var source = new List<StartupLocation>
            {
                Location(eRealm.Albion, 0, 0, 1, 10_000, 20_000, 1_000),
                Location(eRealm.Albion, 1, 1, 1, 10_000, 20_000, 1_000),
                Location(eRealm.Albion, 1, 1, 51, 30_000, 40_000, 1_100),
            };

            StartupLocation[] result = StartupLocations
                .FilterClassicSiLocationsForAutonomous(source, eRealm.Albion, 1, 1)
                .ToArray();

            Assert.That(result, Has.Length.EqualTo(2));
            Assert.That(result.Single(location => location.Region == 1).ClassID, Is.EqualTo(1));
        }

        [Test]
        public void FilterClassicSiLocationsForAutonomous_RejectsTutorialPostSiAndIncompatibleLocations()
        {
            var source = new List<StartupLocation>
            {
                Location(eRealm.Albion, 1, 1, 1, 10_000, 20_000, 1_000, minVersion: 169),
                Location(eRealm.Albion, 1, 1, 27, 20_000, 30_000, 1_000, clientRegion: 27),
                Location(eRealm.Albion, 1, 1, 10, 30_000, 40_000, 1_000),
                Location(eRealm.Albion, 2, 1, 1, 40_000, 50_000, 1_000),
                Location(eRealm.Albion, 1, 2, 1, 50_000, 60_000, 1_000),
                Location(eRealm.Albion, 1, 1, 1, 0, 0, 0),
            };

            IReadOnlyList<StartupLocation> result = StartupLocations
                .FilterClassicSiLocationsForAutonomous(source, eRealm.Albion, 1, 1);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ChooseUniformAutonomousLocation_DoesNotPreferFirstRow()
        {
            StartupLocation classicVillage = Location(eRealm.Albion, 1, 1, 1, 10_000, 20_000, 1_000);
            StartupLocation siVillage = Location(eRealm.Albion, 1, 1, 51, 30_000, 40_000, 1_100);

            Assert.Multiple(() =>
            {
                Assert.That(StartupLocations.ChooseUniformAutonomousLocation(new[] { classicVillage, siVillage }, new IndexRandom(0)), Is.SameAs(classicVillage));
                Assert.That(StartupLocations.ChooseUniformAutonomousLocation(new[] { classicVillage, siVillage }, new IndexRandom(1)), Is.SameAs(siVillage));
            });
        }

        private static StartupLocation Location(
            eRealm realm, int raceId, int classId, int region, int x, int y, int z,
            int heading = 0, int clientRegion = 0, int minVersion = 0) => new()
            {
                RealmID = (int)realm,
                RaceID = raceId,
                ClassID = classId,
                Region = region,
                XPos = x,
                YPos = y,
                ZPos = z,
                Heading = heading,
                ClientRegionID = clientRegion,
                MinVersion = minVersion,
            };

        private sealed class IndexRandom(int index) : Random
        {
            public override int Next(int maxValue) => Math.Clamp(index, 0, maxValue - 1);
        }
    }
}
