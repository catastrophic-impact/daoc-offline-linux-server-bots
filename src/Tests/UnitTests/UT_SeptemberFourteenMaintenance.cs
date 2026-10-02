using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public class UT_SeptemberFourteenMaintenance
{
    [Test]
    public void ProgressingBoardingApproachGetsBoundedExtraTimeNotAnInfiniteWait()
    {
        Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(true,1000,131000,120000),Is.False);
        Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(true,1000,131000,20000),Is.True);
        Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(true,1000,301000,295000),Is.True);
        Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(false,1000,301000,20000),Is.False);
        Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCountForRegion(23),Is.GreaterThanOrEqualTo(124));
    }
    private static Spell Pulse(int id, string type, int level) => new(new DbSpell
    {
        SpellID = id, Name = "Test pulse", Type = type, Target = "Group", Pulse = 6,
        CastTime = 4, Value = level
    }, level);

    [Test]
    public void CasterTravelDoesNotAlternateSpeedAndBladeturn()
    {
        Spell speed = Pulse(1, "SpeedEnhancement", 35);
        Spell protection = Pulse(2, "Bladeturn", 50);
        Assert.That(speed.IsPulsing, Is.True);
        for (int tick = 0; tick < 20; tick++)
        {
            int selected = BotMaintenancePulsePolicy.Choose([speed, protection], true);
            Assert.That(selected, Is.EqualTo(speed.ID));
            Assert.That(BotMaintenancePulsePolicy.CanMaintain(protection, selected), Is.False);
        }
        Assert.That(BotMaintenancePulsePolicy.Choose([speed, protection], false), Is.EqualTo(protection.ID));
        Assert.That(BotMaintenancePulsePolicy.Choose([protection], true), Is.EqualTo(protection.ID));
    }

    [Test]
    public void OrdinaryBuffsRemainAvailableAndDarknessFallsCannotBeAGoal()
    {
        Spell ordinary = new(new DbSpell { SpellID = 3, Type = "StrengthBuff", Target = "Realm", Duration = 600 }, 1);
        Assert.That(BotMaintenancePulsePolicy.CanMaintain(ordinary, 2), Is.True);
        Assert.That(AutonomousDungeonPolicy.IsSupportedDungeonZone(249), Is.False);
        Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(249, "lilispawn"), Is.False);
        Assert.That(AutonomousDungeonPolicy.IsSupportedDungeonZone(23), Is.True);
        Assert.That(AutonomousDarknessFallsPolicy.CanUseRegionEdge(eRealm.Albion, 249, 1, _ => false), Is.True);
    }

    [Test]
    public void FullDragonRallyWaitsForLandingButUndersizedRallyStillExpires()
    {
        long deadline = RealmRaidRecruitmentPolicy.AutonomousStagingLimitMilliseconds;
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(false, deadline, 218, false), Is.False);
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(true, deadline, 200, false), Is.False);
        Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(false, deadline, 199, false), Is.True);
        Assert.That(RealmRaidRecruitmentPolicy.Ready(false, deadline, 218, true), Is.True);
        Assert.That(RealmRaidRecruitmentPolicy.BattleMilliseconds, Is.EqualTo(4 * 60 * 60_000L));
    }

    [Test]
    public void WaitingKeepDefenderIsProtectedButDistantTravelIsNot()
    {
        Assert.That(AutonomousRvrEventLayer.IsHoldingSiegeDefense(true, true, 800 * 800, 500), Is.True);
        Assert.That(AutonomousRvrEventLayer.IsHoldingSiegeDefense(true, true, 4000 * 4000, 0), Is.False);
        Assert.That(AutonomousRvrEventLayer.IsHoldingSiegeDefense(true, false, 0, 0), Is.False);
        Assert.That(AutonomousRvrEventLayer.IsHoldingSiegeDefense(false, true, 0, 0), Is.False);
    }
}
