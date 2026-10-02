using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.GS;
using NUnit.Framework;
using DOL.Database;
using DOL.GS.ServerProperties;
using DOL.GS.ServerRules;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_ContinuousBotRoutes
    {
        private sealed class Actor : GameBot
        {
            public Actor() : base((OfflineWorldBotRecord)null) { }
            public bool Fighting;
            public override int X { get; set; }
            public override int Y { get; set; }
            public override int Z { get; set; }
            public override ushort Heading { get; set; }
            public override bool IsAlive => true;
            public override bool InCombat => Fighting;
            public override bool IsCasting => false;
            public override bool IsAttacking => false;
            public override bool IsCrowdControlled => false;
            public override eRealm Realm { get; set; }
            public override int GetModified(eProperty property) => property == eProperty.MaxSpeed ? 200 : 0;
        }
        private sealed class Mesh : PathfindingMgrBase
        {
            public int Queries;
            public bool Stalled;
            public override bool IsAvailable => true;
            public override bool HasNavmesh(Zone zone) => true;
            public override bool HasLineOfSight(Zone zone, Vector3 from, Vector3 to, EDtPolyFlags[] filters) => true;
            public override PathfindingResult GetPathStraight(Zone zone, Vector3 from, Vector3 to, EDtPolyFlags[] filters, Span<WrappedPathfindingNode> nodes)
            {
                Queries++;
                nodes[0] = new(from, EDtPolyFlags.Walk);
                if (Stalled) return new(PathfindingStatus.PartialPathFound, 1);
                bool partial = Vector3.Distance(from, to) > 200;
                nodes[1] = new(partial ? from + Vector3.Normalize(to - from) * 200 : to, EDtPolyFlags.Walk);
                return new(partial ? PathfindingStatus.PartialPathFound : PathfindingStatus.PathFound, 2);
            }
        }
        private IPathfindingMgr _previous;
        private GameServer _previousServer;
        private PetTestLanguageScope _language;
        private sealed class Server : GameServer
        {
            protected override IServerRules ServerRulesImpl => new NormalServerRules();
            protected override IObjectDatabase DataBaseImpl => Empty;
        }
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        [SetUp] public void Setup()
        {
            _previous = PathfindingProvider.Instance;
            _previousServer = GameServer.Instance;
            _language = new PetTestLanguageScope();
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown] public void Cleanup()
        {
            PathfindingProvider.SetPathfindingMgr(_previous);
            GameServer.LoadTestDouble(_previousServer);
            _language.Dispose();
        }

        [Test]
        public void ExactCorridorCache_IsBoundedAndInvalidatedByGeometryRevision()
        {
            var zone = Zone();
            var cache = new AutonomousCorridorCache.ZoneCache();
            var key = new AutonomousCorridorCache.Key(Vector3.Zero, new(1, 2, 3), NavigationGeometryRevision.Read(zone));
            cache.Store(key, false);
            Assert.That(cache.TryGet(key, out bool answer), Is.True);
            Assert.That(answer, Is.False);
            NavigationGeometryRevision.Changed(zone);
            Assert.That(cache.TryGet(key with { Revision = NavigationGeometryRevision.Read(zone) }, out _), Is.False);
            Assert.That(cache.TryGet(key with { From = new(1, 0, 0) }, out _), Is.False);
            for (int i = 0; i < 10000; i++) cache.Store(key with { From = new(i, 0, 0) }, true);
            Assert.That(cache.Count, Is.LessThanOrEqualTo(AutonomousCorridorCache.CapacityPerZone));
        }
        private static Actor Bot()
        {
            var bot = (Actor)RuntimeHelpers.GetUninitializedObject(typeof(Actor));
            bot.Realm = eRealm.Albion;
            typeof(GameLiving).GetField("<TempProperties>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(bot, new PropertyCollection());
            typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot, true);
            typeof(GameBot).GetProperty(nameof(GameBot.PersistentRecord)).SetValue(bot, new OfflineWorldBotRecord { ObjectiveAssignmentId = "travel" });
            typeof(GameNPC).GetField("m_brains", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(bot, new ArrayList());
            typeof(GameNPC).GetField("m_ownBrain", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(bot, new BotBrain());
            return bot;
        }
        private static Zone Zone() => new(null, 2, "test", 0, 0, 65536, 65536, 2, false, 0, false, 0, 0, 0, 0, 0);
        [Test]
        public void RvrPlanningView_IsSharedOnlyWithinOneNpcPhase()
        {
            Actor first = Bot(), second = Bot();
            first.DatabaseID = 9000000001; second.DatabaseID = 9000000002;
            first.ObjectState = second.ObjectState = GameObject.eObjectState.Active;
            second.Realm = eRealm.Midgard;
            try
            {
                AutonomousBotRegistry.Register(first);
                AutonomousBotRegistry.Register(second);
                AutonomousWorldBotController.PrepareRvrPlanningTick();
                var method = typeof(AutonomousWorldBotController).GetMethod("GetRvrPlanningView", BindingFlags.NonPublic | BindingFlags.Static);
                object view = method.Invoke(null, null);
                Assert.That(method.Invoke(null, null), Is.SameAs(view));
                var count = view.GetType().GetMethod("Count");
                Assert.That(count.Invoke(view, new object[] { (ushort)0, eRealm.Albion }), Is.EqualTo((1, 1)));
                second.Realm = eRealm.Albion;
                AutonomousWorldBotController.PrepareRvrPlanningTick();
                object next = method.Invoke(null, null);
                Assert.That(next, Is.Not.SameAs(view));
                Assert.That(count.Invoke(next, new object[] { (ushort)0, eRealm.Albion }), Is.EqualTo((2, 0)));
            }
            finally
            {
                AutonomousBotRegistry.Unregister(first);
                AutonomousBotRegistry.Unregister(second);
                AutonomousBotRegistry.SamplePopulationNow();
                AutonomousWorldBotController.PrepareRvrPlanningTick();
            }
        }
        [Test]
        public void LiveMerchantIndex_TracksAddsRemovalsAndReusedObjectSlots()
        {
            var region = new Region(new RegionData { Id = 100, Name = "test", Description = "test", Mobs = [] });
            var first = (GameMerchant)RuntimeHelpers.GetUninitializedObject(typeof(GameMerchant));
            var second = (GameMerchant)RuntimeHelpers.GetUninitializedObject(typeof(GameMerchant));
            var third = (GameMerchant)RuntimeHelpers.GetUninitializedObject(typeof(GameMerchant));
            bool Add(GameObject actor) => (bool)typeof(Region).GetMethod("AddObject", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(region, new object[] { actor });
            void Remove(GameObject actor) => typeof(Region).GetMethod("RemoveObject", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(region, new object[] { actor });
            Assert.That(Add(first), Is.True);
            Assert.That(Add(Bot()), Is.True);
            Assert.That(Add(second), Is.True);
            var oldSnapshot = region.Merchants;
            Remove(first);
            Assert.That(region.Merchants, Is.EqualTo(new[] { second }));
            Assert.That(oldSnapshot, Is.EqualTo(new[] { first, second }));
            Assert.That(Add(third), Is.True);
            Assert.That(region.Merchants, Is.EqualTo(new[] { third, second }));
            Remove(third);
            Remove(second);
            Assert.That(region.Merchants, Is.Empty);
        }

        [Test]
        public void ValidatedWaypoint_ArrivalStartsNextPathWithoutBrainTurn()
        {
            var nav = new Mesh();
            PathfindingProvider.SetPathfindingMgr(nav);
            var region = new Region(new RegionData { Id = 1, Name = "test", Description = "test", Mobs = [] });
            var zone = new Zone(region, 2, "test", 0, 0, 65536, 65536, 2, false, 0, false, 0, 0, 0, 0, 0) { IsPathfindingEnabled = true };
            region.Zones.Add(zone);
            Actor bot = Bot();
            bot.CurrentRegion = region;
            bot.X = 1000; bot.Y = 1000;
            bot.Brain.NextThinkTick = 12000;
            var mover = new NpcMovementComponent(bot);
            bot.movementComponent = mover;
            Vector3 waypoint = new(1000, 1000, 0), final = new(1600, 1000, 0);
            mover.PathViaValidatedWaypoint(waypoint, final, 200);
            typeof(NpcMovementComponent).GetField("_currentMovementDesiredSpeed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mover, (short)200);
            typeof(MovementComponent).GetField("_ownerPosition", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mover, waypoint);
            typeof(NpcMovementComponent).GetMethod("OnArrival", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(mover, null);
            Assert.That(mover.IsMoving, Is.True);
            Assert.That(mover.CurrentSpeed, Is.EqualTo(200));
            Assert.That(mover.Destination, Is.EqualTo(new Vector3(1200, 1000, 0)));
            Assert.That(bot.Brain.NextThinkTick, Is.EqualTo(12000));
            mover.StopMoving();
        }
        [Test]
        public void TenThousandPartialRoutes_AdvanceWithoutWakingBrainsOrUnboundedRetries()
        {
            var nav = new Mesh();
            PathfindingProvider.SetPathfindingMgr(nav);
            var zone = Zone();
            Vector3 goal = new(800, 0, 0);
            for (int i = 0; i < 10000; i++)
            {
                Actor bot = Bot();
                bot.Brain.NextThinkTick = 12000;
                var path = new Pathfinder(bot);
                for (int leg = 1; leg <= 4; leg++)
                {
                    Assert.That(path.TryGetNextNode(zone, new(bot.X, bot.Y, bot.Z), goal, out var next), Is.True);
                    Assert.That(next.Value.X, Is.EqualTo(leg * 200));
                    bot.X = (int)next.Value.X;
                }
                Assert.That(bot.Brain.NextThinkTick, Is.EqualTo(12000));
            }
            Assert.That(nav.Queries, Is.EqualTo(40000));
        }
        [Test]
        public void DisconnectedPartialEndpoint_DoesNotSpinOrInventMovement()
        {
            var nav = new Mesh { Stalled = true };
            PathfindingProvider.SetPathfindingMgr(nav);
            var bot = Bot();
            var path = new Pathfinder(bot);
            Assert.That(path.TryGetNextNode(Zone(), Vector3.Zero, new(800, 0, 0), out _), Is.False);
            Assert.That(nav.Queries, Is.EqualTo(1));
        }
        [TestCase(false)] [TestCase(true)]
        public void ValidatedSeam_ContinuesInSameArrivalUnlessCombatInterrupts(bool fighting)
        {
            var nav = new Mesh();
            PathfindingProvider.SetPathfindingMgr(nav);
            Actor bot = Bot();
            var mover = new NpcMovementComponent(bot);
            bot.movementComponent = mover;
            Vector3 inside = new(1000, 1000, 0), outside = new(1128, 1000, 0);
            mover.PathAcrossValidatedSeam(inside, outside, null, 200);
            typeof(NpcMovementComponent).GetField("_currentMovementDesiredSpeed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mover, (short)200);
            typeof(MovementComponent).GetField("_ownerPosition", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mover, inside);
            bot.X = 1000; bot.Y = 1000; bot.Fighting = fighting;
            bool continued = (bool)typeof(NpcMovementComponent).GetMethod("TryContinueValidatedSeam", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(mover, null);
            Assert.That(continued, Is.EqualTo(!fighting));
            if (!fighting)
            {
                Assert.That(mover.IsMoving, Is.True);
                Assert.That(mover.Destination, Is.EqualTo(outside));
            }
            mover.StopMoving();
        }

        [TestCase("stop")]
        [TestCase("teleport")]
        [TestCase("new-order")]
        [TestCase("new-assignment")]
        [TestCase("rest")]
        public void ValidatedSeam_DiscardsObsoleteContinuation(string interrupt)
        {
            PathfindingProvider.SetPathfindingMgr(new Mesh());
            Actor bot = Bot();
            var mover = new NpcMovementComponent(bot);
            bot.movementComponent = mover;
            Vector3 inside = new(1000, 1000, 0), outside = new(1128, 1000, 0);
            mover.PathAcrossValidatedSeam(inside, outside, null, 200);
            typeof(MovementComponent).GetField("_ownerPosition", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mover, inside);
            bot.X = 1000; bot.Y = 1000;
            switch (interrupt)
            {
                case "stop": mover.StopMoving(); break;
                case "teleport": mover.ForceUpdatePosition(); break;
                case "new-order": mover.PathTo(new(500, 500, 0), 200); break;
                case "new-assignment": bot.PersistentRecord.ObjectiveAssignmentId = "changed"; break;
                case "rest": typeof(GameBot).GetField("_recoveryRestLocked", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(bot, true); break;
            }
            bool continued = (bool)typeof(NpcMovementComponent).GetMethod("TryContinueValidatedSeam", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(mover, null);
            Assert.That(continued, Is.False);
            Assert.That(typeof(NpcMovementComponent).GetField("_seamContinuation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(mover), Is.Null);
            mover.StopMoving();
        }

        [Test]
        public void ValidatedSeam_ActualArrivalKeepsNonzeroMovement()
        {
            PathfindingProvider.SetPathfindingMgr(new Mesh());
            Actor bot = Bot();
            var mover = new NpcMovementComponent(bot);
            bot.movementComponent = mover;
            Vector3 inside = new(1000, 1000, 0), outside = new(1128, 1000, 0);
            mover.PathAcrossValidatedSeam(inside, outside, null, 200);
            typeof(NpcMovementComponent).GetField("_currentMovementDesiredSpeed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mover, (short)200);
            typeof(MovementComponent).GetField("_ownerPosition", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mover, inside);
            bot.X = 1000; bot.Y = 1000;
            typeof(NpcMovementComponent).GetMethod("OnArrival", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(mover, null);
            Assert.That(mover.IsMoving, Is.True);
            Assert.That(mover.CurrentSpeed, Is.EqualTo(200));
            Assert.That(mover.Destination, Is.EqualTo(outside));
            mover.StopMoving();
        }
    }
}
