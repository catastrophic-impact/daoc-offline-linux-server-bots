using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_AutonomousGroundTravelIntegration
    {
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private sealed class Server : GameServer { protected override IObjectDatabase DataBaseImpl => Empty; }
        private GameServer _previous;
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public bool Attacking;
            public override bool IsAttacking => Attacking;
            public override eFlags Flags { get; set; }
        }
        [SetUp] public void SetUp() { _previous=GameServer.Instance;GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server))); }
        [TearDown] public void TearDown() => GameServer.LoadTestDouble(_previous);
        private static Bot Actor(bool autonomous=true)
        {
            var b=(Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(b,autonomous);
            return b;
        }
        private static NpcMovementComponent Movement(Bot b,Vector3 delta,short speed=191)
        {
            var m=new NpcMovementComponent(b);b.movementComponent=m;m.CurrentSpeed=speed;
            typeof(NpcMovementComponent).GetProperty(nameof(NpcMovementComponent.IsDestinationValid)).SetValue(m,true);
            typeof(NpcMovementComponent).GetField("_destination",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(m,delta);
            return m;
        }
        private static void Recalculate(NpcMovementComponent m,Vector3 delta) =>
            typeof(NpcMovementComponent).GetMethod("UpdateVelocity",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(m,new object[]{delta.Length()});
        private static object Arrival(NpcMovementComponent m) => typeof(NpcMovementComponent).GetField("_groundTravelArrivalMilliseconds",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(m);

        [Test]
        public void ActualMoverUsesGroundSpeedAndMatchingArrivalThenClearsItForCombat()
        {
            var b=Actor();Vector3 d=new(100,0,200);var m=Movement(b,d);
            Recalculate(m,d);
            Assert.That(m.HorizontalVelocityForClient,Is.EqualTo(191));
            Assert.That(Arrival(m),Is.EqualTo(524));
            b.Attacking=true;Recalculate(m,d);
            Assert.That(m.HorizontalVelocityForClient,Is.EqualTo(191/Math.Sqrt(5)).Within(0.001));
            Assert.That(Arrival(m),Is.Null);
        }

        [TestCase("companion")]
        [TestCase("temporary")]
        [TestCase("player-led")]
        [TestCase("stable")]
        [TestCase("fixed")]
        [TestCase("flying")]
        [TestCase("swimming")]
        [TestCase("follow")]
        public void UnrelatedMovementRetainsOriginalKinematics(string kind)
        {
            var b=Actor(kind!="companion");Vector3 d=new(100,0,200);var m=Movement(b,d);
            if(kind=="temporary")typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(b,true);
            if(kind=="player-led")typeof(GameBot).GetProperty(nameof(GameBot.PlayerGroupLeader)).SetValue(b,RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer)));
            if(kind=="stable")typeof(GameBot).GetProperty(nameof(GameBot.IsOnStableMasterRoute)).SetValue(b,true);
            if(kind=="fixed")m.FixedSpeed=true;
            if(kind=="flying")b.Flags=GameNPC.eFlags.FLYING;
            if(kind=="swimming")b.Flags=GameNPC.eFlags.SWIMMING;
            if(kind=="follow")typeof(NpcMovementComponent).GetProperty(nameof(NpcMovementComponent.FollowTarget)).SetValue(m,Actor());
            Recalculate(m,d);
            Assert.That(m.HorizontalVelocityForClient,Is.EqualTo(191/Math.Sqrt(5)).Within(0.001));
            Assert.That(Arrival(m),Is.Null);
        }
    }
}
