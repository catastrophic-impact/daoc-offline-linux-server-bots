using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// A pet summoned at a zone's outer edge used to throw (no zone ahead of the owner), and the
    /// casting service removed the owner from the world: one Necromancer gamebot was reloaded and
    /// kicked about every 20 seconds. The pet now appears on top of its owner instead.
    /// </summary>
    [TestFixture]
    public class UT_PetSummonSpot
    {
        [Test]
        public void OffTheMapSpotSummonsOnTheOwner() =>
            Assert.That(SummonSpellHandler.ChoosePetSpot(null, new Point2D(461895, 507943), 461895, 507879, 2912),
                Is.EqualTo((461895, 507879, 2912)));

        [Test]
        public void ZoneWithoutNavmeshKeepsTheSpotInFront()
        {
            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new RegionData { Id = 51 });
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new List<Zone>());
            var zone = new Zone(region, 52, "Avalon Isle", 425984, 442368, 65536, 65536, 52, false, 0, false, 0, 0, 0, 0, 0);
            Assert.That(zone.IsPathfindingEnabled, Is.False);
            Assert.That(SummonSpellHandler.ChoosePetSpot(zone, new Point2D(461895, 507800), 461895, 507736, 2912),
                Is.EqualTo((461895, 507800, 2912)));
        }
    }
}
