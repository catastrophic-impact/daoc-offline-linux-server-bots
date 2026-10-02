namespace DOL.GS
{
    public enum BotPartyRole { Damage, Tank, Support }
    public enum BotPveGroupRole { Tank, Healer, Buffer, Attacker }

    /// <summary>Group jobs, not a test for whether a class happens to know a buff.</summary>
    public static class BotPartyRoles
    {
        public static BotPartyRole For(eCharacterClass characterClass) => characterClass switch
        {
            eCharacterClass.Cleric or
            eCharacterClass.Druid or
            eCharacterClass.Healer => BotPartyRole.Support,
            eCharacterClass.Paladin or
            eCharacterClass.Armsman or eCharacterClass.Reaver or
            eCharacterClass.Warrior or eCharacterClass.Thane or
            eCharacterClass.Hero or eCharacterClass.Champion or
            eCharacterClass.Sluaghbinder => BotPartyRole.Tank,
            _ => BotPartyRole.Damage
        };

        public static bool IsHealingClass(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Cleric or eCharacterClass.Friar or eCharacterClass.Druid or
            eCharacterClass.Warden or eCharacterClass.Healer or eCharacterClass.Shaman or
            eCharacterClass.Paladin;

        public static bool CanFill(eCharacterClass characterClass, BotPveGroupRole role) => role switch
        {
            BotPveGroupRole.Tank => For(characterClass) == BotPartyRole.Tank,
            BotPveGroupRole.Healer => IsHealingClass(characterClass),
            BotPveGroupRole.Buffer => For(characterClass) == BotPartyRole.Support || IsHybridSupport(characterClass),
            BotPveGroupRole.Attacker => For(characterClass) != BotPartyRole.Support || IsHybridSupport(characterClass),
            _ => false
        };

        public static string GroupRoleLabel(BotPveGroupRole role) => role switch
        {
            BotPveGroupRole.Tank => "Tank",
            BotPveGroupRole.Healer => "Healer",
            BotPveGroupRole.Buffer => "Buffer",
            _ => "Attacker"
        };

        public static bool IsHybridSupport(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Warden or eCharacterClass.Paladin or eCharacterClass.Shaman or eCharacterClass.Friar or
            eCharacterClass.Sorcerer or eCharacterClass.Mentalist or
            eCharacterClass.Bard or eCharacterClass.Minstrel or eCharacterClass.Skald;

        /// <summary>
        /// A support class is a non-combat role only when the party has an
        /// actual combat-capable partner.  A solo healer (or a party made up
        /// entirely of healers/supports) must still be allowed through the
        /// normal attack path so it can level instead of repeatedly trying
        /// defensive upkeep with no one to fight for it.
        /// </summary>
        public static bool IsSupport(GameBot bot) => bot?.Group?.MemberCount > 1 &&
            bot.CharacterClass != null && For((eCharacterClass)bot.CharacterClass.ID) == BotPartyRole.Support &&
            HasCombatPartner(bot);

        private static bool HasCombatPartner(GameBot bot)
        {
            Group group = bot?.Group;
            if (group == null)
                return false;

            foreach (GameLiving member in group.GetMembersInTheGroup())
            {
                if (member == null || member == bot || !member.IsAlive ||
                    member.ObjectState != GameObject.eObjectState.Active)
                    continue;

                // A real player is always a combat-capable party partner from
                // the bot's point of view, even when that player is currently
                // idle.  This preserves player-led healer/buffer behavior.
                if (member is GamePlayer)
                    return true;

                if (member is GameBot other && other.CharacterClass != null &&
                    For((eCharacterClass)other.CharacterClass.ID) != BotPartyRole.Support)
                    return true;
            }

            return false;
        }

        public static bool IsTank(GameBot bot) => bot?.CharacterClass != null &&
            For((eCharacterClass)bot.CharacterClass.ID) == BotPartyRole.Tank;

        public static string Label(eCharacterClass characterClass) => IsHybridSupport(characterClass)
            ? (For(characterClass) == BotPartyRole.Tank ? "Tank/Healer/Buffer"
                : IsHealingClass(characterClass) ? "Attacker/Healer/Buffer" : "Attacker/Buffer")
            : For(characterClass) switch
        {
            BotPartyRole.Tank => "Tank/Attacker",
            BotPartyRole.Support => IsHealingClass(characterClass) ? "Healer/Buffer" : "Song Support/Buffer",
            _ => "Damage/Attacker"
        };
    }
}
