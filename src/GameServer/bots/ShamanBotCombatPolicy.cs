namespace DOL.GS
{
    /// <summary>
    /// Shamans fight as hybrids: nuke and bolt when the 20-second recasts are
    /// up, keep their damage over time and disease on the target, and melee in
    /// between. Their root (Creepers, a 99% speed decrease) breaks on any
    /// damage, so on the monster being killed it only wasted a cast; in PvE it
    /// is reserved for an add attacking a grouped party, like Bard add mezzes.
    /// </summary>
    public static class ShamanBotCombatPolicy
    {
        public const int RootApproachDistance = 300;

        public static bool IsHybrid(eCharacterClass characterClass) => characterClass == eCharacterClass.Shaman;

        public static bool IsRoot(Spell spell) =>
            spell != null && spell.SpellType == eSpellType.SpeedDecrease && spell.Value >= 99;

        /// <summary>
        /// Every healing class (Shaman, Druid, Healer, Cleric, Friar, Warden, Paladin) keeps its
        /// roots for adds, like the Shaman always did: solo healers rooted the monster they were
        /// killing instead of casting damage, and the next hit broke the root anyway.
        /// </summary>
        public static bool KeepsRootsForAdds(eCharacterClass characterClass) =>
            IsHybrid(characterClass) || BotPartyRoles.IsHealingClass(characterClass);

        /// <summary>Roots never enter a healing class's ordinary PvE attack rotation.</summary>
        public static bool AllowsOrdinaryOffense(eCharacterClass characterClass, Spell spell) =>
            !KeepsRootsForAdds(characterClass) || !IsRoot(spell);

        /// <summary>Nukes and bolts first, then the damage over time, then the disease.</summary>
        public static int RotationPriority(Spell spell) => spell?.SpellType switch
        {
            eSpellType.Bolt or eSpellType.DirectDamage => 0,
            eSpellType.DamageOverTime => 1,
            _ => 2,
        };

        /// <summary>
        /// A ready spell waits out the Shaman's own weapon swing (melee stops
        /// and the cast goes off next), but a monster hitting the Shaman keeps
        /// it in melee: that cast would be interrupted for a player too.
        /// </summary>
        public static bool HoldMeleeForCast(bool hasReadyCast, bool interruptedByOther, bool selfInterrupted) =>
            hasReadyCast && !interruptedByOther && selfInterrupted;

        /// <summary>
        /// Same safe-add rules as Bard mezzes, and the add must still be on its
        /// way: rooting a monster already swinging at its victim stops nothing.
        /// </summary>
        public static bool IsRootableAdd(bool safeAdd, double distanceToVictim) =>
            safeAdd && distanceToVictim > RootApproachDistance;
    }
}
