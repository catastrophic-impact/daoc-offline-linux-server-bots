using System;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_BountyQuestWaypoint
{
    [Test]
    public void BountyWaypointPopulatesBothNativeQuestPacketLocations()
    {
        var goal = new RewardQuest.QuestGoal("bounty", new RewardQuest(), "Defeat the target",
            RewardQuest.QuestGoal.GoalType.KillTask, 1, 12, null);

        // The 1.127 QuestEntry packet reads both triplets; x/y are zone-local.
        goal.SetWaypoint(129, 33486 - 8192, 32889 - 8192);

        Assert.Multiple(() =>
        {
            Assert.That(goal.ZoneID1, Is.EqualTo(129));
            Assert.That(goal.XOffset1, Is.EqualTo(25294));
            Assert.That(goal.YOffset1, Is.EqualTo(24697));
            Assert.That(goal.ZoneID2, Is.EqualTo(129));
            Assert.That(goal.XOffset2, Is.EqualTo(25294));
            Assert.That(goal.YOffset2, Is.EqualTo(24697));
        });
    }

    [Test]
    public void WaypointRejectsCoordinatesTheQuestPacketWouldTruncate()
    {
        var goal = new RewardQuest.QuestGoal("bounty", new RewardQuest(), "Defeat the target",
            RewardQuest.QuestGoal.GoalType.KillTask, 1, 12, null);

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => goal.SetWaypoint(1, -1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => goal.SetWaypoint(1, 10, 65536));
        });
    }
}
