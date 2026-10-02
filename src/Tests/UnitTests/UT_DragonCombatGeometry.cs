using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS;
using DOL.Database;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_DragonCombatGeometry
    {
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private sealed class Server : GameServer { protected override IObjectDatabase DataBaseImpl => EmptyDatabase; }
        private GameServer _previous;
        [SetUp] public void SetUp()
        {
            _previous = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown] public void TearDown() => GameServer.LoadTestDouble(_previous);
        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public bool Fail;
            public bool SawDisplacement;
            public Point3D Destination;
            public override bool MoveTo(ushort region, int x, int y, int z, ushort heading)
            {
                SawDisplacement = DragonCombatGeometry.IsDisplacing(this);
                Destination = new Point3D(x, y, z);
                if (Fail) throw new InvalidOperationException("Simulated failed transfer");
                return true;
            }
        }

        private static Player MakePlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            typeof(GameLiving).GetField("<TempProperties>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(player, new PropertyCollection());
            return player;
        }

        [TestCase(typeof(DOL.AI.Brain.AlbGolestandtBrain))]
        [TestCase(typeof(DOL.AI.Brain.MidGjalpinulvaBrain))]
        [TestCase(typeof(DOL.AI.Brain.HibCuuldurachBrain))]
        public void BreathIsReducedAndDisplacementCallbackRemainsRemoved(Type type)
        {
            var brain = RuntimeHelpers.GetUninitializedObject(type);
            var breath = (Spell)type.GetProperty("Dragon_PBAOE", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(brain);
            Assert.That(breath.Damage, Is.EqualTo(600));
            Assert.That(type.GetMethod("ThrowPlayer", BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
        }

        [TestCase(typeof(AlbGolestandt))]
        [TestCase(typeof(MidGjalpinulva))]
        [TestCase(typeof(HibCuuldurach))]
        public void OnlyGroundedMainlandDragonsHaveBodyReach(Type type)
        {
            var dragon = (GameNPC)RuntimeHelpers.GetUninitializedObject(type);
            Assert.That(DragonCombatGeometry.TargetReach(dragon), Is.EqualTo(320));
            dragon.Flags = GameNPC.eFlags.FLYING;
            Assert.That(DragonCombatGeometry.TargetReach(dragon), Is.Zero);
        }

        [Test]
        public void OrdinaryNpcsAndPlayersDoNotGainReach()
        {
            Assert.That(DragonCombatGeometry.TargetReach(null), Is.Zero);
            Assert.That(DragonCombatGeometry.TargetReach((GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC))), Is.Zero);
            Assert.That(DragonCombatGeometry.TargetReach(MakePlayer()), Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void ThrowScopesPortalSuppressionAndAlwaysClearsIt(bool fail)
        {
            var player = MakePlayer();
            player.Fail = fail;
            if (fail) Assert.Throws<InvalidOperationException>(() => DragonCombatGeometry.ThrowPlayer(player,1,100,200,1500,0));
            else Assert.That(DragonCombatGeometry.ThrowPlayer(player,1,100,200,1500,0), Is.True);
            Assert.That(player.SawDisplacement, Is.True);
            Assert.That(DragonCombatGeometry.IsDisplacing(player), Is.False, "Later genuine portals must work normally");
            Assert.That(DragonCombatGeometry.IsRecoveringFromThrow(player), Is.True);
            Assert.That(DragonCombatGeometry.IsRecoveringFromThrow(MakePlayer()), Is.False);
        }

        [Test]
        public void AlbionLiftIsHalfHeightWithoutHorizontalTeleport()
        {
            var player = MakePlayer();
            player.X = 391326; player.Y = 755351; player.Z = 391;
            Assert.That(DragonCombatGeometry.LiftPlayer(player), Is.True);
            Assert.That(player.Destination.X, Is.EqualTo(player.X));
            Assert.That(player.Destination.Y, Is.EqualTo(player.Y));
            Assert.That(player.Destination.Z - player.Z, Is.EqualTo((1815 - 391) / 2));
            Assert.That(DragonCombatGeometry.LiftPlayer(player), Is.False, "Do not stack a second displacement while falling");
        }

        [Test]
        public void GroundTeleportWithoutRegionSafelySkips()
        {
            Assert.That(DragonCombatGeometry.TeleportGrounded(MakePlayer(), 1, 100, 200, 1500, 0), Is.False);
        }
    }
}
