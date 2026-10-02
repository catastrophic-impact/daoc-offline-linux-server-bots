using System.Numerics;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_DarknessFallsGrindPolicy
{
    [Test]
    public void OrdinaryGoals_KeepLowLevelCampsInTheHomeWing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(
                eRealm.Midgard, eRealm.Midgard, 16, 8_000, 8_000), Is.True);
            Assert.That(AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(
                eRealm.Midgard, eRealm.Albion, 16, 45_000, 17_000), Is.False,
                "A Midgard solo bot must not cross DF to an Albion-side familiar.");
            Assert.That(AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(
                eRealm.Hibernia, eRealm.Albion, 22, 53_000, 10_000), Is.False);
            Assert.That(AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(
                eRealm.None, eRealm.None, 20, 1_000, 1_000), Is.False);
        });
    }

    [Test]
    public void OrdinaryGoals_AllowHighLevelSharedInteriorButNotOppositeEntrance()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(
                eRealm.Hibernia, eRealm.Albion, 49, 36_000, 27_000), Is.True,
                "The higher-level shared interior remains a valid destination.");
            Assert.That(AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(
                eRealm.Midgard, eRealm.Albion, 49, 47_000, 7_000), Is.False,
                "A high-level bot still cannot use the opposite entrance as a local camp.");
            Assert.That(AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(
                eRealm.Albion, eRealm.Midgard, 49, float.PositiveInfinity, 25_000), Is.False);
        });
    }

    [Test]
    public void EmptyRoomContinuation_RequiresNearbySameNameAndOwnWing()
    {
        Vector3 current = new(34_611, 23_560, 21_778);
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousDarknessFallsNavigation.IsNearbySameWingCamp(
                eRealm.Albion, eRealm.Albion, "demoniac familiar", "Demoniac Familiar",
                current, new(33_833, 23_535, 21_505), 5_000), Is.True);
            Assert.That(AutonomousDarknessFallsNavigation.IsNearbySameWingCamp(
                eRealm.Albion, eRealm.Midgard, "demoniac familiar", "demoniac familiar",
                current, new(33_833, 23_535, 21_505), 5_000), Is.False);
            Assert.That(AutonomousDarknessFallsNavigation.IsNearbySameWingCamp(
                eRealm.Albion, eRealm.Albion, "demoniac familiar", "plated fiend",
                current, new(33_833, 23_535, 21_505), 5_000), Is.False);
            Assert.That(AutonomousDarknessFallsNavigation.IsNearbySameWingCamp(
                eRealm.Albion, eRealm.Albion, "demoniac familiar", "demoniac familiar",
                current, new(10_000, 10_000, 21_505), 5_000), Is.False);
        });
    }
}
