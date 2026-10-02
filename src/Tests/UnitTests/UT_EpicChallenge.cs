using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [NonParallelizable]
    public class UT_EpicChallenge
    {
        private EpicTestServerScope _server;
        [SetUp] public void SetUp() => _server = new EpicTestServerScope();
        [TearDown] public void TearDown() => _server.Dispose();

        private sealed class Boss : Fames
        {
            public bool Alive = true;
            public override bool IsAlive => Alive;
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 60; set { } }
            public override eFlags Flags { get; set; }
        }
        private sealed class Delegate : GameBot
        {
            private Delegate() : base((OfflineWorldBotRecord)null) { }
            public bool Alive;
            public int PositionX;
            public ushort RegionId;
            public override bool IsAlive => Alive;
            public override byte Level => 50;
            public override int X => PositionX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => RegionId; set => RegionId = value; }
        }
        private static bool Accept(Fames boss) => (bool)typeof(Fames).GetMethod("AcceptChallenge",
            BindingFlags.NonPublic | BindingFlags.Instance).Invoke(boss, null);

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void PreparationOnlyUnlocksTheStillLivingActiveBoss(bool alive, bool active)
        {
            var boss = (Boss)RuntimeHelpers.GetUninitializedObject(typeof(Boss));
            boss.Alive = alive;
            boss.ObjectState = active ? GameObject.eObjectState.Active : GameObject.eObjectState.Inactive;
            boss.Flags = GameNPC.eFlags.PEACE | GameNPC.eFlags.DONTSHOWNAME;
            Assert.That(boss.StartFamesTimer(null), Is.Zero);
            Assert.That((boss.Flags & GameNPC.eFlags.PEACE) == 0, Is.EqualTo(alive && active));
            Assert.That(boss.Flags.HasFlag(GameNPC.eFlags.DONTSHOWNAME), Is.True);
        }

        [TestCase("present", true)]
        [TestCase("dead", false)]
        [TestCase("removed", false)]
        [TestCase("distant", false)]
        [TestCase("other-region", false)]
        [TestCase("ungrouped", false)]
        public void AutomatedAnswerRequiresARealPresentEligibleBot(string state, bool expected)
        {
            bool previous = Fames.CanInteract, waiting = ApocInitializator.FamesWaitForText;
            var boss = (Boss)RuntimeHelpers.GetUninitializedObject(typeof(Boss));
            boss.Alive = true;
            boss.ObjectState = GameObject.eObjectState.Active;
            var actor = (Delegate)RuntimeHelpers.GetUninitializedObject(typeof(Delegate));
            actor.Alive = state != "dead";
            actor.ObjectState = state == "removed" ? GameObject.eObjectState.Inactive : GameObject.eObjectState.Active;
            actor.PositionX = state == "distant" ? 1501 : 100;
            actor.RegionId = (ushort)(state == "other-region" ? 160 : 60);
            actor.Group = state == "ungrouped" ? null : new Group(actor);
            typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(actor, true);
            try
            {
                Fames.CanInteract = false;
                Assert.That(typeof(Fames).GetMethod("TryAcceptBotChallengeFrom", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(boss, new object[] { actor }), Is.EqualTo(expected));
            }
            finally
            {
                ((ECSGameTimer)typeof(Fames).GetField("_challengeTimer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(boss))?.Stop();
                Fames.CanInteract = previous;
                ApocInitializator.FamesWaitForText = waiting;
            }
        }

        [Test]
        public void AcceptanceSchedulesOnePreparationTimerRegardlessOfObservers()
        {
            bool previous = Fames.CanInteract, waiting = ApocInitializator.FamesWaitForText;
            var boss = (Boss)RuntimeHelpers.GetUninitializedObject(typeof(Boss));
            boss.Alive = true;
            boss.ObjectState = GameObject.eObjectState.Active;
            ECSGameTimer timer = null;
            try
            {
                Fames.CanInteract = false;
                ApocInitializator.FamesWaitForText = true;
                Assert.That(Accept(boss), Is.True);
                timer = (ECSGameTimer)typeof(Fames).GetField("_challengeTimer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(boss);
                Assert.That(timer.Interval, Is.EqualTo(120000));
                for (int i = 0; i < 80; i++) Assert.That(Accept(boss), Is.False);
                Assert.That(typeof(Fames).GetField("_challengeTimer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(boss), Is.SameAs(timer));
                Assert.That(ApocInitializator.FamesWaitForText, Is.False);
            }
            finally
            {
                timer?.Stop();
                Fames.CanInteract = previous;
                ApocInitializator.FamesWaitForText = waiting;
            }
        }

        [Test]
        public void SimultaneousHumanAndBotAnswersCannotScheduleDuplicateStarts()
        {
            bool previous = Fames.CanInteract, waiting = ApocInitializator.FamesWaitForText;
            var boss = (Boss)RuntimeHelpers.GetUninitializedObject(typeof(Boss));
            boss.Alive = true;
            boss.ObjectState = GameObject.eObjectState.Active;
            int accepted = 0;
            try
            {
                Fames.CanInteract = false;
                Parallel.For(0, 80, _ => { if (Accept(boss)) Interlocked.Increment(ref accepted); });
                Assert.That(accepted, Is.EqualTo(1));
            }
            finally
            {
                ((ECSGameTimer)typeof(Fames).GetField("_challengeTimer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(boss))?.Stop();
                Fames.CanInteract = previous;
                ApocInitializator.FamesWaitForText = waiting;
            }
        }

        [Test]
        public void RemovedOrDeadBossCannotStartAChallenge()
        {
            bool previous = Fames.CanInteract;
            var boss = (Boss)RuntimeHelpers.GetUninitializedObject(typeof(Boss));
            try
            {
                Fames.CanInteract = false;
                boss.ObjectState = GameObject.eObjectState.Active;
                boss.Alive = false;
                Assert.That(Accept(boss), Is.False);
                boss.Alive = true;
                boss.ObjectState = GameObject.eObjectState.Inactive;
                Assert.That(Accept(boss), Is.False);
            }
            finally { Fames.CanInteract = previous; }
        }
    }
}
