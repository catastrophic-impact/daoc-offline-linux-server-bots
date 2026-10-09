using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_GameWideAnnouncements
    {
        [TearDown]
        public void After()
        {
            GameWideAnnouncements.RvrBattleground = true;
            GameWideAnnouncements.PveRealmEvents = true;
        }

        [Test]
        public void EachCategoryHasItsOwnSwitch()
        {
            Assert.That(GameWideAnnouncements.Enabled(AnnouncementKind.RvrBattleground), Is.True);
            Assert.That(GameWideAnnouncements.Enabled(AnnouncementKind.PveRealmEvent), Is.True);

            GameWideAnnouncements.RvrBattleground = false;
            Assert.That(GameWideAnnouncements.Enabled(AnnouncementKind.RvrBattleground), Is.False);
            Assert.That(GameWideAnnouncements.Enabled(AnnouncementKind.PveRealmEvent), Is.True);

            GameWideAnnouncements.RvrBattleground = true;
            GameWideAnnouncements.PveRealmEvents = false;
            Assert.That(GameWideAnnouncements.Enabled(AnnouncementKind.RvrBattleground), Is.True);
            Assert.That(GameWideAnnouncements.Enabled(AnnouncementKind.PveRealmEvent), Is.False);
        }

        [Test]
        public void BattlegroundSiegeAnnouncesOncePerQuietSpell()
        {
            long quiet = AutonomousRvrEventLayer.BattlegroundSiegeQuietMilliseconds;
            Assert.That(AutonomousRvrEventLayer.BattlegroundSiegeIsNew(0, 10_000), Is.True);
            Assert.That(AutonomousRvrEventLayer.BattlegroundSiegeIsNew(10_000, 10_000 + quiet - 1), Is.False);
            Assert.That(AutonomousRvrEventLayer.BattlegroundSiegeIsNew(10_000, 10_000 + quiet), Is.True);
        }

        [Test]
        public void BattlegroundSiegeLinesNameTheHolder()
        {
            Assert.That(GameWideAnnouncements.BattlegroundSiegeStarted(eRealm.Albion, "Dun Abermenai", "Abermenai", eRealm.None),
                Is.EqualTo("Albion's forces are assaulting the unclaimed Dun Abermenai in Abermenai!"));
            Assert.That(GameWideAnnouncements.BattlegroundSiegeStarted(eRealm.Midgard, "Dun Abermenai", "Abermenai", eRealm.Hibernia),
                Is.EqualTo("Midgard's forces have laid siege to Hibernia's Dun Abermenai in Abermenai!"));
        }
    }
}
