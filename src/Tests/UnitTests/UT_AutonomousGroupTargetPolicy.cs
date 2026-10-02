using System;
using System.Linq;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_AutonomousGroupTargetPolicy
    {
        [TestCase(2, 1)] [TestCase(3, 1)] [TestCase(4, 2)] [TestCase(5, 2)]
        [TestCase(6, 2)] [TestCase(7, 3)] [TestCase(8, 3)]
        public void ExactRequestedGroupSizeMapping(int size, int bonus)
        {
            Assert.That(AutonomousGroupTargetPolicy.PreferredBonus(size), Is.EqualTo(bonus));
            Assert.That(AutonomousGroupTargetPolicy.SelectAvailableLevel(Enumerable.Range(1, 58), 10, size), Is.EqualTo(10 + bonus));
            foreach (int seed in Enumerable.Range(0, 20))
                Assert.That(AutonomousBotGroupCoordinator.RollPreferredLevelBonus(size, new Random(seed)), Is.InRange(3, 10));
        }

        [Test] public void FixedEightManTargetsStayBetweenPlusThreeAndPlusTen()
        {
            Assert.That(AutonomousGroupTargetPolicy.SelectFixedEightManLevel(new[] { 10, 12, 13, 16, 21 }, 10, 6),
                Is.EqualTo(16));
            Assert.That(AutonomousGroupTargetPolicy.SelectFixedEightManLevel(new[] { 10, 12, 21 }, 10, 10),
                Is.Zero);
        }

        [TestCase(1)] [TestCase(16)] [TestCase(21)] [TestCase(23)] [TestCase(50)] [TestCase(65)] [TestCase(255)]
        public void AssignedTargetExecutionHasNoLevelOrConCeiling(int level)
        {
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("forest hunter", "forest hunter", level), Is.True);
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("forest hunter", "unrelated monster", level), Is.False);
        }

        [Test]
        public void ForestHunterIncidentAcceptsAssignedLevelAndNearbyVariants()
        {
            Assert.That(AutonomousGroupTargetPolicy.SelectFixedEightManLevel(new[] {21, 23}, 13, 8), Is.EqualTo(21));
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("forest hunter", "Forest Hunter", 21), Is.True);
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("forest hunter", "forest hunter", 23), Is.True);
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("", "forest hunter", 21), Is.False);
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("wolf nipper", "wolf nipper", 0), Is.True,
                "level 0 starter-camp creatures are real XP targets for a level 1 bot");
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("wolf nipper", "forest hunter", 0), Is.False);
        }

        [Test] public void MissingPreferredTargetsFallBackDownwardNotToUnkillableMobs()
        {
            Assert.That(AutonomousGroupTargetPolicy.SelectAvailableLevel(new[] { 18, 14, 12, 10 }, 10, 8), Is.EqualTo(12));
            Assert.That(AutonomousGroupTargetPolicy.SelectAvailableLevel(new[] { 18, 14, 10 }, 10, 8), Is.EqualTo(10));
            Assert.That(AutonomousGroupTargetPolicy.SelectAvailableLevel(new[] { 14, 18 }, 10, 8), Is.Zero);
            Assert.That(AutonomousGroupTargetPolicy.SelectAvailableLevel(Array.Empty<int>(), 10, 8), Is.Zero);
        }

        [Test] public void WipesLowerActualFallbackTargetRatherThanUnusedNominalTarget()
        {
            int penalty = AutonomousGroupTargetPolicy.PenaltyAfterWipe(10, 8, 0, 10);
            Assert.That(AutonomousGroupTargetPolicy.PreferredLevel(10, 8, penalty), Is.EqualTo(9));
            Assert.That(AutonomousGroupTargetPolicy.SelectAvailableLevel(new[] { 8, 9, 10, 13 }, 10, 8, penalty), Is.EqualTo(9));
            penalty = AutonomousGroupTargetPolicy.PenaltyAfterWipe(10, 8, penalty, 9);
            Assert.That(AutonomousGroupTargetPolicy.SelectAvailableLevel(new[] { 8, 9, 10 }, 10, 8, penalty), Is.EqualTo(8));
        }

        [Test] public void ImpossibleDeathCeilingUsesSafestNonGreyCandidateWithoutRaisingInitialCap()
        {
            // Caller supplies already-filtered XP-bearing levels; L1 has no green alternative.
            int selected = AutonomousGroupTargetPolicy.SelectAvailableLevel(new[] { 1, 2, 5 }, 1, 2, 10);
            Assert.That(selected, Is.EqualTo(1));
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("frog", "frog", selected), Is.True);
            // Recovery restricts the next goal selection, not self-defense or
            // execution of another level of the already assigned monster.
            Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("frog", "frog", 2), Is.True);
        }

        [Test] public void SharedPlanningLevelDoesNotImposeAnotherCombatCeiling()
        {
            var shared = new AutonomousBotGroupCoordinator.SharedCamp("camp", "frog", "zone", 200, 1, 2, 3, true, false, 9);
            Assert.That(shared.TargetLevel, Is.EqualTo(9));
            foreach (int level in new[] {1, 9, 10, 14, 255})
                Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget(shared.MonsterName, "frog", level), Is.True);
        }

        [Test] public void AllLevelsAndGroupSizesStepDownUntilOnlyNonGreyFloorRemains()
        {
            for (int average = 1; average <= 50; average++)
            for (int size = 2; size <= 8; size++)
            {
                int[] levels = Enumerable.Range(1, 60).Where(level =>
                    ConLevels.GetConColor(ConLevels.GetConLevel(average, level)) > ConColor.GREY).ToArray();
                int penalty = 0;
                int selected = AutonomousGroupTargetPolicy.SelectAvailableLevel(levels, average, size, penalty);
                Assert.That(selected, Is.EqualTo(average + AutonomousGroupTargetPolicy.PreferredBonus(size)));
                for (int wipe = 0; wipe < 50; wipe++)
                {
                    penalty = AutonomousGroupTargetPolicy.PenaltyAfterWipe(average, size, penalty, selected);
                    int next = AutonomousGroupTargetPolicy.SelectAvailableLevel(levels, average, size, penalty);
                    Assert.That(next, Is.LessThanOrEqualTo(selected));
                    if (selected > levels.Min()) Assert.That(next, Is.LessThan(selected));
                    Assert.That(AutonomousPveTargetPolicy.IsAssignedTarget("frog", "frog", next), Is.True);
                    selected = next;
                }
            }
        }
    }
}
