using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.Movement;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [NonParallelizable]
    public class UT_RealmRaidStableTravel
    {
        private EpicTestServerScope _server;
        [SetUp] public void SetUp() => _server = new EpicTestServerScope();
        [TearDown] public void TearDown() => _server.Dispose();

        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type Travel = typeof(RealmRaidFormation).Assembly.GetType("DOL.GS.RealmRaidStableTravel");

        private sealed class Rider : GameBot
        {
            private Rider() : base((OfflineWorldBotRecord)null) { }
            public Vector3 Position;
            public bool Alive, Fighting;
            public ushort RegionId;
            public override bool IsAlive => Alive;
            public override bool InCombat => Fighting;
            public override bool IsAttacking => Fighting;
            public override int X => (int)Position.X;
            public override int Y => (int)Position.Y;
            public override int Z => (int)Position.Z;
            public override ushort CurrentRegionID { get => RegionId; set => RegionId = value; }
        }

        // Install a single isolated trip/commitment, then exercise the production
        // coordinator. No world/server, database, real horse, or timer is started.
        private sealed class Journey : IDisposable
        {
            public readonly Rider[] Riders;
            public readonly Group Group;
            public readonly object Trip;
            private readonly object _trips;
            private readonly IDictionary _membership;
            public readonly Vector3 End = new(10000, 10000, 300);
            public readonly Vector3[] Posts;
            public Journey(GameStableMaster master = null)
            {
                Riders = Enumerable.Range(0, 8).Select(i =>
                {
                    var rider = (Rider)RuntimeHelpers.GetUninitializedObject(typeof(Rider));
                    rider.Alive = true;
                    rider.RegionId = 1;
                    rider.ObjectState = GameObject.eObjectState.Active;
                    typeof(GameNPC).GetField("m_brains", Hidden).SetValue(rider, new ArrayList());
                    return rider;
                }).ToArray();
                Group = new Group(Riders[0]);
                typeof(Group).GetField("_groupMembers", Hidden).SetValue(Group, Riders.Cast<GameLiving>().ToList());
                foreach (var rider in Riders) rider.Group = Group;
                Posts = Enumerable.Range(0, 8).Select(i => new Vector3(100 + i * 70, 100, 300)).ToArray();
                for (int i = 0; i < 8; i++) Riders[i].Position = Posts[i];
                Type tripType = Travel.GetNestedType("Trip", BindingFlags.NonPublic);
                Trip = Activator.CreateInstance(tripType, true);
                Set("Members", Riders.Cast<GameBot>().ToArray());
                Set("Boarding", Posts);
                Set("Arrival", Posts.Select(p => p - Posts[0] + End).ToArray());
                Set("End", End);
                Set("Region", (ushort)1);
                Set("Deadline", 300000L);
                Set("Choice", new AutonomousStableRoutePlanner.Choice(master, new DbItemTemplate(),
                    null, "Test destination", 60, 60, 120, 1, 0, Posts[0], Posts[0]));
                _trips = Travel.GetField("Trips", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                _trips.GetType().GetMethod("Add").Invoke(_trips, new[] { Group, Trip });
                Type manager = typeof(AutonomousRealmRaid);
                object raid = Activator.CreateInstance(manager.GetNestedType("Raid", BindingFlags.NonPublic), true);
                object party = Activator.CreateInstance(manager.GetNestedType("Party", BindingFlags.NonPublic), true);
                party.GetType().GetField("View").SetValue(party, new AutonomousRealmRaid.View("test-trip", "Travel", null, false));
                ((IDictionary)raid.GetType().GetField("Parties").GetValue(raid)).Add(Group, party);
                _membership = (IDictionary)manager.GetField("Membership", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                _membership.Add(Group, raid);
            }
            public void Set(string field, object value) => Trip.GetType().GetField(field).SetValue(Trip, value);
            public T Get<T>(string field) => (T)Trip.GetType().GetField(field).GetValue(Trip);
            public void Board(int index, long now, bool success = true) => Travel.GetMethod("Boarded").Invoke(null, new object[] { Riders[index], success, now });
            public void Mounted(int index, bool mounted) => typeof(GameBot).GetField("<IsOnStableMasterRoute>k__BackingField", Hidden).SetValue(Riders[index], mounted);
            public string Order(int index, long now, out string failure)
            {
                object[] args = { Riders[index], now, null, null };
                bool active = (bool)Travel.GetMethod("TryOrder").Invoke(null, args);
                failure = (string)args[3];
                return active ? args[2].GetType().GetProperty("Action").GetValue(args[2]).ToString() : null;
            }
            public void RemoveEvent() => _membership.Remove(Group);
            public void Dispose()
            {
                _membership.Remove(Group);
                _trips.GetType().GetMethod("Remove", new[] { typeof(Group) }).Invoke(_trips, new object[] { Group });
            }
        }

        [Test]
        public void ActualCoordinatorSharesTheDepartureLaneAcrossParties()
        {
            var master = (GameStableMaster)RuntimeHelpers.GetUninitializedObject(typeof(GameStableMaster));
            using var first = new Journey(master);
            using var second = new Journey(master);
            Assert.That(first.Order(0, 1000, out _), Is.EqualTo("Board"));
            Assert.That(second.Order(0, 1000, out _), Is.EqualTo("Wait"));
            first.Board(0, 1000);
            Assert.That(second.Order(0, 1749, out _), Is.EqualTo("Wait"));
            Assert.That(second.Order(0, 1750, out _), Is.EqualTo("Board"));
        }

        [Test]
        public void ActualBoardingProgressExtendsTheLegButIdleWaitingDoesNot()
        {
            using var journey = new Journey();
            journey.Order(0, 1000, out _);
            long firstDeadline = journey.Get<long>("Deadline");
            journey.Order(7, 20000, out _);
            Assert.That(journey.Get<long>("Deadline"), Is.EqualTo(firstDeadline));
            journey.Board(0, 30000);
            Assert.That(journey.Get<long>("Deadline"), Is.EqualTo(330000));
        }

        [Test]
        public void WholePartyStagesBeforeBoardingAndBoardingIsSpaced()
        {
            using var journey = new Journey();
            journey.Riders[7].Position += new Vector3(1000, 0, 0);
            Assert.That(journey.Order(0, 1000, out _), Is.EqualTo("Wait"));
            journey.Riders[7].Position = journey.Posts[7];
            journey.Riders[3].IsMezzed = true;
            Assert.That(journey.Order(0, 1000, out _), Is.EqualTo("Wait"));
            journey.Riders[3].IsMezzed = false;
            journey.Riders[3].Fighting = true;
            Assert.That(journey.Order(0, 1000, out _), Is.EqualTo("Wait"));
            journey.Riders[3].Fighting = false;
            Assert.That(journey.Order(0, 1000, out _), Is.EqualTo("Board"));
            journey.Board(0, 1000);
            Assert.That(journey.Order(1, 1749, out _), Is.EqualTo("Wait"));
            Assert.That(journey.Order(1, 1750, out _), Is.EqualTo("Walk"));
            journey.Riders[1].Position = journey.Posts[0];
            Assert.That(journey.Order(1, 1750, out _), Is.EqualTo("Board"));
            Assert.That(journey.Get<long>("Deadline"), Is.EqualTo(301000));
        }

        [Test]
        public void SevenArrivalsCannotReleaseThePartyBeforeTheLastNativeRideEnds()
        {
            using var journey = new Journey();
            for (int i = 0; i < 8; i++)
            {
                journey.Board(i, 1000 + i * 1000);
                journey.Riders[i].Position = journey.End;
            }
            journey.Mounted(7, true);
            Assert.That(journey.Order(0, 70000, out _), Is.EqualTo("Wait"));
            Assert.That(journey.Get<bool>("Finished"), Is.False);
            Assert.That(journey.Riders[7].IsOnStableMasterRoute, Is.True);
            journey.Mounted(7, false);
            Assert.That(journey.Order(0, 71000, out _), Is.Null);
            Assert.That(journey.Get<bool>("Finished"), Is.True);
            Assert.That(journey.Get<long>("RetryAfter"), Is.EqualTo(91000));
        }

        [TestCase("death")]
        [TestCase("roster")]
        [TestCase("region")]
        [TestCase("event")]
        [TestCase("deadline")]
        public void CancelledJourneyNeverDismountsExistingRiders(string cause)
        {
            using var journey = new Journey();
            journey.Board(0, 1000);
            journey.Mounted(0, true);
            if (cause == "death") journey.Riders[6].Alive = false;
            if (cause == "roster") journey.Riders[6].Group = null;
            if (cause == "region") journey.Riders[6].RegionId = 2;
            if (cause == "event") journey.RemoveEvent();
            long now = cause == "deadline" ? journey.Get<long>("Deadline") : 2000;
            Assert.That(journey.Order(1, now, out string failure), Is.Null);
            Assert.That(failure, Is.Not.Empty);
            Assert.That(journey.Riders[0].IsOnStableMasterRoute, Is.True);
            Assert.That(journey.Get<long>("RetryAfter"), Is.EqualTo(now + 120000));
        }

        [Test]
        public void InterruptedBoardingOrWrongEndpointDoesNotPretendThePartyArrived()
        {
            using var journey = new Journey();
            journey.Board(0, 1000);
            journey.Riders[0].Position = journey.End + new Vector3(1000, 0, 0);
            Assert.That(journey.Order(0, 2000, out string failure), Is.Null);
            Assert.That(failure, Does.Contain("did not arrive"));
            Assert.That(journey.Get<long>("RetryAfter"), Is.EqualTo(122000));
        }

        private static bool Posts(IPathfindingMgr nav, Zone zone, Vector3 center, out Vector3[] posts)
        {
            object[] arguments = { nav, zone, center, null };
            var type = typeof(RealmRaidFormation).Assembly.GetType("DOL.GS.RealmRaidStableTravel");
            bool success = (bool)type.GetMethod("TryPosts", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, arguments);
            posts = (Vector3[])arguments[3];
            return success;
        }
        [Test]
        public void EightBoardingAndArrivalPostsDoNotStackOnTicketOrigin()
        {
            var nav = new UT_RealmRaidFormation.Mesh();
            Vector3 center = new(20000, 20000, 1000);
            Assert.That(Posts(nav, UT_RealmRaidFormation.Zone(), center, out var posts), Is.True);
            Assert.That(posts.Distinct().Count(), Is.EqualTo(8));
            foreach (Vector3 post in posts)
            {
                Assert.That(Vector3.Distance(post, center), Is.InRange(90, 180));
                foreach (Vector3 other in posts.Where(p => p != post))
                    Assert.That(Vector3.Distance(post, other), Is.GreaterThanOrEqualTo(55));
            }
            Assert.That(nav.Samples, Is.LessThanOrEqualTo(24));
        }

        [TestCase("wall")] [TestCase("island")] [TestCase("floor")]
        public void TicketIsNotUsedWhenEitherEndHasNoSafePartyFormation(string obstruction)
        {
            var nav = new UT_RealmRaidFormation.Mesh { Visible = obstruction != "wall", Connected = obstruction != "island",
                HeightOffset = obstruction == "floor" ? 300 : 0 };
            Assert.That(Posts(nav, UT_RealmRaidFormation.Zone(), new(20000, 20000, 1000), out _), Is.False);
        }

        [Test]
        public void MultiplePartiesCannotReserveTheSameBoardingOrArrivalPosts()
        {
            var nav = new UT_RealmRaidFormation.Mesh();
            Vector3 center = new(20000, 20000, 1000);
            var occupied = new List<Vector3>();
            MethodInfo reserve = Travel.GetMethod("TryAvailablePosts", BindingFlags.NonPublic | BindingFlags.Static);
            for (int party = 0; party < 30; party++)
            {
                object[] args = { nav, UT_RealmRaidFormation.Zone(), center, occupied, null };
                Assert.That((bool)reserve.Invoke(null, args), Is.True, $"Party {party + 1}");
                foreach (Vector3 post in (Vector3[])args[4])
                {
                    Assert.That(Vector3.Distance(post, center), Is.LessThan(1000));
                    Assert.That(occupied.All(p => Vector3.Distance(p, post) >= 55), Is.True);
                    occupied.Add(post);
                }
            }
        }

        [Test]
        public void ArrivalAtOuterReservedPostCountsWithoutForcingAnotherPileAtEndpoint()
        {
            using var journey = new Journey();
            var arrival = journey.Get<Vector3[]>("Arrival");
            for (int i = 0; i < 8; i++)
            {
                arrival[i] += new Vector3(600, 0, 0);
                journey.Board(i, 1000 + i * 1000);
                journey.Riders[i].Position = arrival[i];
            }
            Assert.That(journey.Order(0, 70000, out _), Is.Null);
            Assert.That(journey.Get<bool>("Finished"), Is.True);
        }

        [Test]
        public void EightRidersHaveIndependentTicketWaypointStateIncludingFlyingHeight()
        {
            var original = new PathPoint(100, 200, 300, 500, EPathType.Once) { WaitTime = 3 };
            original.Next = new PathPoint(5000, 6000, 7300, 500, EPathType.Once) { Prev = original, WaitTime = 7 };
            MethodInfo clone = typeof(AutonomousStableRoutePlanner).GetMethod("CloneRoute", BindingFlags.NonPublic | BindingFlags.Static);
            var copies = Enumerable.Range(0, 8).Select(_ => (PathPoint)clone.Invoke(null, new object[] { original })).ToArray();
            foreach (var rider in copies)
            {
                Assert.That(rider, Is.Not.SameAs(original));
                Assert.That(rider.Next, Is.Not.SameAs(original.Next));
                Assert.That(rider.Next.Z, Is.EqualTo(7300));
                Assert.That(rider.Next.Prev, Is.SameAs(rider));
                Assert.That(rider.Next.WaitTime, Is.EqualTo(7));
            }
            copies[0].Next.WaitTime = 99;
            Assert.That(copies.Skip(1).All(r => r.Next.WaitTime == 7), Is.True);
            Assert.That(original.Next.WaitTime, Is.EqualTo(7));
        }
    }
}
