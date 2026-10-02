namespace DOL.GS
{
    public static class BotCasterPriority
    {
        public static bool IsDamage(Spell spell) => spell != null &&
            (spell.Damage > 0 || spell.SpellType is eSpellType.DirectDamage or eSpellType.Lifedrain or
                eSpellType.Bolt or eSpellType.DirectDamageWithDebuff or eSpellType.DamageSpeedDecrease);

        public static bool AllowInstant(Spell spell, bool rangedCaster, bool meleePressure) =>
            !rangedCaster || IsDamage(spell) || meleePressure;
    }
}
