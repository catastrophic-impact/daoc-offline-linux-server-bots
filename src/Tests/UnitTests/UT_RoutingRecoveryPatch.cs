using System;
using System.Numerics;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public class UT_RoutingRecoveryPatch
{
    private sealed class ReleaseBot : GameBot
    {
        private ReleaseBot() : base((OfflineWorldBotRecord)null) { }
        public bool MoveSucceeds, ThrowOnMove;
        public int HealthWhenMoving;
        public override int Health { get; set; }
        public override int Mana { get; set; }
        public override int Endurance { get; set; }
        public override int MaxHealth => 90;
        public override int MaxMana => 60;
        public override int MaxEndurance => 120;
        public override bool IsAlive => Health > 0;
        public override bool Say(string message) => true;
        public override bool MoveTo(ushort region, int x, int y, int z, ushort heading)
        {
            HealthWhenMoving = Health;
            if (ThrowOnMove) throw new InvalidOperationException("injected transfer failure");
            return MoveSucceeds;
        }
    }

    [TestCase(false, false, 3000)]
    [TestCase(false, true, 3000)]
    [TestCase(true, false, 0)]
    public void ReleaseTransfersBeforeRevivingAndRetainsRetryAfterFailure(bool moved, bool throws, int nextTick)
    {
        var bot = (ReleaseBot)RuntimeHelpers.GetUninitializedObject(typeof(ReleaseBot));
        bot.MoveSucceeds = moved; bot.ThrowOnMove = throws;
        bot.ObjectState = GameObject.eObjectState.Active;
        bot.movementComponent = new NpcMovementComponent(bot);
        typeof(GameNPC).GetField("m_brains", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(bot, new ArrayList());
        void Set(string name, object value) => typeof(GameBot).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bot, value);
        Set("_deathTick", GameLoop.GameLoopTime - 100_000L);
        Set("_deathRegionId", (ushort)65003);
        Set("_deathLocation", new Point3D(10,20,30));
        Set("_nextDeathRecoveryErrorTick", long.MaxValue);
        var callback = typeof(GameBot).GetMethod("HandleDeathRecovery", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(callback.Invoke(bot, new object[] { null }), Is.EqualTo(nextTick));
        Assert.That(bot.HealthWhenMoving, Is.Zero, "Never revive at the corpse before transfer succeeds");
        Assert.That(bot.Health, Is.EqualTo(moved ? 30 : 0));
        Assert.That(bot.IsReturningAfterRelease, Is.EqualTo(moved));
    }

    [TestCase(38f, true)]
    [TestCase(38.01f, false)]
    [TestCase(38.9f, false)]
    [TestCase(39f, false)]
    public void FormationAndAttendanceShareAnExactBoundary(float distance, bool expected)
    {
        bool arrived = AutonomousRendezvousAttendance.IsAtSlot(Vector3.Zero, new(distance, 0, 0));
        Assert.That(arrived, Is.EqualTo(expected));
        var attendance = new AutonomousRendezvousAttendance();
        Assert.That(attendance.Observe(1, 0, arrived), Is.False);
        Assert.That(attendance.Observe(1, 900_000, arrived), Is.EqualTo(!expected));
    }

    [Test]
    public void BindCacheCopiesCoordinatesAndReplacesAtomicallyWithoutDatabaseAccess()
    {
        const ushort region = 65001;
        var old = new DbBindPoint { X = 10, Y = 20, Z = 30 };
        BotReleaseBindPoints.Replace(region, new[] { old, new DbBindPoint { X = 1000, Y = 2000, Z = 90 } });
        old.X = 999;
        Assert.That(BotReleaseBindPoints.Nearest(region, 0, 0)?.X, Is.EqualTo(10));
        Assert.That(BotReleaseBindPoints.Nearest(region, 1001, 2001)?.Z, Is.EqualTo(90));
        BotReleaseBindPoints.Replace(region, Array.Empty<DbBindPoint>());
        Assert.That(BotReleaseBindPoints.Nearest(region, 0, 0), Is.Null);
        Assert.That(BotReleaseBindPoints.Nearest(65002, 0, 0), Is.Null);
    }

    [Test]
    public void GroundRepairCannotSnapSidewaysDownAFloorOrAcceptANonFinitePoint()
    {
        Vector3 position = new(100, 200, 300);
        Assert.That(AutonomousNavigationSurface.IsLocalFloor(position, position + new Vector3(0,0,90)), Is.True);
        Assert.That(AutonomousNavigationSurface.IsLocalFloor(position, position - new Vector3(0,0,24)), Is.True,
            "Integral capital saves may sit slightly above their fractional Detour floor");
        Assert.That(AutonomousNavigationSurface.IsLocalFloor(position, position + new Vector3(3,0,90)), Is.False);
        Assert.That(AutonomousNavigationSurface.IsLocalFloor(position, position + new Vector3(0,0,129)), Is.False);
        Assert.That(AutonomousNavigationSurface.IsLocalFloor(position, position - new Vector3(0,0,33)), Is.False,
            "The repair must not drop through stacked floors or walkable props");
        Assert.That(AutonomousNavigationSurface.IsLocalFloor(position, position - new Vector3(0,0,80)), Is.False);
        Assert.That(AutonomousNavigationSurface.IsLocalFloor(position, new(float.NaN)), Is.False);
    }

    [Test]
    public void ZonePointFailureIsQuarantinedPerBotNotPerCapital()
    {
        string first = AutonomousWorldBotController.ZonePointQuarantineKey(101, 201, 209, 200, 19);
        string second = AutonomousWorldBotController.ZonePointQuarantineKey(202, 201, 209, 200, 19);
        Assert.That(first, Is.Not.EqualTo(second));
        Assert.That(first, Does.Contain(":201:209:200:19"));
    }

    [Test]
    public void AuditedRouteHotspotsAreStrictlyBounded()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRouteHotspotRepair.IsKnownFloorDriftArea(200,
                new(318807, 629660, 4959), out float connachtCorrection), Is.True);
            Assert.That(connachtCorrection, Is.EqualTo(384));
            Assert.That(AutonomousRouteHotspotRepair.IsKnownFloorDriftArea(1,
                new(556103, 560268, 2148), out float salisburyCorrection), Is.True);
            Assert.That(salisburyCorrection, Is.EqualTo(512));
            Assert.That(AutonomousRouteHotspotRepair.IsKnownFloorDriftArea(200,
                new(315453, 623125, 6638), out float pheulocCorrection), Is.True);
            Assert.That(pheulocCorrection, Is.EqualTo(2048));
            Assert.That(AutonomousRouteHotspotRepair.IsKnownFloorDriftArea(100,
                new(803743, 722129, 4684), out float mularnCorrection), Is.True);
            Assert.That(mularnCorrection, Is.EqualTo(256),
                "The Vale of Mularn/Aegir seam only permits its measured vertical repair");
            Assert.That(AutonomousRouteHotspotRepair.IsKnownFloorDriftArea(100,
                new(315453, 623125, 6638), out _), Is.False, "same coordinates in another region");
            Assert.That(AutonomousRouteHotspotRepair.IsKnownFloorDriftArea(200,
                new(310000, 620000, 6638), out _), Is.False, "unrelated Shannon Estuary terrain");
            Assert.That(AutonomousRouteHotspotRepair.IsJordheimServicePocket(101,
                new(31189, 27479, 8830)), Is.True);
            Assert.That(AutonomousRouteHotspotRepair.IsJordheimServicePocket(101,
                new(32499, 28664, 8830)), Is.True, "Cruella de Vil uses the same bounded service escape");
            Assert.That(AutonomousRouteHotspotRepair.IsJordheimServicePocket(101,
                new(31600, 27479, 8830)), Is.False);
            Assert.That(AutonomousRouteHotspotRepair.IsJordheimServicePocket(200,
                new(31189, 27479, 8830)), Is.False);
        });
    }

    [Test]
    public void FinishedShortSegmentsAreNotMisclassifiedAsRouteFailures()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousWorldBotController.IsRouteDestinationReached(
                new(100, 100, 100), new(148, 100, 196)), Is.True);
            Assert.That(AutonomousWorldBotController.IsRouteDestinationReached(
                new(100, 100, 100), new(149, 100, 100)), Is.False);
            Assert.That(AutonomousWorldBotController.IsRouteDestinationReached(
                new(100, 100, 100), new(100, 100, 197)), Is.False);
        });
    }

    [Test]
    public void OnlyAuditedShortZonePointEndpointsReceiveTheBoundedTolerance()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousWorldBotController.IsAuditedShortEndpoint(14), Is.True,
                "Connacht's previously audited portal keeps its narrow allowance");
            Assert.That(AutonomousWorldBotController.IsAuditedShortEndpoint(22), Is.True,
                "Jordheim's secondary exit has the same measured 197-unit endpoint gap");
            Assert.That(AutonomousWorldBotController.IsAuditedShortEndpoint(19), Is.True,
                "Tir na Nog's Lough Derg gate has a measured 195-unit endpoint gap");
            Assert.That(AutonomousWorldBotController.IsAuditedShortEndpoint(42), Is.True,
                "Cursed Tomb's Gotar portal has a measured 191-unit endpoint gap");
            Assert.That(AutonomousWorldBotController.IsAuditedShortEndpoint(165), Is.True,
                "Mularn's Aegir portal has a measured 199-unit endpoint gap");
            Assert.That(AutonomousWorldBotController.IsAuditedShortEndpoint(21), Is.False);
            Assert.That(AutonomousWorldBotController.IsAuditedShortEndpoint(23), Is.False);

            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                22, 197, 8, true, true, true), Is.True);
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                22, 209, 8, true, true, true), Is.False, "Never widen beyond the measured gap");
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                14, 213, 8, true, true, true), Is.True);
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                14, 225, 8, true, true, true), Is.False);
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                19, 195, 8, true, true, true), Is.True);
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                42, 191, 8, true, true, true), Is.True);
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                165, 199, 8, true, true, true), Is.True);
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                22, 197, 161, true, true, true), Is.False, "Never cross floors");
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                22, 197, 8, false, true, true), Is.False, "Never cross zones through proximity alone");
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                22, 197, 8, true, false, true), Is.False, "The actor must stand on usable mesh");
            Assert.That(AutonomousWorldBotController.CanUseAuditedShortEndpoint(
                22, 197, 8, true, true, false), Is.False, "The authored source must be usable mesh");

            Assert.That(AutonomousWorldBotController.CanUsePortal14Endpoint(
                14, 197, 8, true, true, true), Is.True);
            Assert.That(AutonomousWorldBotController.CanUsePortal14Endpoint(
                22, 197, 8, true, true, true), Is.False,
                "The compatibility helper remains exclusive to portal 14");
        });
    }
}
