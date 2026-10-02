using System;
using System.IO;
using System.Linq;
using DOL.GS;
using NUnit.Framework;
using OfflineDaoc.Configuration;

[TestFixture, NonParallelizable]
public class UT_BotGoalSettings
{
    private string _directory;
    private string PathName => Path.Combine(_directory, BotGoalSettings.FileName);
    [SetUp] public void Setup() { _directory = Path.Combine(Path.GetTempPath(), "bot-goal-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory); }
    [TearDown] public void Cleanup()
    {
        File.Delete(PathName);
        AutonomousBotGoalPolicy.Initialize(_directory);
        Directory.Delete(_directory, true);
    }
    private void Apply(BotGoalWeights weights)
    {
        (BotGoalSettings.Defaults with { Levels20To49 = weights, Level50 = weights }).Save(PathName, () => true);
        AutonomousBotGoalPolicy.Initialize(_directory);
    }

    [TestCase(1, 60, 40, 0)] [TestCase(19, 60, 40, 0)]
    [TestCase(20, 40, 40, 20)] [TestCase(49, 40, 40, 20)] [TestCase(50, 20, 40, 40)]
    public void DefaultsAndBrackets(int level, int solo, int group, int rvr) =>
        Assert.That(BotGoalSettings.Defaults.ForLevel(level), Is.EqualTo(new BotGoalWeights(solo, group, rvr)));

    [TestCase(100, 0, 0, 0)] [TestCase(0, 100, 0, 1)] [TestCase(0, 0, 100, 2)]
    public void ExclusivePoliciesApplyToAllocationRollsRecoveryAndOldEligibility(int solo, int group, int rvr, int kind)
    {
        Apply(new(solo, group, rvr));
        foreach (int level in new[] { 20, 49, 50 })
        {
            for (int seed = 0; seed < 300; seed++)
                Assert.That((int)AutonomousBotGoalPolicy.Choose(level, new Random(seed)), Is.EqualTo(kind));
            foreach (eAutonomousObjectiveKind previous in Enum.GetValues<eAutonomousObjectiveKind>())
                Assert.That((int)AutonomousBotGoalPolicy.EnsureAllowed(level, previous), Is.EqualTo(kind));
            var record = new OfflineWorldBotRecord { Level = level, ObjectiveRvrEligibleUtc = AutonomousObjectiveAssignments.PveCompletionRequired };
            Assert.That(AutonomousObjectiveAssignments.IsRvrEligible(record, DateTime.UtcNow), Is.EqualTo(rvr > 0));
        }
        foreach (bool fifty in new[] { false, true })
        {
            var allocation = AutonomousObjectiveAssignments.TargetForPopulation(101, fifty);
            Assert.That(allocation, Is.EqualTo(new AutonomousObjectiveAssignments.Allocation(solo > 0 ? 101 : 0, group > 0 ? 101 : 0, rvr > 0 ? 101 : 0)));
        }
    }

    [TestCase(0, 25, 75)] [TestCase(25, 0, 75)] [TestCase(25, 75, 0)]
    public void ZeroWeightsNeverAppearInMixedPolicies(int solo, int group, int rvr)
    {
        var weights = new BotGoalWeights(solo, group, rvr);
        var choices = Enumerable.Range(0, 10000).Select(i => weights.Choose(i / 10000d)).ToArray();
        Assert.That(choices.Count(i => i == 0), Is.EqualTo(solo * 100));
        Assert.That(choices.Count(i => i == 1), Is.EqualTo(group * 100));
        Assert.That(choices.Count(i => i == 2), Is.EqualTo(rvr * 100));
    }

    [TestCase(0, 100, 0, 1)] [TestCase(0, 50, 50, 2)] [TestCase(50, 50, 0, 0)]
    public void MatchmakingFallbackNeverInventsADisabledGoal(int solo, int group, int rvr, int expected)
    {
        var weights = new BotGoalWeights(solo, group, rvr);
        Assert.That(Enumerable.Range(0, 100).Select(i => weights.Choose(i / 100d, true)), Has.All.EqualTo(expected));
    }

    [Test]
    public void DisabledSavedTaskIsClearedWithoutTouchingProgress()
    {
        Apply(new(0, 0, 100));
        var record = new OfflineWorldBotRecord { Level = 50, Experience = 12345, Name = "PreserveMe", RegionId = 1, X = 456,
            ObjectiveKind = "GroupPve", ObjectiveAssignmentId = "old-group", ObjectiveExpiresUtc = DateTime.UtcNow.AddHours(1).ToString("O"),
            CurrentCampId = "old-camp", TargetName = "old-target", ObjectiveRvrEligibleUtc = AutonomousObjectiveAssignments.PveCompletionRequired };
        Assert.That(AutonomousBotGoalPolicy.ReconcileSavedAssignment(record), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(record.ObjectiveKind, Is.EqualTo("RvR"));
            Assert.That(record.ObjectiveAssignmentId, Is.Empty);
            Assert.That(record.ObjectiveExpiresUtc, Is.Empty);
            Assert.That(record.CurrentCampId, Is.Empty);
            Assert.That(record.ObjectiveRvrEligibleUtc, Is.Empty);
            Assert.That(record.Experience, Is.EqualTo(12345));
            Assert.That(record.X, Is.EqualTo(456));
            Assert.That(record.Name, Is.EqualTo("PreserveMe"));
        });
    }

    [Test]
    public void AllowedTaskAndNecessaryServicePhaseArePreserved()
    {
        Apply(new(0, 0, 100));
        foreach (var (kind, id) in new[] { ("RvR", "current-rvr"), ("SoloPve", "between-pve-services-T-1-123") })
        {
            var record = new OfflineWorldBotRecord { Level = 50, ObjectiveKind = kind, ObjectiveAssignmentId = id };
            Assert.That(AutonomousBotGoalPolicy.ReconcileSavedAssignment(record), Is.False);
            Assert.That(record.ObjectiveAssignmentId, Is.EqualTo(id));
        }
    }

    [Test]
    public void LowLevelExclusivePoliciesAndRoundingRemainLegal()
    {
        foreach (var weights in new[] { new BotGoalWeights(100, 0, 0), new BotGoalWeights(0, 100, 0), new BotGoalWeights(1, 99, 0) })
        {
            (BotGoalSettings.Defaults with { Levels1To19 = weights }).Save(PathName, () => true);
            AutonomousBotGoalPolicy.Initialize(_directory);
            for (int count = 1; count < 100; count++)
            {
                var target = AutonomousObjectiveAssignments.TargetForLowLevelPopulation(count);
                Assert.That(target.RvR, Is.Zero);
                Assert.That(target.SoloPve + target.GroupPve, Is.EqualTo(count));
                Assert.That(AutonomousBotGoalPolicy.Choose(19), Is.Not.EqualTo(eAutonomousObjectiveKind.RvR));
            }
        }
    }

    [Test]
    public void InvalidSettingsCannotReplaceWorkingFileAndServerStartRaceIsRejected()
    {
        BotGoalSettings.Defaults.Save(PathName, () => true);
        string original = File.ReadAllText(PathName);
        foreach (var bad in new[] { new BotGoalWeights(-1, 101, 0), new(1, 1, 1), new(0, 0, 0), new(0, 0, 100) })
            Assert.Throws<InvalidDataException>(() => (BotGoalSettings.Defaults with { Levels1To19 = bad }).Save(PathName, () => true));
        Assert.Throws<InvalidOperationException>(() => BotGoalSettings.Defaults.Save(PathName, () => false));
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => BotGoalSettings.Defaults.Save(PathName, () => ++calls == 1));
        Assert.That(File.ReadAllText(PathName), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
    }

    [Test]
    public void PortableRoundTripAndMissingFileLegacyBehavior()
    {
        AutonomousBotGoalPolicy.Initialize(_directory);
        Assert.That(AutonomousBotGoalPolicy.IsConfigured, Is.False);
        Apply(new(0, 0, 100));
        Assert.That(BotGoalSettings.Load(PathName).Level50, Is.EqualTo(new BotGoalWeights(0, 0, 100)));
        Assert.That(File.ReadAllText(PathName), Does.Not.Contain(_directory));
        Assert.That(AutonomousBotGoalPolicy.IsConfigured, Is.True);
    }

    [TestCase("{}")] [TestCase("null")] [TestCase("not json")]
    [TestCase("{\"Version\":1,\"Levels1To19\":null,\"Levels20To49\":null,\"Level50\":null}")]
    public void BrokenFilesAreRejectedInsteadOfSilentlyRestoringDisabledGoals(string json)
    {
        File.WriteAllText(PathName, json);
        Assert.That(() => AutonomousBotGoalPolicy.Initialize(_directory), Throws.Exception);
    }
}
