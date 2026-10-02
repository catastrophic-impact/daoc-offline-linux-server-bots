namespace DOL.GS
{
    /// <summary>
    /// A Bard's ordinary attack target is the party's kill target. Mesmerizing
    /// it delays the attack, is broken by allied damage, and is guaranteed to
    /// fail on an enraged NPC below 75% health. Keep mezzes out of the normal
    /// attack rotation; a separate grouped-PvE path may control a safe add.
    /// </summary>
    public static class BardBotCrowdControlPolicy
    {
        public static bool HasGroupForPveAdd(int? memberCount) => memberCount >= 2;

        public static bool AllowsOrdinaryOffense(eCharacterClass characterClass, eSpellType spellType) =>
            characterClass != eCharacterClass.Bard ||
            spellType is not (eSpellType.Mez or eSpellType.Mesmerize);

        public static bool IsSafePveAdd(bool grouped, bool hasOtherActiveTarget,
            bool attacksGroupMember, bool isGroupAttackTarget, bool isBardAttackTarget,
            int healthPercent, bool alreadyControlled, long now, long retryUntil) =>
            grouped && hasOtherActiveTarget && attacksGroupMember &&
            !isGroupAttackTarget && !isBardAttackTarget &&
            healthPercent == 100 && !alreadyControlled && now >= retryUntil;
    }
}
