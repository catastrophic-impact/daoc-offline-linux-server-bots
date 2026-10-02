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

        public static void Pulse(long now)
        {
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
