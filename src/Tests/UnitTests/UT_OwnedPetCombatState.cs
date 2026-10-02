using DOL.AI;
using DOL.AI.Brain;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_OwnedPetCombatState
{
    private sealed class State : FSMState
    {
        public int Entries, Exits, DrawPulses, ReleasedShots;
        public State(eFSMStateType type) => StateType = type;
        public override void Enter() => Entries++;
        public override void Exit() { Exits++; DrawPulses = 0; }
        public override void Think()
        {
            if (++DrawPulses == 10) { ReleasedShots++; DrawPulses = 0; }
        }
    }

    [Test]
    public void RepeatedPetTargetAdoptionDoesNotCancelAnExistingDraw()
    {
        var fsm = new FSM();
        var idle = new State(eFSMStateType.IDLE);
        var combat = new State(eFSMStateType.AGGRO);
        fsm.Add(idle); fsm.Add(combat);
        fsm.SetCurrentState(eFSMStateType.IDLE);
        // Real FSM transition semantics, with a draw spanning ten owner turns.
        // The old unconditional SetCurrentState resets the draw on every turn.
        for (int i = 0; i < 50; i++)
        {
            BotBrain.EnterOwnedPetCombatState(fsm);
            fsm.Think();
        }
        Assert.Multiple(() =>
        {
            Assert.That(idle.Exits, Is.EqualTo(1));
            Assert.That(combat.Entries, Is.EqualTo(1));
            Assert.That(combat.Exits, Is.Zero);
            Assert.That(combat.ReleasedShots, Is.EqualTo(5));
        });
        // A real combat end and subsequent pet engagement must still transition.
        fsm.SetCurrentState(eFSMStateType.IDLE);
        BotBrain.EnterOwnedPetCombatState(fsm);
        Assert.That(combat.Entries, Is.EqualTo(2));
        Assert.That(combat.Exits, Is.EqualTo(1));
    }

    [Test]
    public void SharedFsmExplicitReentrySemanticsRemainUnchanged()
    {
        var fsm = new FSM();
        var combat = new State(eFSMStateType.AGGRO);
        fsm.Add(combat);
        fsm.SetCurrentState(eFSMStateType.AGGRO);
        fsm.SetCurrentState(eFSMStateType.AGGRO);
        Assert.That(combat.Entries, Is.EqualTo(2));
        Assert.That(combat.Exits, Is.EqualTo(1));
    }
}
