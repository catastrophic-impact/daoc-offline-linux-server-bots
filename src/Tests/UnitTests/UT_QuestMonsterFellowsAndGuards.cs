using DOL.GS;
using DOL.AI.Brain;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_QuestMonsterFellowsAndGuards
    {
        [Test]
        public void GuardsLeaveAQuestMonsterAtHomeAndFightOneFarFromIt()
        {
            // List of Denial's thieves inside Camelot stay the player's fight.
            Assert.That(GuardBrain.LeavesToPlayer(true, 0), Is.True);
            Assert.That(GuardBrain.LeavesToPlayer(true, GuardBrain.QuestMonsterHomeRadius), Is.True);
            // Fjall at the Vindsaul gate, about 41,000 units from his hut in the fenrir fort, is fought.
            Assert.That(GuardBrain.LeavesToPlayer(true, 41_000), Is.False);
            // Ordinary monsters are fought anywhere.
            Assert.That(GuardBrain.LeavesToPlayer(false, 0), Is.False);
        }

        [Test]
        public void FellowsUseTheirOwnIdleAndFightStates()
        {
            var brain = new QuestFellowBrain();
            Assert.That(brain.FSM.GetState(eFSMStateType.IDLE), Is.InstanceOf<QuestFellowState_IDLE>());
            Assert.That(brain.FSM.GetState(eFSMStateType.AGGRO), Is.InstanceOf<QuestFellowState_AGGRO>());
            Assert.That(brain.FSM.GetState(eFSMStateType.PATROLLING), Is.InstanceOf<StandardMobState_PATROLLING>());
        }
    }
}
