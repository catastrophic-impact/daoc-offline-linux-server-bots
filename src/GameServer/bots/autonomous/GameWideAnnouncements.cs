using System.Collections.Concurrent;
using DOL.GS.PacketHandler;

namespace DOL.GS
{
    public enum AnnouncementKind
    {
        /// <summary>Frontier and battleground keeps or relics taken, sieges started.</summary>
        RvrBattleground,
        /// <summary>Raids forming and raid final bosses defeated (epic dungeons, Summoner's Hall, Darkness Falls).</summary>
        PveRealmEvent,
    }

    /// <summary>
    /// Announcements every realm sees, in two groups the launcher's Options tab can turn off separately (owner
    /// 2026-10-07, both default on): RvR and battleground (keeps and relics taken, sieges started, battleground keep
    /// sieges started and taken) and PvE realm events (raids forming, a raid's final boss defeated in an epic dungeon,
    /// Summoner's Hall or Darkness Falls).
    /// </summary>
    public static class GameWideAnnouncements
    {
        [ServerProperties.ServerProperty("autonomous", "rvr_battleground_announcements",
            "RvR and battleground announcements: keeps or relics taken and sieges started, frontier and battlegrounds. Set in the launcher Options tab.", true)]
        public static bool RvrBattleground = true;

        [ServerProperties.ServerProperty("autonomous", "pve_realm_event_announcements",
            "PvE realm event announcements: raids forming and raid final bosses defeated in epic dungeons, Summoner's Hall and Darkness Falls. Set in the launcher Options tab.", true)]
        public static bool PveRealmEvents = true;

        public static bool Enabled(AnnouncementKind kind) => kind switch
        {
            AnnouncementKind.PveRealmEvent => PveRealmEvents,
            _ => RvrBattleground,
        };

        private static readonly ConcurrentQueue<(AnnouncementKind Kind, string Text)> Pending = new();

        /// <summary>Queued from any thread (event locks may be held); sent on the next notice pulse.</summary>
        public static void Queue(AnnouncementKind kind, string text)
        {
            if (!Enabled(kind) || string.IsNullOrWhiteSpace(text) || Pending.Count >= 32) return;
            Pending.Enqueue((kind, text));
        }

        public static void Flush()
        {
            while (Pending.TryDequeue(out var notice))
            {
                if (!Enabled(notice.Kind)) continue;
                foreach (GamePlayer player in ClientService.Instance.GetPlayers())
                {
                    player.Out.SendMessage(notice.Text, eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage(notice.Text, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }
            }
        }

        /// <summary>The line for a raid's final boss defeat ("Albion's forces have defeated Legion in Darkness Falls!").</summary>
        public static string FinalBossDefeated(eRealm realm, string boss, string place) =>
            $"{GlobalConstants.RealmToName(realm)}'s forces have defeated {boss} in {place}!";

        /// <summary>The line for a battleground keep siege starting ("Albion's forces have laid siege to Dun Abermenai in Abermenai!").</summary>
        public static string BattlegroundSiegeStarted(eRealm attacker, string keep, string battleground, eRealm holder) =>
            holder == eRealm.None
                ? $"{GlobalConstants.RealmToName(attacker)}'s forces are assaulting the unclaimed {keep} in {battleground}!"
                : $"{GlobalConstants.RealmToName(attacker)}'s forces have laid siege to {GlobalConstants.RealmToName(holder)}'s {keep} in {battleground}!";
    }
}
