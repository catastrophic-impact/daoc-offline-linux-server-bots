using System;
using System.Linq;
using System.Numerics;
using DOL.Database;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public class UT_RealmBoundarySafety
    {
        [Test]
        public void EveryRealmCanUseEveryFrontierButOnlyItsOwnHomeland()
        {
            foreach (eRealm traveler in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
            foreach (var territory in new[] {
                (Region:(ushort)1, Home:(ushort)0, Owner:eRealm.Albion, Frontiers:new ushort[]{11,12,14,15}),
                (Region:(ushort)100, Home:(ushort)100, Owner:eRealm.Midgard, Frontiers:new ushort[]{111,112,113,115}),
                (Region:(ushort)200, Home:(ushort)200, Owner:eRealm.Hibernia, Frontiers:new ushort[]{210,211,212,214}) })
            {
                Assert.That(AutonomousRealmBoundary.Allows(traveler, territory.Region, territory.Home), Is.EqualTo(traveler == territory.Owner));
                foreach (ushort zone in territory.Frontiers)
                    Assert.That(AutonomousRealmBoundary.Allows(traveler, territory.Region, zone), Is.True);
            }
        }

        [Test]
        public void NearestEnemyBindCannotOverrideFriendlyBind()
        {
            const ushort region = 65001;
            BotReleaseBindPoints.Replace(region, new[] {
                new DbBindPoint { X=1, Y=0, Z=0, Realm=2 },
                new DbBindPoint { X=100, Y=0, Z=0, Realm=1 } });
            Assert.That(BotReleaseBindPoints.Nearest(region,0,0,eRealm.Albion).X, Is.EqualTo(100));
            Assert.That(BotReleaseBindPoints.Nearest(region,0,0,eRealm.Hibernia), Is.Null);
            Assert.That(BotReleaseBindPoints.Nearest(region,0,0).X, Is.EqualTo(1), "Legacy companion default is unchanged");
        }

        [TestCase((ushort)1, eRealm.Albion)]
        [TestCase((ushort)100, eRealm.Midgard)]
        [TestCase((ushort)200, eRealm.Hibernia)]
        public void UntaggedFrontierBindBelongsToHostNotVisitingEnemies(ushort region, eRealm host)
        {
            Assert.That(BotReleaseBindPoints.Owner(region, eRealm.None), Is.EqualTo(host));
            foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
                Assert.That(BotReleaseBindPoints.Owner(region, realm), Is.EqualTo(realm), "Explicit portal bind ownership survives");
        }

        [Test]
        public void UnclassifiedBindFailsClosedForAutonomousButPreservesCompanionDefault()
        {
            const ushort region = 65003;
            BotReleaseBindPoints.Replace(region, new[] { new DbBindPoint { X=1, Realm=0 } });
            foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
                Assert.That(BotReleaseBindPoints.Nearest(region,0,0,realm), Is.Null);
            Assert.That(BotReleaseBindPoints.Nearest(region,0,0), Is.Not.Null);
        }

        [Test, NonParallelizable]
        public void ActualSvasudNeutralBindRejectsBothInvadersButAllowsMidgardAndFriendlyPortal()
        {
            const ushort region = 100;
            try
            {
                BotReleaseBindPoints.Replace(region, new[] { new DbBindPoint { X=765147, Y=668315, Z=5736, Realm=0 } });
                Assert.That(BotReleaseBindPoints.Nearest(region,765147,668315,eRealm.Albion), Is.Null);
                Assert.That(BotReleaseBindPoints.Nearest(region,765147,668315,eRealm.Hibernia), Is.Null);
                Assert.That(BotReleaseBindPoints.Nearest(region,765147,668315,eRealm.Midgard), Is.Not.Null);
                Assert.That(BotReleaseBindPoints.IsEnemyBindPosition(region,765147,668315,5739,eRealm.Hibernia), Is.True);
                Assert.That(BotReleaseBindPoints.IsEnemyBindPosition(region,765147,668315,5739,eRealm.Midgard), Is.False);
                Assert.That(BotReleaseBindPoints.IsEnemyBindPosition(region,765147,668315,6000,eRealm.Hibernia), Is.False);
                BotReleaseBindPoints.Replace(region, new[] {
                    new DbBindPoint { X=765147, Y=668315, Z=5736, Realm=0 },
                    new DbBindPoint { X=596055, Y=581400, Z=6031, Realm=3 } });
                Assert.That(BotReleaseBindPoints.Nearest(region,765147,668315,eRealm.Hibernia).X, Is.EqualTo(596055));
            }
            finally { BotReleaseBindPoints.Replace(region, Array.Empty<DbBindPoint>()); }
        }

        [TestCase(50, 128)]
        [TestCase(58, 192)]
        public void ConstrainedKeepWingsRemainSeparated(int keep, int cap)
        {
            int pair=AutonomousRvrRally.ChooseOrientation(keep,1,eRealm.Albion,cap==192,Vector3.Zero,null,null,eRealm.Midgard);
            var a=Enumerable.Range(0,cap).Select(slot=>AutonomousRvrRally.AttackerPost(Vector3.Zero,AutonomousRvrRally.OrientationFor(pair,true),true,slot));
            var b=Enumerable.Range(0,cap).Select(slot=>AutonomousRvrRally.AttackerPost(Vector3.Zero,AutonomousRvrRally.OrientationFor(pair,false),false,slot)).ToArray();
            foreach(var point in a)
                Assert.That(b.Min(other=>Vector3.Distance(point,other)), Is.GreaterThan(5000));
        }
    }
}
