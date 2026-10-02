using DOL.GS;
using DOL.Database;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_PlayerGeneratedCharmRanks
    {
        [TestCase(eCharacterClass.Sorcerer, 951, new[] {1,7,12,20,32})]
        [TestCase(eCharacterClass.Mentalist, 4211, new[] {4,10,17,25,33,42})]
        [TestCase(eCharacterClass.Minstrel, 1151, new[] {5,13,20,28,34,41})]
        [TestCase(eCharacterClass.Hunter, 3551, new[] {1,7,13,20,32})]
        [TestCase(eCharacterClass.Hunter, 3576, new[] {3,9,15,22,35})]
        public void EveryRankUsesItsOwnBonusIncludingOlderRanksAtFifty(eCharacterClass characterClass,
            int firstSpell, int[] unlocks)
        {
            for (int rank = 0; rank < unlocks.Length; rank++)
            {
                Assert.That(PlayerGeneratedCharmPolicy.TryGetRank(characterClass, firstSpell + rank,
                    out int unlock, out int bonus), Is.True);
                Assert.That(unlock, Is.EqualTo(unlocks[rank]));
                Assert.That(bonus, Is.EqualTo(rank - 3));
                Assert.That(PlayerGeneratedCharmPolicy.TargetLevel(characterClass, firstSpell + rank, 50),
                    Is.EqualTo(47 + rank));
                Assert.That(PlayerGeneratedCharmPolicy.TargetLevel(characterClass, firstSpell + rank, unlock - 1), Is.Zero);
            }
            Assert.That(PlayerGeneratedCharmPolicy.TargetLevel(eCharacterClass.Hunter, 3551, 1), Is.EqualTo(1));
        }

        [Test]
        public void SparsePoolsUseNearbyTypesWithoutWeightingDuplicateSpawns()
        {
            DbMob[] mobs = [new() {Name="a",Level=29}, new() {Name="a",Level=29},
                new() {Name="b",Level=28}, new() {Name="c",Level=30}, new() {Name="far",Level=40}];
            var pool = PlayerGeneratedCharmPolicy.SelectTemplates(mobs,29);
            Assert.That(pool.Length, Is.EqualTo(3));
            Assert.That(pool[0].Name, Is.EqualTo("a"));
            Assert.That(pool, Has.None.Property("Name").EqualTo("far"));
        }
    }
}
