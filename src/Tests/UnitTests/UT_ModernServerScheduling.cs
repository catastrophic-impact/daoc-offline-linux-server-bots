using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture, NonParallelizable]
    public sealed class UT_ModernServerScheduling
    {
        private sealed class TestServer : GameServer
        {
            public DOL.Database.IObjectDatabase TestDatabase;
            protected override DOL.Database.IObjectDatabase DataBaseImpl => TestDatabase;
        }
        private GameServer _previousServer;

        [SetUp]
        public void SetUp()
        {
            _previousServer = GameServer.Instance;
            var server = (TestServer)RuntimeHelpers.GetUninitializedObject(typeof(TestServer));
            server.TestDatabase = DispatchProxy.Create<DOL.Database.IObjectDatabase,
                DOL.UnitTests.UT_UnobservedConcentration.EmptyReads>();
            GameServer.LoadTestDouble(server);
        }

        [TearDown]
        public void TearDown() => GameServer.LoadTestDouble(_previousServer);

        private sealed class RegistryActor : GameBot
        {
            private RegistryActor() : base((OfflineWorldBotRecord)null) { }
            public override eObjectState ObjectState { get; set; }
        }

        private sealed class TestRam : GameSiegeRam
        {
            public override eObjectState ObjectState { get; set; }
        }

        [Test]
        public void RamOwnershipIndexTracksTransferReleaseAndInactiveActors()
        {
            var first = (RegistryActor)RuntimeHelpers.GetUninitializedObject(typeof(RegistryActor));
            var second = (RegistryActor)RuntimeHelpers.GetUninitializedObject(typeof(RegistryActor));
            var ram = (TestRam)RuntimeHelpers.GetUninitializedObject(typeof(TestRam));
            ram.ObjectState = GameObject.eObjectState.Active;
            try
            {
                Assert.That(AutonomousSiegeOwnership.Snapshot(first), Is.Empty);
                ram.Owner = first;
                ram.Owner = first;
                Assert.That(AutonomousSiegeOwnership.Snapshot(first), Is.EqualTo(new[] { ram }));
                ram.Owner = second;
                Assert.That(AutonomousSiegeOwnership.Snapshot(first), Is.Empty);
                Assert.That(AutonomousSiegeOwnership.Snapshot(second), Is.EqualTo(new[] { ram }));
                ram.ObjectState = GameObject.eObjectState.Inactive;
                Assert.That(AutonomousSiegeOwnership.Snapshot(second), Is.Empty);
                ram.ObjectState = GameObject.eObjectState.Active;
                Assert.That(AutonomousSiegeOwnership.Snapshot(second), Is.EqualTo(new[] { ram }));
                ram.Owner = null;
                Assert.That(AutonomousSiegeOwnership.Snapshot(second), Is.Empty);
            }
            finally { ram.Owner = null; }
        }

        [Test]
        public void PopulationInputIsSampledOnceWithoutChangingExactLiveCounts()
        {
            var active = (ConcurrentDictionary<long, GameBot>)typeof(AutonomousBotRegistry)
                .GetField("Active", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var previous = active.ToArray();
            try
            {
                active.Clear();
                for (int i = 0; i < 4500; i++)
                {
                    var actor = (RegistryActor)RuntimeHelpers.GetUninitializedObject(typeof(RegistryActor));
                    actor.ObjectState = GameObject.eObjectState.Active;
                    active[i] = actor;
                }
                Assert.That(AutonomousBotRegistry.Count, Is.EqualTo(4500));
                long oldAllocation = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                long oldSum = 0;
                for (int i = 0; i < 4500; i++) oldSum += AutonomousBotRegistry.Count;
                double oldMs = watch.Elapsed.TotalMilliseconds;
                oldAllocation = GC.GetAllocatedBytesForCurrentThread() - oldAllocation;
                long newAllocation = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                AutonomousBotRegistry.SamplePopulationNow();
                long newSum = 0;
                for (int i = 0; i < 4500; i++) newSum += AutonomousBotRegistry.PopulationForBrainTick;
                double newMs = watch.Elapsed.TotalMilliseconds;
                newAllocation = GC.GetAllocatedBytesForCurrentThread() - newAllocation;
                Assert.That(newSum, Is.EqualTo(oldSum));
                TestContext.WriteLine($"4500 population reads: old {oldMs:F2} ms/{oldAllocation:N0} bytes; sampled {newMs:F2} ms/{newAllocation:N0} bytes");
                active[0].ObjectState = GameObject.eObjectState.Inactive;
                Assert.That(AutonomousBotRegistry.Count, Is.EqualTo(4499), "Population admission must remain exact between ticks");
                AutonomousBotRegistry.SamplePopulationNow();
                Assert.That(AutonomousBotRegistry.PopulationForBrainTick, Is.EqualTo(4499));
            }
            finally
            {
                active.Clear();
                foreach (var entry in previous) active[entry.Key] = entry.Value;
                AutonomousBotRegistry.SamplePopulationNow();
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(12)]
        public void WorkerBatchesExecuteEveryItemExactlyOnce(int parallelism)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                using var pool = new GameLoopThreadPoolMultiThreaded(parallelism);
                pool.Init();
                var work = Enumerable.Range(0, 6000).ToList();
                foreach (int count in new[] { 0, 1, 2, 11, 12, 13, 4500, 6000, 1, 17 })
                {
                    var seen = new int[work.Count];
                    pool.ExecuteForEach(work, count, i => Interlocked.Increment(ref seen[i]));
                    Assert.That(seen.Take(count), Is.All.EqualTo(1));
                    Assert.That(seen.Skip(count), Is.All.Zero);
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        [Test]
        public void WorkerContextFlowsAndUnevenRoundsDoNotOverlap()
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            var ambient = new AsyncLocal<int>();
            try
            {
                using var pool = new GameLoopThreadPoolMultiThreaded(12);
                pool.Init();
                var work = Enumerable.Range(0, 4500).ToList();
                int wrongContext = 0;
                var threads = new ConcurrentDictionary<int, byte>();
                var watch = Stopwatch.StartNew();
                for (int round = 1; round <= 30; round++)
                {
                    ambient.Value = round;
                    int expected = round;
                    int completed = 0;
                    pool.ExecuteForEach(work, work.Count, i =>
                    {
                        if (ambient.Value != expected) Interlocked.Increment(ref wrongContext);
                        threads.TryAdd(Environment.CurrentManagedThreadId, 0);
                        if (i % 401 == 0) Thread.SpinWait(1000);
                        Interlocked.Increment(ref completed);
                    });
                    Assert.That(completed, Is.EqualTo(work.Count));
                }
                TestContext.WriteLine($"4500-item uneven scheduler: 30 batches, {watch.Elapsed.TotalMilliseconds:F1} ms total; {threads.Count} threads");
                Assert.That(threads.Count, Is.GreaterThan(1));
                Assert.That(wrongContext, Is.Zero);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        [Test]
        public void WorkerFaultIsReturnedAfterBarrierWithoutReplayingActions()
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                using var pool = new GameLoopThreadPoolMultiThreaded(4);
                pool.Init();
                var work = Enumerable.Range(0, 100).ToList();
                var seen = new int[100];
                Assert.Throws<InvalidOperationException>(() => pool.ExecuteForEach(work, 100, i =>
                {
                    Interlocked.Increment(ref seen[i]);
                    if (i == 37) throw new InvalidOperationException("test fault");
                }));
                Assert.That(seen, Is.All.EqualTo(1));
                pool.ExecuteForEach(work, 100, i => Interlocked.Increment(ref seen[i]));
                Assert.That(seen, Is.All.EqualTo(2));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        [Test]
        public void ZeroWorkersIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GameLoopThreadPoolMultiThreaded(0));
        }

        [Test]
        public void SnapshotWorkerIsNonBlockingSingleFlightAndDoesNotInheritGameContext()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var ambient = new AsyncLocal<int> { Value = 42 };
            using var worker = new BackgroundSnapshotBuilder<int[], (int Sum, int Ambient, bool NoContext)>("SnapshotTest", values =>
            {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
                return (values.Sum(), ambient.Value, SynchronizationContext.Current == null);
            });
            try
            {
                Assert.That(worker.TryRequest([1, 2, 3]), Is.True);
                Assert.That(entered.Wait(5000), Is.True);
                for (int i = 0; i < 4500; i++) Assert.That(worker.TryRequest([9]), Is.False);
                Assert.That(worker.TryTake(out _, out _), Is.False);
            }
            finally { release.Set(); }
            (int Sum, int Ambient, bool NoContext) result = default;
            Exception error = null;
            Assert.That(SpinWait.SpinUntil(() => worker.TryTake(out result, out error), 5000), Is.True);
            Assert.That(error, Is.Null);
            Assert.That(result, Is.EqualTo((6, 0, true)));
            Assert.That(worker.TryTake(out _, out _), Is.False);
            Assert.That(worker.TryRequest([4, 5]), Is.True);
            Assert.That(SpinWait.SpinUntil(() => worker.TryTake(out result, out error), 5000), Is.True);
            Assert.That(result.Sum, Is.EqualTo(9));
        }

        [Test]
        public void SnapshotFailureCanRetryAndDisposeRejectsNewWork()
        {
            using var worker = new BackgroundSnapshotBuilder<int, int>("SnapshotFailureTest", value =>
                value == 0 ? throw new InvalidOperationException("test") : value * 2);
            Assert.That(worker.TryRequest(0), Is.True);
            Exception error = null;
            Assert.That(SpinWait.SpinUntil(() => worker.TryTake(out _, out error), 5000), Is.True);
            Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(worker.TryRequest(3), Is.True);
            int result = 0;
            Assert.That(SpinWait.SpinUntil(() => worker.TryTake(out result, out error), 5000), Is.True);
            Assert.That(error, Is.Null);
            Assert.That(result, Is.EqualTo(6));
            worker.Dispose();
            Assert.That(worker.TryRequest(7), Is.False);
        }

        [Test]
        public void PlanningAndWorkerEntryPointsLoadAndJit()
        {
            Assert.DoesNotThrow(() => RuntimeHelpers.PrepareMethod(typeof(AutonomousBotGroupCoordinator)
                .GetMethod(nameof(AutonomousBotGroupCoordinator.PrepareCoordinatorTick)).MethodHandle));
            foreach (string name in new[] { "PrepareCampPlanningTick", "StopCampPlanning", "CaptureCampMonsters", "BuildCampCatalogDraft", "ProjectCamp" })
            {
                MethodInfo method = typeof(AutonomousWorldBotController).GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null, name);
                Assert.DoesNotThrow(() => RuntimeHelpers.PrepareMethod(method.MethodHandle), name);
            }
        }

        [Test]
        public void GroupMaintenanceDoesNotRepeatGlobalWorkWithinOneTick()
        {
            FieldInfo gate = typeof(AutonomousBotGroupCoordinator).GetField("_lastMaintenanceTick",
                BindingFlags.Static | BindingFlags.NonPublic);
            long previous = (long)gate.GetValue(null);
            try
            {
                gate.SetValue(null, GameLoop.GameLoopTime);
                for (int i = 0; i < 4500; i++)
                    AutonomousBotGroupCoordinator.PrepareCoordinatorTick();
                Assert.That(gate.GetValue(null), Is.EqualTo(GameLoop.GameLoopTime));
            }
            finally { gate.SetValue(null, previous); }
        }
    }
}
