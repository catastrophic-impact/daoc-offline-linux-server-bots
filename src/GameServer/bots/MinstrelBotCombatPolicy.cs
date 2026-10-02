namespace DOL.GS
{
    public static class MinstrelBotCombatPolicy
    {
        private static bool IsInstantDamage(Spell spell) => spell != null && spell.IsInstantCast &&
            !spell.NeedInstrument && BotCasterPriority.IsDamage(spell);

        // An instant shout must not cancel the melee approach or weapon swing.
        // Real casts, charm upkeep, songs and unknown queued requests still wait.
        public static bool ContinueAfterInstantDamage(bool hybridMinstrel, bool spellAction,
            bool isCasting, Spell activeSpell, bool hasPendingCast, Spell pendingSpell)
        {
            if (!hybridMinstrel || !spellAction)
                return false;
            if (isCasting && !IsInstantDamage(activeSpell))
                return false;
            if (hasPendingCast && !IsInstantDamage(pendingSpell))
                return false;
            return true;
        }
    }
}
