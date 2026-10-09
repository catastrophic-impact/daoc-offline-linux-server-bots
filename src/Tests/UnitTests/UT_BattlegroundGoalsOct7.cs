using System;
using System.IO;
using System.Linq;
using DOL.GS;
using NUnit.Framework;
using OfflineDaoc.Configuration;

namespace DOL.UnitTests
{
    /// <summary>
    /// Battlegrounds as the fourth bot goal (owner, 2026-10-07): brackets by level only, 0% by default,
    /// never for level 50, and battleground slots only for bots whose level has a bracket.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class UT_BattlegroundGoalsOct7
    {
        [TestCase(14, null)] [TestCase(15, "Abermenai")] [TestCase(19, "Abermenai")]
        [TestCase(20, "Thidranki")] [TestCase(24, "Thidranki")] [TestCase(25, "Murdaigean")] [TestCase(29, "Murdaigean")]
        [TestCase(30, "Caledonia")] [TestCase(35, "Caledonia")] [TestCase(36, null)] [TestCase(50, null)]
        public void BracketsAreByLevelOnly(int level, string expected) =>
            Assert.That(BattlegroundBrackets.ForLevel(level)?.Name, Is.EqualTo(expected));

        [Test]
        public void DefaultsKeepBattlegroundsOff()
        {
            BotGoalSettings defaults = BotGoalSettings.Defaults;
            Assert.That(new[] { defaults.Levels1To19, defaults.Levels20To49, defaults.Level50 }.Select(row => row.Battlegrounds), Has.All.EqualTo(0));
        }

        [Test]
        public void OldSettingsFilesWithoutTheColumnStillLoad()
        {
            string directory = Path.Combine(Path.GetTempPath(), "bg-goal-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, BotGoalSettings.FileName);
                File.WriteAllText(path, "{\"Version\":1,\"Levels1To19\":{\"SoloPve\":90,\"GroupPve\":10,\"RvR\":0}," +
                    "\"Levels20To49\":{\"SoloPve\":60,\"GroupPve\":40,\"RvR\":0},\"Level50\":{\"SoloPve\":20,\"GroupPve\":60,\"RvR\":20}}");
                BotGoalSettings loaded = BotGoalSettings.Load(path);
                Assert.That(loaded.Levels20To49, Is.EqualTo(new BotGoalWeights(60, 40, 0, 0)));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void LevelFiftyCannotHaveBattlegrounds() =>
            Assert.Throws<InvalidDataException>(() =>
                (BotGoalSettings.Defaults with { Level50 = new BotGoalWeights(20, 40, 30, 10) }).Validate());

        [Test]
        public void ChooseSplitsAllFourGoals()
        {
            var weights = new BotGoalWeights(40, 20, 10, 30);
            int[] choices = Enumerable.Range(0, 10000).Select(i => weights.Choose(i / 10000d)).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(choices.Count(i => i == 0), Is.EqualTo(4000));
                Assert.That(choices.Count(i => i == 1), Is.EqualTo(2000));
                Assert.That(choices.Count(i => i == 2), Is.EqualTo(1000));
                Assert.That(choices.Count(i => i == 3), Is.EqualTo(3000));
            });
        }

        [Test]
        public void OutOfBracketBotsNeverRollBattlegrounds()
        {
            var weights = new BotGoalWeights(40, 20, 10, 30);
            Assert.That(Enumerable.Range(0, 1000).Select(i => weights.Choose(i / 1000d, excludeBattlegrounds: true)), Has.None.EqualTo(3));
            // A battleground-only row grinds solo while no bracket fits.
            Assert.That(new BotGoalWeights(0, 0, 0, 100).Choose(0.5, excludeBattlegrounds: true), Is.EqualTo(0));
        }

        [TestCase(10, 20, 30, 40, 100, 40)]
        [TestCase(10, 20, 30, 40, 25, 25)]
        [TestCase(10, 20, 30, 40, 0, 0)]
        public void UnusableBattlegroundSlotsReturnToPve(int solo, int group, int rvr, int battlegrounds, int eligible, int expected)
        {
            var target = new AutonomousObjectiveAssignments.Allocation(solo, group, rvr, battlegrounds);
            var limited = AutonomousObjectiveAssignments.LimitBattlegrounds(target, eligible);
            Assert.Multiple(() =>
            {
                Assert.That(limited.Battlegrounds, Is.EqualTo(expected));
                Assert.That(limited.RvR, Is.EqualTo(rvr));
                Assert.That(limited.SoloPve + limited.GroupPve + limited.RvR + limited.Battlegrounds, Is.EqualTo(solo + group + rvr + battlegrounds));
            });
        }
    }
}
