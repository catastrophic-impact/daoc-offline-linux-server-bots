namespace DOL.GS;

public static class AutonomousRvrTargetPolicy
{
    public static bool IsEligible(eRealm actorRealm, eRealm targetRealm, bool targetAlive,
        bool sameRegion, bool targetInFrontier, bool eitherActorInSafeArea, bool serverAllowsAttack) =>
        actorRealm != eRealm.None && targetRealm != eRealm.None && actorRealm != targetRealm &&
        targetAlive && sameRegion && targetInFrontier && !eitherActorInSafeArea && serverAllowsAttack;
}
