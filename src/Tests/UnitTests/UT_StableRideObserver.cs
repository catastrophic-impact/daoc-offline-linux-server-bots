using System;
using System.Collections.Generic;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public sealed class UT_StableRideObserver
    {
        [Test]
        public void CreatesBothObjectsBeforeAttachingAndIgnoresRecursiveCreateHooks()
        {
            var observer = new StableRideObserver();
            var packets = new List<string>();
            observer.Refresh(0, false, () =>
            {
                packets.Add("horse");
                observer.Refresh(0, true, () => Assert.Fail("recursive creation"), () => Assert.Fail("early mount"));
                packets.Add("rider");
                observer.Refresh(0, true, () => Assert.Fail("recursive creation"), () => Assert.Fail("early mount"));
                packets.Add("equipment");
            }, () => packets.Add("mount"));
            Assert.That(packets, Is.EqualTo(new[] { "horse", "rider", "equipment", "mount" }));
        }

        [Test]
        public void SafetyRefreshIsBoundedButNotPermanentlySuppressed()
        {
            var observer = new StableRideObserver();
            int creates = 0, attaches = 0;
            observer.Refresh(100, false, () => creates++, () => attaches++);
            for (int now = 101; now < 5100; now++)
                observer.Refresh(now, false, () => creates++, () => attaches++);
            Assert.That(attaches, Is.EqualTo(1));
            observer.Refresh(5100, false, () => creates++, () => attaches++);
            Assert.That(attaches, Is.EqualTo(2));
            Assert.That(creates, Is.EqualTo(1), "Retries must not repeatedly recreate horses.");
        }

        [Test]
        public void RecreatedObjectsAndPositionUpdatesCanImmediatelyReattach()
        {
            var observer = new StableRideObserver();
            int creates = 0, attaches = 0;
            observer.Refresh(100, false, () => creates++, () => attaches++);
            observer.Refresh(100, true, () => creates++, () => attaches++);
            Assert.That(creates, Is.EqualTo(1));
            Assert.That(attaches, Is.EqualTo(2));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FailedInitialCreateOrAttachCanRetry(bool failCreate)
        {
            var observer = new StableRideObserver();
            Assert.Throws<InvalidOperationException>(() => observer.Refresh(100, false,
                () => { if (failCreate) throw new InvalidOperationException(); },
                () => throw new InvalidOperationException()));
            int creates = 0, attaches = 0;
            observer.Refresh(101, false, () => creates++, () => attaches++);
            Assert.That(creates, Is.EqualTo(1));
            Assert.That(attaches, Is.EqualTo(1));
        }

        [Test]
        public void NewViewerOrNewTripGetsItsOwnInitialization()
        {
            int creates = 0, attaches = 0;
            foreach (var observer in new[] { new StableRideObserver(), new StableRideObserver() })
                observer.Refresh(100, false, () => creates++, () => attaches++);
            Assert.That(creates, Is.EqualTo(2));
            Assert.That(attaches, Is.EqualTo(2));
        }

        [Test]
        public void FailedRefreshDoesNotSuppressRetryOrRecreateExistingActors()
        {
            var observer = new StableRideObserver();
            observer.Refresh(0, false, () => { }, () => { });
            Assert.Throws<InvalidOperationException>(() => observer.Refresh(5000, false,
                () => Assert.Fail("already created"), () => throw new InvalidOperationException()));
            int attaches = 0;
            observer.Refresh(5001, false, () => Assert.Fail("already created"), () => attaches++);
            Assert.That(attaches, Is.EqualTo(1));
        }
    }
}
