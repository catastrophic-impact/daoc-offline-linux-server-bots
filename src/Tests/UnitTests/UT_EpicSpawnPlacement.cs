using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_EpicSpawnPlacement
    {
        [Test]
        public void CorrectionsOnlyMatchTheTwoExactLegacyPlacements()
        {
            foreach (var (name, position) in new[] {
                ("icebound skeleton", new Vector3(44673, 34675, 14483)),
                ("hrimthursa berg", new Vector3(29194, 33054, 15198)) })
            {
                var corrected = EpicSpawnPlacement.Correct(160, name, position);
                Assert.That(corrected, Is.Not.EqualTo(position));
                Assert.That(EpicSpawnPlacement.Correct(160, name, corrected), Is.EqualTo(corrected));
                Assert.That(EpicSpawnPlacement.Correct(191, name, position), Is.EqualTo(position));
                Assert.That(EpicSpawnPlacement.Correct(160, "another mob", position), Is.EqualTo(position));
                Assert.That(EpicSpawnPlacement.Correct(160, name, position + Vector3.One), Is.EqualTo(position + Vector3.One));
            }
            var flying = new Vector3(54323, 35754, 17613);
            Assert.That(EpicSpawnPlacement.Correct(160, "griffon icewing", flying), Is.EqualTo(flying));
        }
    }
}
