using System.Linq;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Summoner's Hall and Darkness Falls as neutral realm events.</summary>
    [TestFixture]
    public class UT_NeutralRaidEventsOct6
    {
        private static DbZonePoint Edge(int id, ushort from, ushort to, ushort realm = 0) =>
            new() { Id = (ushort)id, SourceRegion = from, TargetRegion = to, Realm = realm };

        [Test]
        public void EveryRealmHasBothNeutralEventsAndEachEventHasAHub()
        {
            foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
            {
                var neutral = AutonomousRealmRaid.Definitions.Where(d => d.Realm == realm && d.IsNeutral).ToArray();
                Assert.That(neutral.Select(d => d.Region), Is.EquivalentTo(new ushort[] { 248, 249 }), realm.ToString());
                foreach (var definition in neutral)
                {
                    Assert.That(definition.IsDungeon, Is.True);
                    Assert.That(definition.Objectives.Last(), Is.EqualTo(definition.Region == 248 ? "Grand Summoner Govannon" : "Legion"));
                }
            }
            foreach (var definition in AutonomousRealmRaid.Definitions)
                Assert.That(RealmRaidMuster.Hubs.Count(h => h.Event == definition.Id), Is.GreaterThanOrEqualTo(1), definition.Id);
        }

        [Test]
        public void SummonersHallIsReachedThroughTheRealmsApproachDungeon()
        {
            var crossings = new[]
            {
                Edge(88, 1, 277), Edge(90, 277, 248),     // Albion: Hall of the Corrupt
                Edge(93, 100, 246), Edge(92, 246, 248),   // Midgard: Dodens Gruva
                Edge(94, 200, 276), Edge(96, 276, 248),   // Hibernia: Marfach Caverns
            };
            var albion = RealmRaidNeutralEvents.Entrances(crossings, eRealm.Albion, 1, 248).ToArray();
            Assert.That(albion.Length, Is.EqualTo(1));
            Assert.That(albion[0].Outer.Id, Is.EqualTo(88));
            Assert.That(albion[0].Inner.Id, Is.EqualTo(90));
            Assert.That(RealmRaidNeutralEvents.Entrances(crossings, eRealm.Midgard, 100, 248).Single().Inner.Id, Is.EqualTo(92));
        }

        [Test]
        public void DarknessFallsUsesOnlyTheRealmsOwnEntrances()
        {
            var crossings = new[]
            {
                Edge(81, 1, 249, 1), Edge(81, 1, 249, 2), Edge(79, 1, 249, 1),
                Edge(84, 100, 249, 2), Edge(82, 100, 249, 2), Edge(87, 200, 249),
            };
            var albion = RealmRaidNeutralEvents.Entrances(crossings, eRealm.Albion, 1, 249).ToArray();
            Assert.That(albion.Select(e => (int)e.Outer.Id), Is.EquivalentTo(new[] { 81, 79 }), "the Midgard row of 81 is not Albion's");
            Assert.That(albion.All(e => e.Outer == e.Inner), Is.True, "Darkness Falls is entered directly");
            Assert.That(RealmRaidNeutralEvents.Entrances(crossings, eRealm.Hibernia, 200, 249).Single().Outer.Id, Is.EqualTo(87));
        }

        [TestCase(eRealm.Albion, 81, 79)]
        [TestCase(eRealm.Midgard, 84, 82)]
        [TestCase(eRealm.Hibernia, 87, 86)]
        public void OpenGroundEntrancesComeBeforeKeepTowers(eRealm realm, int open, int tower) =>
            Assert.That(RealmRaidNeutralEvents.EntrancePreference(realm, open), Is.LessThan(RealmRaidNeutralEvents.EntrancePreference(realm, tower)));

        [TestCase((ushort)248, true, true)]
        [TestCase((ushort)249, true, true)]
        [TestCase((ushort)277, false, true)]
        [TestCase((ushort)246, false, true)]
        [TestCase((ushort)276, false, true)]
        [TestCase((ushort)191, false, false)]
        [TestCase((ushort)1, false, false)]
        public void NeutralAndBattleRegions(ushort region, bool neutral, bool battle)
        {
            Assert.That(RealmRaidNeutralEvents.IsNeutralRegion(region), Is.EqualTo(neutral));
            Assert.That(RealmRaidNeutralEvents.IsBattleRegion(region), Is.EqualTo(battle));
        }

        [Test]
        public void ObjectivesMatchByNameIgnoringCase()
        {
            string[] objectives = RealmRaidNeutralEvents.Objectives(249);
            Assert.That(RealmRaidNeutralEvents.IsObjective(objectives, "prince ba'alorien"), Is.True);
            Assert.That(RealmRaidNeutralEvents.IsObjective(objectives, "Duke Bimure"), Is.False);
            Assert.That(RealmRaidNeutralEvents.Objectives(191), Is.Null, "epic dungeons keep clearing every monster");
        }

        [Test]
        public void AHorseLandsOnTheGroundUnderItsRecordedEndPoint()
        {
            var landing = new System.Numerics.Vector3(764734, 675456, 5694); // Svasud Faste, ~450 above the floor
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousStableRoutePlanner.IsGroundLanding(landing, new(764740, 675450, 5152)), Is.True);
                Assert.That(AutonomousStableRoutePlanner.IsGroundLanding(landing, new(764900, 675456, 5152)), Is.False, "never a sideways jump");
                Assert.That(AutonomousStableRoutePlanner.IsGroundLanding(landing, new(764734, 675456, 4900)), Is.False, "never a floor far below");
            });
        }

        [Test]
        public void OrdinaryDarknessFallsGoalsAreNotRaidDestinations() =>
            Assert.That(AutonomousRealmRaid.IsActiveRaidDestination(249, 44310, 37978), Is.False);

        [TestCase((byte)75, true, 60, ExpectedResult = (byte)60, TestName = "A level 75 summoner fights at 60 during a raid")]
        [TestCase((byte)55, true, 60, ExpectedResult = (byte)55, TestName = "Lower encounters keep their level")]
        [TestCase((byte)75, false, 60, ExpectedResult = (byte)75, TestName = "Ordinary monsters are untouched")]
        [TestCase((byte)83, true, 0, ExpectedResult = (byte)83, TestName = "A cap of 0 changes nothing")]
        public byte EncounterLevelsAreCappedForRaids(byte level, bool encounter, int cap) =>
            RealmRaidNeutralEvents.CappedLevel(level, encounter, cap);
    }
}
