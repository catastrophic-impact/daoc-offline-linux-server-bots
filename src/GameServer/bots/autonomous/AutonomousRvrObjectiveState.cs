namespace DOL.GS;

/// <summary>Pure, observable phases for live RvR execution; no phase changes ownership.</summary>
public enum eAutonomousRvrPhase
{
    Recon,
    Traveling,
    BreachingDoor,
    ClearingKeep,
    AssaultingLord,
    HuntingEnemy,
    RelicRequiresPlayerCarrier,
}

public static class AutonomousRvrObjectiveState
{
    public static eAutonomousRvrPhase ForTarget(GameLiving target) => target switch
    {
        DOL.GS.Keeps.GameKeepDoor => eAutonomousRvrPhase.BreachingDoor,
        DOL.GS.Keeps.GuardLord => eAutonomousRvrPhase.AssaultingLord,
        DOL.GS.Keeps.GameKeepGuard => eAutonomousRvrPhase.ClearingKeep,
        GameBot => eAutonomousRvrPhase.HuntingEnemy,
        _ => eAutonomousRvrPhase.Recon,
    };

    public static eAutonomousRvrPhase Next(bool atObjective, bool closedEnemyDoor, bool enemyLordAlive, bool enemyBotVisible, bool relicAvailable) =>
        !atObjective ? eAutonomousRvrPhase.Traveling :
        closedEnemyDoor ? eAutonomousRvrPhase.BreachingDoor :
        enemyLordAlive ? eAutonomousRvrPhase.AssaultingLord :
        enemyBotVisible ? eAutonomousRvrPhase.HuntingEnemy :
        relicAvailable ? eAutonomousRvrPhase.RelicRequiresPlayerCarrier :
        eAutonomousRvrPhase.Recon;
}
