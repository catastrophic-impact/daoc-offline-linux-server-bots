using System;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>
    /// The four classic battlegrounds and their level brackets (patch 1.60 values; the Battleground table
    /// wins where it has a row). One owner for teleporters, bot goals and camp access, so a player and a
    /// bot see exactly the same rule. Level is the only limit (owner, 2026-10-07): the period realm point
    /// caps are deliberately not applied to players or bots.
    /// </summary>
    public static class BattlegroundBrackets
    {
        public sealed record Bracket(string Name, string CentralKeep, ushort RegionId, byte MinLevel, byte MaxLevel);

        private static readonly Bracket[] Period =
        {
            new("Abermenai", "Dun Abermenai", 253, 15, 19),
            new("Thidranki", "Thidranki Faste", 252, 20, 24),
            new("Murdaigean", "Dun Murdaigean", 251, 25, 29),
            new("Caledonia", "Caer Caledon", 250, 30, 35),
        };

        public static int Count => Period.Length;

        public static int LowestLevel => Period[0].MinLevel;
        public static int HighestLevel => Period[^1].MaxLevel;

        public static bool IsBattlegroundRegion(ushort regionId) => regionId is >= 250 and <= 253;

        private static Bracket[] _live;

        /// <summary>The live bracket for one of the four regions; the database row overrides the period default.</summary>
        public static Bracket Get(int index) => (_live ??= Load())[index];

        // Read once: the Battleground table is loaded at startup and never changes while running. Without a
        // running server (unit tests) the period values apply.
        private static Bracket[] Load()
        {
            var live = new Bracket[Period.Length];
            for (int index = 0; index < Period.Length; index++)
            {
                DbBattleground row = null;
                try { row = GameServer.Instance == null ? null : GameServer.KeepManager?.GetBattleground(Period[index].RegionId); }
                catch (Exception) { row = null; }
                live[index] = row == null ? Period[index] : Period[index] with { MinLevel = row.MinLevel, MaxLevel = row.MaxLevel };
            }
            return live;
        }

        public static Bracket ForRegion(ushort regionId)
        {
            for (int index = 0; index < Period.Length; index++)
                if (Period[index].RegionId == regionId) return Get(index);
            return null;
        }

        public static bool Allows(Bracket bracket, int level) =>
            bracket != null && level >= bracket.MinLevel && level <= bracket.MaxLevel;

        /// <summary>The single battleground a character of this level may enter, or null.</summary>
        public static Bracket ForLevel(int level)
        {
            for (int index = 0; index < Period.Length; index++)
            {
                Bracket bracket = Get(index);
                if (Allows(bracket, level)) return bracket;
            }
            return null;
        }

        public static bool LevelHasBracket(int level) => ForLevel(level) != null;

        public static bool CanEnter(GameLiving living, ushort regionId)
        {
            Bracket bracket = ForRegion(regionId);
            return bracket == null || living != null && Allows(bracket, living.Level);
        }

        /// <summary>Each realm's portal-keep arrival point; identical in all four battleground regions.</summary>
        public static GameLocation Entry(Bracket bracket, eRealm realm) => realm switch
        {
            eRealm.Albion => new GameLocation(bracket.Name + " Alb", bracket.RegionId, 38113, 53507, 4160, 3268),
            eRealm.Midgard => new GameLocation(bracket.Name + " Mid", bracket.RegionId, 53568, 23643, 4530, 3268),
            eRealm.Hibernia => new GameLocation(bracket.Name + " Hib", bracket.RegionId, 17367, 18248, 4320, 3268),
            _ => null,
        };

        /// <summary>
        /// Where a bot lands: open ground 800 outside its portal keep's gate (identical in all four battlegrounds).
        /// Run 19-20: the players' arrival point is up inside the portal keep, behind its keep door (players click
        /// through it); bots could not path out (NoPathFound), were sent home as stuck and teleported straight back,
        /// so no bot ever reached a battleground's central keep. These spots are on the field mesh that reaches it.
        /// </summary>
        public static GameLocation BotArrival(Bracket bracket, eRealm realm) => realm switch
        {
            eRealm.Albion => new GameLocation(bracket.Name + " Alb field", bracket.RegionId, 36428, 51260, 3952, 1883),
            eRealm.Midgard => new GameLocation(bracket.Name + " Mid field", bracket.RegionId, 53150, 26093, 4263, 668),
            eRealm.Hibernia => new GameLocation(bracket.Name + " Hib field", bracket.RegionId, 19714, 19085, 4094, 3699),
            _ => null,
        };

        public static string Describe(Bracket bracket) =>
            $"{bracket.Name} (levels {bracket.MinLevel}-{bracket.MaxLevel})";
    }
}
