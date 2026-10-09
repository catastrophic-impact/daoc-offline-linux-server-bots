using System;
using System.Text;
using DOL.Database;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// The [Battlegrounds] choice shared by the realm teleporters. Players only see and can take the
    /// battleground of their own level bracket (the same rule bots use); game masters may visit any of them.
    /// </summary>
    public static class BattlegroundTeleportOptions
    {
        public const string MenuLine = "\n\nThe [Battlegrounds] are open to fighters of levels 15 to 35.";

        /// <summary>
        /// True when <paramref name="text"/> was a battleground choice. <paramref name="destination"/> is set
        /// when the player may go; otherwise the teleporter has already explained why not.
        /// </summary>
        public static bool TryHandle(GameNPC teleporter, GamePlayer player, string text, eRealm realm, out DbTeleport destination)
        {
            destination = null;
            if (teleporter == null || player == null || string.IsNullOrWhiteSpace(text)) return false;
            bool gameMaster = player.Client?.Account?.PrivLevel > 1;

            if (text.Equals("battlegrounds", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("battleground", StringComparison.OrdinalIgnoreCase))
            {
                BattlegroundBrackets.Bracket own = BattlegroundBrackets.ForLevel(player.Level);
                var reply = new StringBuilder("The battlegrounds are small frontiers where young fighters of all three realms meet:\n");
                for (int index = 0; index < BattlegroundBrackets.Count; index++)
                {
                    BattlegroundBrackets.Bracket bracket = BattlegroundBrackets.Get(index);
                    bool open = gameMaster || bracket == own;
                    reply.Append('\n').Append(open ? $"[{bracket.Name}]" : bracket.Name)
                        .Append($" - levels {bracket.MinLevel} to {bracket.MaxLevel}");
                }
                reply.Append(own != null || gameMaster
                    ? "\n\nI can send you to the one that matches your strength."
                    : "\n\nNone of them matches your level right now.");
                teleporter.SayTo(player, reply.ToString());
                return true;
            }

            for (int index = 0; index < BattlegroundBrackets.Count; index++)
            {
                BattlegroundBrackets.Bracket bracket = BattlegroundBrackets.Get(index);
                if (!text.Equals(bracket.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!gameMaster && !BattlegroundBrackets.Allows(bracket, player.Level))
                {
                    teleporter.SayTo(player, $"{bracket.Name} is only for fighters of levels {bracket.MinLevel} to {bracket.MaxLevel}.");
                    return true;
                }
                GameLocation entry = BattlegroundBrackets.Entry(bracket, realm);
                if (entry == null) return true;
                destination = new DbTeleport
                {
                    TeleportID = bracket.Name,
                    Type = string.Empty,
                    Realm = (int)realm,
                    RegionID = entry.RegionID,
                    X = entry.X,
                    Y = entry.Y,
                    Z = entry.Z,
                    Heading = entry.Heading,
                };
                return true;
            }
            return false;
        }
    }
}
