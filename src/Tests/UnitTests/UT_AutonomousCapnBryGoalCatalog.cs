using System.Linq;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_AutonomousCapnBryGoalCatalog
{
    [Test]
    public void EmbeddedCatalogContainsClassicShroudedIslesAndDarknessFalls()
    {
        var entries = AutonomousCapnBryGoalCatalog.Entries;

        Assert.Multiple(() =>
        {
            Assert.That(entries.Count, Is.GreaterThan(5000));
            Assert.That(entries.Any(entry => entry.ZoneId == 0), Is.True, "Classic Camelot Hills is absent");
            Assert.That(entries.Any(entry => entry.ZoneId == 51), Is.True, "Albion SI is absent");
            Assert.That(entries.Any(entry => entry.ZoneId == 151), Is.True, "Midgard SI is absent");
            Assert.That(entries.Any(entry => entry.ZoneId == 249), Is.True, "Darkness Falls is absent");
            Assert.That(AutonomousCapnBryGoalCatalog.SourceEmptySupportedZoneIds.Contains(181), Is.True,
                "CapnBry's empty Hibernia SI coverage must retain the bounded live fallback");
            Assert.That(AutonomousCapnBryGoalCatalog.CoveredZoneIds.Contains(181), Is.False,
                "A source-empty zone must not be presented as CapnBry-covered");
        });
    }

    [Test]
    public void EmbeddedCatalogContainsOnlyLowercaseLevelValidNonNewFrontierEntries()
    {
        var entries = AutonomousCapnBryGoalCatalog.Entries;

        Assert.Multiple(() =>
        {
            Assert.That(entries.All(entry => entry.Name == entry.Name.ToLowerInvariant()), Is.True);
            Assert.That(entries.All(entry => entry.Levels.Length > 0 && entry.Levels.All(level => level is >= 1 and <= 50)), Is.True);
            Assert.That(entries.All(entry => entry.ZoneId is < 163 or > 178), Is.True);
        });
    }

    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, false)]
    [TestCase(0, false)]
    public void ExpansionBoundaryUsesRegionOneBasedConvention(int expansion, bool expected)
    {
        Assert.That(AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(expansion), Is.EqualTo(expected));
    }
}
