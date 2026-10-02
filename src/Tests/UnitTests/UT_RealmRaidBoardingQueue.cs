using System.Linq;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_RealmRaidBoardingQueue
    {
        [Test]
        public void DifferentPartiesShareOneLaneAndKeepTheDepartureGap()
        {
            var queue = new RealmRaidBoardingQueue();
            object first = new(), second = new(), third = new();
            Assert.That(queue.TryEnter(first, 1000), Is.True);
            Assert.That(queue.TryEnter(second, 1000), Is.False);
            Assert.That(queue.TryEnter(third, 1000), Is.False);
            Assert.That(queue.TryEnter(first, 2000), Is.True);
            queue.Leave(first, 2000);
            Assert.That(queue.TryEnter(second, 2749), Is.False);
            Assert.That(queue.TryEnter(second, 2750), Is.True);
            Assert.That(queue.TryEnter(first, 2750), Is.False, "A party cannot jump ahead of other waiting parties");
            queue.Leave(second, 3000);
            Assert.That(queue.TryEnter(third, 3750), Is.True);
        }

        [Test]
        public void StuckOrCancelledRiderCannotRenewAdmissionForever()
        {
            var queue = new RealmRaidBoardingQueue();
            object stuck = new(), next = new();
            Assert.That(queue.TryEnter(stuck, 1000), Is.True);
            Assert.That(queue.TryEnter(next, 12000), Is.False);
            Assert.That(queue.TryEnter(stuck, 15999), Is.True);
            Assert.That(queue.TryEnter(stuck, 16000), Is.False);
            Assert.That(queue.TryEnter(next, 16000), Is.True);
        }

        [Test]
        public void QueueIsBoundedAndInactiveWaitersExpire()
        {
            var queue = new RealmRaidBoardingQueue();
            object[] riders = Enumerable.Range(0, 31).Select(_ => new object()).ToArray();
            for (int i = 0; i < riders.Length; i++)
                Assert.That(queue.TryEnter(riders[i], 1000), Is.EqualTo(i == 0));
            Assert.That(queue.TryEnter(riders[30], 16000), Is.True);
            Assert.That(queue.TryEnter(null, 16000), Is.False);
        }
    }
}
