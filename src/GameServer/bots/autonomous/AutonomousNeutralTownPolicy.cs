using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DOL.GS
{
    /// <summary>
    /// Shrouded Isles neutral towns (mantid Krrzck, lammia Cryptos Mythicos and
    /// iarn dwarf Remnants) are always open to gamebots. A town is the area
    /// around one of that faction's no-realm stable masters. Faction members
    /// spawned there never attack bots, and bots never pull them, so bots can
    /// ride and pass through without fighting the town. Players still need
    /// reputation exactly as before.
    /// </summary>
    public static class AutonomousNeutralTownPolicy
    {
        public const int TownRadius = 2500;

        public static readonly IReadOnlySet<int> TownFactionIds = new HashSet<int>
        {
            16,  // Cryptos Mythicos (Albion lammia)
            89,  // Krrzck (Hibernia mantids)
            172, // The Remnants (Midgard iarn dwarves)
        };

        public readonly record struct TownAnchor(ushort RegionId, int FactionId, int X, int Y);

        private const long EmptyRetryMilliseconds = 30_000;
        private static TownAnchor[] _anchors;
        private static long _nextEmptyRetryTick;
        private static readonly Lock BuildLock = new();

        public static bool IsTownResident(GameNPC npc)
        {
            Faction faction = npc?.Faction;
            if (faction == null || !TownFactionIds.Contains(faction.Id))
                return false;

            Point3D spawn = npc.SpawnPoint;
            return IsWithinTown(Anchors(), npc.CurrentRegionID, faction.Id, spawn.X, spawn.Y);
        }

        public static bool IsWithinTown(IReadOnlyList<TownAnchor> anchors, ushort regionId, int factionId, int x, int y)
        {
            if (anchors == null)
                return false;

            foreach (TownAnchor anchor in anchors)
            {
                if (anchor.RegionId != regionId || anchor.FactionId != factionId)
                    continue;

                long dx = anchor.X - x;
                long dy = anchor.Y - y;
                if (dx * dx + dy * dy <= (long)TownRadius * TownRadius)
                    return true;
            }

            return false;
        }

        private static TownAnchor[] Anchors()
        {
            TownAnchor[] anchors = Volatile.Read(ref _anchors);
            if (anchors is { Length: > 0 } || GameLoop.GameLoopTime < _nextEmptyRetryTick)
                return anchors;

            lock (BuildLock)
            {
                anchors = _anchors;
                if (anchors is { Length: > 0 })
                    return anchors;

                var found = new List<TownAnchor>();
                foreach (Region region in WorldMgr.GetAllRegions())
                {
                    if (region == null)
                        continue;

                    foreach (GameStableMaster master in region.Objects.OfType<GameStableMaster>())
                    {
                        if (master.Realm == eRealm.None && master.Faction != null &&
                            TownFactionIds.Contains(master.Faction.Id))
                        {
                            Point3D spawn = master.SpawnPoint;
                            found.Add(new(master.CurrentRegionID, master.Faction.Id, spawn.X, spawn.Y));
                        }
                    }
                }

                // Before the world has loaded there is nothing to find; retry
                // later rather than caching an empty town list forever.
                if (found.Count == 0)
                    _nextEmptyRetryTick = GameLoop.GameLoopTime + EmptyRetryMilliseconds;

                anchors = found.ToArray();
                Volatile.Write(ref _anchors, anchors);
                return anchors;
            }
        }
    }
}
