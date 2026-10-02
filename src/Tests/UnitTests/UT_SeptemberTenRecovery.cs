using System.Numerics;
using DOL.AI.Brain;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SeptemberTenRecovery
    {
        [TestCase(46,0)] [TestCase(47,0)] [TestCase(40,40)]
        public void BoardingApproachDoesNotStopOutsideTheActualBoardingRadius(int x,int z)
        {
            Assert.That(AutonomousWorldBotController.IsRouteDestinationReached(new(x,0,z),Vector3.Zero,true),Is.False);
        }

        [Test]
        public void DungeonPullSurvivesSixSecondIdleTimeoutButNotIndefinitely()
        {
            Assert.That(BotBrain.RetainCommittedDungeonPull(true,true,true,7000,30000),Is.True);
            Assert.That(BotBrain.RetainCommittedDungeonPull(true,true,true,30000,30000),Is.False);
            Assert.That(BotBrain.RetainCommittedDungeonPull(false,true,true,7000,30000),Is.False);
            Assert.That(BotBrain.RetainCommittedDungeonPull(true,false,true,7000,30000),Is.False);
            Assert.That(BotBrain.RetainCommittedDungeonPull(true,true,false,7000,30000),Is.False);
        }
    }
}
