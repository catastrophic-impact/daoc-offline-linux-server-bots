using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.Commands;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_PlayerTravel
{
    private sealed class TestWalkingInput : IPlayerTravelInput
    {
        public bool Cancelled { get; private set; }
        public int WalkingRequests;
        public bool Steer(bool walk) { if (walk) WalkingRequests++; return !Cancelled; }
        public void Dispose() => Cancelled = true;
    }
    private sealed class TestServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
    }
    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_AutonomousLootFlow.EmptyReadDatabase>();
    private GameServer _previousServer;
    [SetUp] public void SetUp()
    {
        _previousServer = GameServer.Instance;
        GameServer.LoadTestDouble((TestServer)RuntimeHelpers.GetUninitializedObject(typeof(TestServer)));
    }
    [TearDown] public void TearDown() => GameServer.LoadTestDouble(_previousServer);

    private sealed class Mesh : PathfindingMgrBase
    {
        public bool Available = true;
        public bool Blocked;
        public bool OpenRaycast;
        public PathfindingStatus Status = PathfindingStatus.PathFound;
        public Vector3[] Corners;
        public int Searches;
        public override bool HasNavmesh(Zone zone) => Available;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            Searches++;
            Vector3[] route = Corners ?? new[] { start, end };
            for (int i = 0; i < route.Length; i++) destination[i] = new(route[i], EDtPolyFlags.Walk);
            return new(Status, route.Length);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => Blocked ? start : end;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => !Blocked || OpenRaycast;
    }

    private sealed class Actor : GamePlayer
    {
        private Actor() : base(null, null) { }
        public GameClient Connection;
        public bool Fighting;
        public bool Dead;
        public bool Mounted;
        public int PositionX;
        public int PositionY;
        public ushort RegionId = 1;
        public IControlledBrain Pet;
        public override GameClient Client => Connection;
        public override bool IsAlive => !Dead;
        public override bool IsAttacking => Fighting;
        public override bool InCombat => Fighting;
        public override bool IsCasting => false;
        public override bool IsCrowdControlled => false;
        public override bool IsOnHorse { get => Mounted; set => Mounted = value; }
        public override int X { get => PositionX; set => PositionX = value; }
        public override int Y { get => PositionY; set => PositionY = value; }
        public override int Z { get => 0; set { } }
        public override ushort CurrentRegionID { get => RegionId; set => RegionId = value; }
        public override IControlledBrain ControlledBrain { get => Pet; set => Pet = value; }
    }

    private sealed class Pet : GameNPC
    {
        public override bool IsAlive => true;
        public override bool InCombat => true;
        public override bool IsAttacking => true;
    }

    private static Actor Player()
    {
        var player = (Actor)RuntimeHelpers.GetUninitializedObject(typeof(Actor));
        player.RegionId = 1;
        player.ObjectState = GameObject.eObjectState.Active;
        player.Connection = (GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient));
        typeof(GamePlayer).GetField("m_steed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, new WeakReference(null));
        // No sockets, timers, world actors or database writes in these tests.
        typeof(GameClient).GetField("<ClientState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(player.Connection, GameClient.eClientState.Playing);
        return player;
    }

    private static (Region Region, Zone Zone) World()
    {
        var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
        Zone zone = new(region, 1, "Test", 0, 0, 65536, 65536, 1, false, 0, false, 0, 0, 0, 0, 0);
        typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new List<Zone> { zone });
        return (region, zone);
    }

    private static Group Party(params GameLiving[] members)
    {
        var group = new Group(members[0]);
        var list = (List<GameLiving>)typeof(Group).GetField("_groupMembers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
        foreach (var member in members) { list.Add(member); member.Group = group; }
        return group;
    }

    [TestCase("Tír na nÓg", "tirnanog")]
    [TestCase("TNN", "tirnanog")]
    [TestCase("Camelot City", "camelot")]
    [TestCase("Cotswold Village", "cotswold")]
    [TestCase("Ardee", "ardee")]
    public void NamedPlacesNormalizeWithoutInventingCoordinates(string name, string expected) =>
        Assert.That(PlayerMobNavigator.NormalizePlaceName(name), Is.EqualTo(expected));

    [TestCase(424, 0, false)]
    [TestCase(425, 0, true)]
    [TestCase(575, 0, true)]
    [TestCase(0, 825, true)]
    [TestCase(826, 0, false)]
    public void DungeonTeleportApproachStaysOutsideTriggerButNearEntrance(int dx, int dy, bool expected) =>
        Assert.That(PlayerMobNavigator.IsSafeDungeonExteriorDistance(10_000, 20_000,
            10_000 + dx, 20_000 + dy), Is.EqualTo(expected));

    [Test]
    public void ObsoleteTravelCommandIsNotRegistered()
    {
        var attribute = typeof(TravelCommandHandler).GetCustomAttribute<CmdAttribute>();
        Assert.That(attribute, Is.Null);
    }

    [Test]
    public void CachedCorridorVisitsCornersInsteadOfWalkingThroughWall()
    {
        var world = World();
        var mesh = new Mesh { Corners = new[] { new Vector3(100, 100, 0), new Vector3(100, 500, 0), new Vector3(600, 500, 0) } };
        var path = new PlayerTravelPath();
        Vector3 current = mesh.Corners[0], goal = mesh.Corners[^1];
        for (int tick = 0; tick < 9; tick++)
        {
            Assert.That(path.TryStep(world.Region, world.Zone, current, goal, 100, tick * 250, mesh, out var next, out var error), Is.True, error);
            if (tick < 4) Assert.That(next.X, Is.EqualTo(100), "Do not cut the corner.");
            current = next;
        }
        Assert.That(current, Is.EqualTo(goal));
        Assert.That(mesh.Searches, Is.EqualTo(1), "Do not repath every movement tick.");
        path.Clear();
        path.TryStep(world.Region, world.Zone, mesh.Corners[0], goal, 100, 10000, mesh, out _, out _);
        Assert.That(mesh.Searches, Is.EqualTo(2), "Resume explicitly invalidates the old corridor.");
    }

    [Test]
    public void BlockedWalkingHasBoundedRecoveryAndNoTeleport()
    {
        var world = World(); var mesh = new Mesh { Blocked = true }; var path = new PlayerTravelPath();
        Vector3 current = new(100, 100, 0), goal = new(1000, 1000, 0);
        for (int i = 0; i < 3; i++)
        {
            Assert.That(path.TryStep(world.Region, world.Zone, current, goal, 100, i * 15000, mesh, out var next, out _), Is.True);
            Assert.That(next, Is.EqualTo(current));
        }
        Assert.That(path.TryStep(world.Region, world.Zone, current, goal, 100, 45000, mesh, out _, out var error), Is.False);
        Assert.That(error, Does.Contain("two replans"));
        Assert.That(mesh.Searches, Is.EqualTo(3));
    }

    [Test]
    public void IncompleteAndMissingMeshesNeverFallBackToDirectWalking()
    {
        var world = World(); var mesh = new Mesh { Available = false };
        Assert.That(PlayerTravelPath.TryBuildCorridor(mesh, world.Zone, Vector3.Zero, new(1000, 1000, 0), out _), Is.False);
        mesh.Available = true; mesh.Status = PathfindingStatus.PartialPathFound;
        mesh.Corners = new[] { Vector3.Zero, new Vector3(100, 100, 0) };
        Assert.That(PlayerTravelPath.TryBuildCorridor(mesh, world.Zone, Vector3.Zero, new(1000, 1000, 0), out _), Is.False);
        Assert.That(mesh.Searches, Is.LessThanOrEqualTo(2));
    }

    [Test]
    public void CornerToleranceHandlesMeshProjectionButNotDifferentFloorsOrWideShortcuts()
    {
        Assert.That(PlayerTravelPath.HasReachedCorner(new(553254, 513106, 2899), new(553241.56f, 513126.38f, 2899.2f)), Is.True);
        Assert.That(PlayerTravelPath.HasReachedCorner(Vector3.Zero, new(33, 0, 0)), Is.False);
        Assert.That(PlayerTravelPath.HasReachedCorner(Vector3.Zero, new(0, 0, 100)), Is.False);
    }

    [Test]
    public void PolygonEdgeRecoveryRequiresOnMeshEndpointAndClearRaycast()
    {
        var world = World(); var path = new PlayerTravelPath(); var mesh = new Mesh { Blocked = true, OpenRaycast = true };
        Vector3 current = new(100, 100, 0), goal = new(1000, 1000, 0);
        Assert.That(path.TryStep(world.Region, world.Zone, current, goal, 47.5f, 0, mesh, out var next, out _), Is.True);
        Assert.That(Vector3.Distance(current, next), Is.EqualTo(47.5f).Within(0.001));
        mesh.OpenRaycast = false;
        Assert.That(path.TryStep(world.Region, world.Zone, current, goal, 47.5f, 250, mesh, out next, out _), Is.True);
        Assert.That(next, Is.EqualTo(current), "Never bypass a real wall.");
    }

    [Test]
    public void LocalPartyAndPetsPauseTravelButDistantOrUnrelatedFightsDoNot()
    {
        var owner = Player(); var member = Player(); var stranger = Player();
        Party(owner, member); member.Fighting = true; stranger.Fighting = true;
        Assert.That(PlayerMobNavigator.ShouldPauseForParty(owner), Is.True);
        Assert.That(PlayerMobNavigator.IsNearbyPartyCombatant(owner, stranger), Is.False);
        member.PositionX = 2001;
        Assert.That(PlayerMobNavigator.ShouldPauseForParty(owner), Is.False);
        member.PositionX = 0; member.RegionId = 2;
        Assert.That(PlayerMobNavigator.ShouldPauseForParty(owner), Is.False);
        member.RegionId = 1; member.Dead = true;
        Assert.That(PlayerMobNavigator.ShouldPauseForParty(owner), Is.False);
        var pet = (Pet)RuntimeHelpers.GetUninitializedObject(typeof(Pet));
        owner.Pet = new ControlledMobBrain(owner) { Body = pet };
        Assert.That(PlayerMobNavigator.ShouldPauseForParty(owner), Is.True, "Necromancer pet engagement must pause its owner's travel.");
    }

    [TestCase(GameClient.eClientState.Playing, false, 100, 200, true)]
    [TestCase(GameClient.eClientState.WorldEnter, false, 100, 200, true)]
    [TestCase(GameClient.eClientState.Playing, true, 100, 200, false)]
    [TestCase(GameClient.eClientState.Playing, false, 200, 200, false)]
    [TestCase(GameClient.eClientState.Disconnected, false, 100, 200, false)]
    public void RegionLoadingWaitDoesNotCancelAcceptedCrossingOrKeepDisconnectedPlayers(GameClient.eClientState state, bool active, long now, long deadline, bool expected) =>
        Assert.That(PlayerMobNavigator.IsWaitingForRegionLoad(state, active, now, deadline), Is.EqualTo(expected));

    [TestCase(true)] [TestCase(false)]
    public void ActiveHorseRouteOwnsMovementAcrossManyTravelTicks(bool horseFlag)
    {
        var player = Player(); player.Mounted = horseFlag;
        if (!horseFlag) player.Steed = (GameTaxi)RuntimeHelpers.GetUninitializedObject(typeof(GameTaxi));
        Type stateType = typeof(PlayerMobNavigator).GetNestedType("TravelState", BindingFlags.NonPublic);
        object state = Activator.CreateInstance(stateType, true);
        var input = new TestWalkingInput();
        stateType.GetField("Input").SetValue(state, input);
        stateType.GetField("Player").SetValue(state, player);
        stateType.GetField("NextStatusTick").SetValue(state, long.MaxValue);
        var travels = (IDictionary)typeof(PlayerMobNavigator).GetField("Travels", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        travels.Add(player, state);
        try
        {
            var tick = typeof(PlayerMobNavigator).GetMethod("TickOnGameLoop", BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < 100; i++) tick.Invoke(null, new[] { state });
            Assert.That(PlayerMobNavigator.IsActive(player), Is.True);
            Assert.That(stateType.GetField("WasRiding").GetValue(state), Is.True);
            Assert.That(PlayerMobNavigator.SuppressClientMovement(player), Is.False);
            Assert.That(player.Mounted || player.Steed != null, Is.True);
            Assert.That(player.PositionX, Is.Zero); Assert.That(player.PositionY, Is.Zero);
            Assert.That(input.WalkingRequests, Is.Zero, "No forward key while a horse owns movement");
            // Native horse movement must remain untouched even when cancelling.
            PlayerMobNavigator.Stop(player, "test", notify: false);
            Assert.That(input.Cancelled, Is.True, "Cancel always releases local input");
            Assert.That(player.Mounted || player.Steed != null, Is.True);
        }
        finally { travels.Remove(player); }
    }

    [Test]
    public void PendingNativeBoardingDoesNotIssueWalkingOrBuyAnotherTicket()
    {
        Type stateType = typeof(PlayerMobNavigator).GetNestedType("TravelState", BindingFlags.NonPublic);
        Type destinationType = typeof(PlayerMobNavigator).GetNestedType("Destination", BindingFlags.NonPublic);
        object state = Activator.CreateInstance(stateType, true);
        var input = new TestWalkingInput();
        stateType.GetField("Input").SetValue(state, input);
        stateType.GetField("Player").SetValue(state, Player());
        stateType.GetField("StableAttemptUntil").SetValue(state, long.MaxValue);
        var choose = typeof(PlayerMobNavigator).GetMethod("TryUseFasterStableRoute", BindingFlags.Static | BindingFlags.NonPublic);
        // Inert actor deliberately has no inventory, merchant, region or movement
        // component. Any attempt to walk/buy/replan during boarding would fail.
        for (int i = 0; i < 100; i++)
            Assert.That(choose.Invoke(null, new[] { state, Activator.CreateInstance(destinationType) }), Is.True);
        Assert.That(stateType.GetField("Status").GetValue(state), Is.EqualTo("Waiting for the stable horse to board"));
        Assert.That(input.WalkingRequests, Is.Zero);
    }
}
