using System;
using System.Collections.Concurrent;
using System.Threading;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.Quests.Hibernia
{
    /// <summary>
    /// The one red map dot for a player's active Sluaghbinder epic quest (Muirenn's chain):
    /// the target while hunting, Muirenn once it is slain. Same relic-marker packet as
    /// bounties and reputation quests, in its own reserved ID, so all three can show at once.
    /// </summary>
    public static class SluaghbinderQuestMapMarkers
    {
        public const uint MarkerId = 0xFFFE0003;
        private const int RefreshMilliseconds = 12000;
        private static readonly Logger Log = LoggerManager.Create(typeof(SluaghbinderQuestMapMarkers));
        private static readonly ConcurrentDictionary<GamePlayer, Target> Targets = new();
        private static Timer _timer;

        public readonly record struct Target(ushort Region, int X, int Y, int Z);

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            _timer?.Dispose();
            _timer = new Timer(Refresh, null, RefreshMilliseconds, RefreshMilliseconds);
        }

        [GameServerStoppedEvent]
        public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
        {
            _timer?.Dispose();
            _timer = null;
            Targets.Clear();
        }

        public static void Set(GamePlayer player, ushort region, int x, int y, int z)
        {
            if (player == null)
                return;

            Target target = new(region, x, y, z);
            if (Targets.TryGetValue(player, out Target old) && old != target && IsActive(player))
                player.Out.SendMinotaurRelicMapRemove(MarkerId);

            Targets[player] = target;
            Send(player, target);
        }

        public static void Clear(GamePlayer player)
        {
            if (player != null && Targets.TryRemove(player, out _) && IsActive(player))
                player.Out.SendMinotaurRelicMapRemove(MarkerId);
        }

        private static bool IsActive(GamePlayer player) =>
            player.ObjectState == GameObject.eObjectState.Active;

        private static void Send(GamePlayer player, Target target)
        {
            if (IsActive(player))
                player.Out.SendMinotaurRelicMapUpdate(MarkerId, target.Region, target.X, target.Y, target.Z);
        }

        private static void Refresh(object state)
        {
            try
            {
                foreach (var entry in Targets)
                {
                    if (!IsActive(entry.Key))
                        Targets.TryRemove(entry.Key, out _);
                    else if (Targets.TryGetValue(entry.Key, out Target current) && current == entry.Value)
                        Send(entry.Key, current);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Unable to refresh Sluaghbinder quest map marker.", ex);
            }
        }
    }
}
