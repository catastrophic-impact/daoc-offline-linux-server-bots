using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_AutonomousBotMapMarkers
{
    [Test]
    public void VerticalFloorTransitionsRefreshEvenWithoutHorizontalMovement()
    {
        Assert.That(AutonomousBotMapMarkers.HasChangedFloorHeight(16202, 16000), Is.True);
        Assert.That(AutonomousBotMapMarkers.HasChangedFloorHeight(16202, 16202), Is.False);
    }

    [Test]
    public void StationaryInteriorMarkersReceiveBoundedRefreshes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousBotMapMarkers.ShouldRefreshStationaryInterior(true, 11_999), Is.False);
            Assert.That(AutonomousBotMapMarkers.ShouldRefreshStationaryInterior(true, 12_000), Is.True);
            Assert.That(AutonomousBotMapMarkers.ShouldRefreshStationaryInterior(false, 60_000), Is.False);
        });
    }

    [Test]
    public void InteriorMarkersUseTheClientVisibleRegionSkin()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousBotMapMarkers.ClientMapRegion(20001, 100), Is.EqualTo(100));
            Assert.That(AutonomousBotMapMarkers.ClientMapRegion(100, 0), Is.EqualTo(100));
        });
    }

    [Test]
    public void DungeonMarkersUseActualRegionAndGlobalCoordinatesOnEveryFloor()
    {
        var marker = AutonomousBotMapMarkers.ResolveClientMapPosition(
            true, 129, 129, 129, 8192, 8192, 33486, 32889, 16202);
        Assert.Multiple(() =>
        {
            Assert.That(marker.MapId, Is.EqualTo(129));
            Assert.That(marker.X, Is.EqualTo(33486));
            Assert.That(marker.Y, Is.EqualTo(32889));
            Assert.That(marker.Z, Is.EqualTo(16202));
        });
    }

    [Test]
    public void CapitalMarkersUseActualRegionAndGlobalCoordinates()
    {
        var jordheim = AutonomousBotMapMarkers.ResolveClientMapPosition(
            true, 101, 101, 120, 8192, 8192, 30000, 28000, 9000);
        Assert.Multiple(() =>
        {
            Assert.That(jordheim.MapId, Is.EqualTo(101));
            Assert.That(jordheim.X, Is.EqualTo(30000));
            Assert.That(jordheim.Y, Is.EqualTo(28000));
        });
    }

    [Test]
    public void OutdoorMarkersRetainExistingRegionGlobalCoordinateContract()
    {
        var marker = AutonomousBotMapMarkers.ResolveClientMapPosition(
            false, 100, 100, 101, 655360, 655360, 712480, 783994, 4798);
        Assert.Multiple(() =>
        {
            Assert.That(marker.MapId, Is.EqualTo(100));
            Assert.That(marker.X, Is.EqualTo(712480));
            Assert.That(marker.Y, Is.EqualTo(783994));
        });
    }
}
