using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>Bounded event chat; producers never send packets while holding event locks.</summary>
    public static class RealmEventNotices
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<(string, eRealm), (string Text, long Expires)> Pending = new();
        private static readonly Dictionary<eRealm, long> Next = new();

        public static void Queue(string id, eRealm realm, string text)
        {
            if (realm == eRealm.None || string.IsNullOrWhiteSpace(text)) return;
            lock (Sync)
            {
                if (Pending.Count >= 32 && !Pending.ContainsKey((id, realm))) return;
                Pending[(id, realm)] = (text, GameLoop.GameLoopTime + 5 * 60_000);
            }
        }

        // Realm-wide one-line announcements at the top of the screen (no sound, not in the chat box),
        // for the moment a siege begins. Sent from Pulse, never while an event lock is held.
        private static readonly Dictionary<(string, eRealm), (string Text, AnnouncementKind Kind)> PendingScreen = new();

        public static void QueueScreen(string id, eRealm realm, string text, AnnouncementKind kind)
        {
            if (realm == eRealm.None || string.IsNullOrWhiteSpace(text) || !GameWideAnnouncements.Enabled(kind)) return;
            lock (Sync)
            {
                if (PendingScreen.Count < 32 || PendingScreen.ContainsKey((id, realm)))
                    PendingScreen[(id, realm)] = (text, kind);
            }
        }

        private static void FlushScreen()
        {
            KeyValuePair<(string, eRealm), (string Text, AnnouncementKind Kind)>[] screen;
            lock (Sync)
            {
                if (PendingScreen.Count == 0) return;
                screen = PendingScreen.ToArray();
                PendingScreen.Clear();
            }
            foreach (var notice in screen)
                if (GameWideAnnouncements.Enabled(notice.Value.Kind))
                    foreach (GamePlayer player in ClientService.Instance.GetPlayersOfRealm(notice.Key.Item2))
                    player.Out.SendMessage(notice.Value.Text, PacketHandler.eChatType.CT_ScreenCenter, PacketHandler.eChatLoc.CL_SystemWindow);
        }

        public static void Pulse(long now)
        {
            FlushScreen();
            GameWideAnnouncements.Flush();
            List<(string EventId, eRealm Realm, string Text)> outgoing = new();
            lock (Sync)
            {
                foreach (var entry in Pending.ToArray())
                {
                    if (entry.Value.Expires < now) { Pending.Remove(entry.Key); continue; }
                    eRealm realm = entry.Key.Item2;
                    if (Next.GetValueOrDefault(realm) > now) continue;
                    Next[realm] = now + 60_000;
                    Pending.Remove(entry.Key);
                    outgoing.Add((entry.Key.Item1, realm, entry.Value.Text));
                }
            }
            outgoing.RemoveAll(notice => !ClientService.Instance.GetPlayersOfRealm(notice.Realm).Any());
            if (outgoing.Count == 0) return;
            // Only performed when a rate-limited announcement is due, not on
            // every bot turn. Use a real committed speaker when one exists.
            var forces = AutonomousRvrEventLayer.ForceTargets();
            GameBot[] bots = AutonomousBotRegistry.Snapshot();
            foreach (var notice in outgoing)
            {
                GameBot speaker = null;
                GameBot fallback = null;
                int realmCandidates = 0;
                int candidates = 0;
                foreach (GameBot bot in bots)
                {
                    if (bot.Realm != notice.Realm || !bot.IsAlive || bot.IsTemporaryGroupHelper ||
                        bot.ObjectState != GameObject.eObjectState.Active) continue;
                    if (Random.Shared.Next(++realmCandidates) == 0) fallback = bot;
                    string force = bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}";
                    if (AutonomousRealmRaid.GetView(bot.Group)?.EventId != notice.EventId &&
                        (!forces.TryGetValue(force, out string target) || target != notice.EventId)) continue;
                    if (Random.Shared.Next(++candidates) == 0) speaker = bot;
                }
                AutonomousBotChatCoordinator.BroadcastFaction(speaker?.Name ?? fallback?.Name ?? "Realm herald", notice.Realm, notice.Text);
            }
        }
    }
}
