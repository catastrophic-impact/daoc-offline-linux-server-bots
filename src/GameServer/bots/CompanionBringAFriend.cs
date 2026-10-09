using DOL.GS.ServerProperties;

namespace DOL.GS
{
    /// <summary>
    /// Bring A Friend for companion bots (the player's /spawn and /raid helpers). A companion
    /// counts as one more member of the player's group, the way another player would, so a solo
    /// player with three companions pulls like a group of four and a /raid 40 or /raid 80 scales
    /// up with its size (only friends that really exist within the BAF radius can join). A
    /// companion, or its pet, pulling a monster triggers BAF for its owner. Gamebots (persistent
    /// autonomous bots) never count and never trigger it.
    /// </summary>
    public static class CompanionBringAFriend
    {
        [ServerProperty("pve", "baf_companion_bots_count",
            "Do a player's companion bots (/spawn and /raid helpers) trigger Bring A Friend and count toward it like extra players in the group? Gamebots never do.", true)]
        public static bool Enabled = true;

        /// <summary>Pure rule: a companion is a temporary helper bot owned by a player, never a gamebot.</summary>
        public static bool IsCompanion(bool isBot, bool temporaryHelper, bool autonomous, bool hasPlayerOwner) =>
            isBot && temporaryHelper && !autonomous && hasPlayerOwner;

        public static bool IsCompanion(GameLiving living) =>
            living is GameBot bot && IsCompanion(true, bot.IsTemporaryGroupHelper, bot.IsAutonomousWorldBot, bot.Owner != null);

        /// <summary>Members that make a mob bring more friends: players, and companions when enabled.</summary>
        public static bool Counts(GameLiving member) =>
            member is GamePlayer || Enabled && IsCompanion(member);

        /// <summary>
        /// Total BAF chance in percent: each 100 is a guaranteed add, the rest a chance of one more.
        /// </summary>
        public static int ChancePercent(int countedAttackers, int initialChance, int additionalChance) =>
            initialChance + (System.Math.Max(1, countedAttackers) - 1) * additionalChance;
    }
}
