using System.Linq;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>RvR warband leaders are the bots closest to their realm's border keep.</summary>
    [TestFixture]
    public class UT_RvrLeaderStaging
    {
        private sealed record Candidate(string Name, eRealm Realm, double Minutes);

        private static string[] Order(params Candidate[] candidates) =>
            AutonomousRvrStaging.ClosestToStagingFirst(candidates, c => c.Realm, c => c.Minutes)
                .Select(c => c.Name).ToArray();

        [Test]
        public void ClosestToTheBorderKeepLeadsFirst() =>
            Assert.That(Order(
                    new("enemy frontier roamer", eRealm.Hibernia, 48),
                    new("dungeon", eRealm.Hibernia, 9999),
                    new("by Druim Ligen", eRealm.Hibernia, 1.5),
                    new("home road", eRealm.Hibernia, 6)),
                Is.EqualTo(new[] { "by Druim Ligen", "home road", "enemy frontier roamer", "dungeon" }));

        [Test]
        public void RealmsTakeTurnsSoNoRealmIsCrowdedOut() =>
            Assert.That(Order(
                    new("alb near", eRealm.Albion, 1),
                    new("alb near 2", eRealm.Albion, 2),
                    new("alb near 3", eRealm.Albion, 3),
                    new("mid far", eRealm.Midgard, 40),
                    new("hib mid", eRealm.Hibernia, 10)),
                Is.EqualTo(new[] { "alb near", "mid far", "hib mid", "alb near 2", "alb near 3" }));

        [Test]
        public void EqualDistancesKeepTheShuffledOrder() =>
            Assert.That(Order(
                    new("first", eRealm.Midgard, 5),
                    new("second", eRealm.Midgard, 5),
                    new("third", eRealm.Midgard, 5)),
                Is.EqualTo(new[] { "first", "second", "third" }));

        [Test]
        public void EveryCandidateIsKeptExactlyOnce()
        {
            var candidates = Enumerable.Range(0, 30)
                .Select(i => new Candidate("b" + i, (eRealm)(1 + i % 3), (i * 7) % 11)).ToArray();
            Assert.That(AutonomousRvrStaging.ClosestToStagingFirst(candidates, c => c.Realm, c => c.Minutes),
                Is.EquivalentTo(candidates));
        }
    }
}
