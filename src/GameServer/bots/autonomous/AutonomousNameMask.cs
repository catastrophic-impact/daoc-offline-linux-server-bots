using DOL.Language;

namespace DOL.GS
{
    /// <summary>
    /// Enemy-realm gamebots shown the period way (goal 12, owner 2026-10-07): like enemy players under the normal
    /// RvR rules, a player sees an autonomous gamebot of another realm by its race (for example "Saracen"), with
    /// its realm rank title where a guild would be, never its real name. Same realm, game masters and companion
    /// bots are unchanged.
    /// </summary>
    public static class AutonomousNameMask
    {
        public static bool Hides(GamePlayer viewer, GameObject target) =>
            target is GameBot bot && viewer != null &&
            ShouldMask(bot.IsAutonomousWorldBot, bot.Realm, viewer.Realm, (int)(viewer.Client?.Account?.PrivLevel ?? 1),
                GameServer.ServerRules != null && GameServer.ServerRules.IsSameRealm(viewer, bot, true));

        /// <summary>The rule on its own (unit tested): only an autonomous gamebot of another realm, seen by a player.</summary>
        public static bool ShouldMask(bool autonomousGamebot, eRealm botRealm, eRealm viewerRealm, int viewerPrivLevel, bool sameRealmByRules) =>
            autonomousGamebot && viewerPrivLevel <= 1 && botRealm != eRealm.None && viewerRealm != eRealm.None &&
            botRealm != viewerRealm && !sameRealmByRules;

        /// <summary>The name a viewer sees for this object (masked for enemy gamebots).</summary>
        public static string NameFor(GamePlayer viewer, GameObject target, int article, bool firstLetterUppercase) =>
            target == null ? string.Empty : Hides(viewer, target) ? RaceName(viewer, (GameBot)target) : target.GetName(article, firstLetterUppercase);

        /// <summary>As above, for an NPC name translated into the viewer's language when it is not masked.</summary>
        public static string NameFor(GamePlayer viewer, GameObject target, int article, bool firstLetterUppercase, string language) =>
            target == null ? string.Empty : Hides(viewer, target) ? RaceName(viewer, (GameBot)target) :
            target is GameNPC npc ? target.GetName(article, firstLetterUppercase, language, npc) : target.GetName(article, firstLetterUppercase);

        /// <summary>
        /// A message already written with the target's real name, as this viewer should read it: for an enemy gamebot
        /// the last place the name appears is swapped for its race. Used where one message goes to many players.
        /// </summary>
        public static string Masked(string message, GamePlayer viewer, GameObject target, int article, bool firstLetterUppercase)
        {
            if (string.IsNullOrEmpty(message) || !Hides(viewer, target))
                return message;
            string real = target.GetName(article, firstLetterUppercase);
            int at = string.IsNullOrEmpty(real) ? -1 : message.LastIndexOf(real, System.StringComparison.Ordinal);
            return at < 0 ? message : message.Substring(0, at) + RaceName(viewer, (GameBot)target) + message.Substring(at + real.Length);
        }

        public static string RaceName(GamePlayer viewer, GameBot bot) =>
            viewer.RaceToTranslatedName(bot.RaceId, bot.Gender);

        /// <summary>The realm rank title of the bot, in the viewer's language (shown in place of a guild).</summary>
        public static string RankTitle(GamePlayer viewer, GameBot bot)
        {
            int rank = bot.RealmLevel > 0 ? bot.RealmLevel / 10 + 1 : 0;
            string realm = bot.Realm == eRealm.Albion ? "Albion" : bot.Realm == eRealm.Midgard ? "Midgard" : "Hibernia";
            string gender = bot.Gender == eGender.Female ? "Female" : "Male";
            string language = viewer.Client?.Account?.Language ?? LanguageMgr.DefaultLanguage;
            return LanguageMgr.TryGetTranslation(out string title, language, $"GamePlayer.RealmTitle.{realm}.RR{rank}.{gender}")
                ? title : string.Empty;
        }
    }
}
