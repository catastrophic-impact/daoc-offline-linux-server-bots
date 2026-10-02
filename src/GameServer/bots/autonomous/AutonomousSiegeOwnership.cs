using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DOL.GS
{
    /// <summary>Bot-only secondary index of native ram ownership. No world scan
    /// on ordinary PvE replans. Weak owner keys cannot retain logged-out bots.</summary>
    public static class AutonomousSiegeOwnership
    {
        internal static readonly object ChangeGate = new();
        private static readonly ConditionalWeakTable<GameBot, ConcurrentDictionary<GameSiegeRam, byte>> Rams = new();
        private static readonly ConditionalWeakTable<GameBot, ConcurrentDictionary<GameSiegeWeapon, byte>> Weapons = new();

        internal static void WeaponChanged(GameSiegeWeapon weapon, GameLiving previous, GameLiving next)
        {
            if (previous is GameBot oldOwner && Weapons.TryGetValue(oldOwner, out var oldWeapons))
                oldWeapons.TryRemove(weapon, out _);
            if (next is GameBot newOwner) Weapons.GetOrCreateValue(newOwner)[weapon] = 0;
        }

        public static GameSiegeWeapon[] All(GameBot owner) => owner != null && Weapons.TryGetValue(owner, out var weapons)
            ? weapons.Keys.Where(w => w.Owner == owner && w.IsAlive && w.ObjectState == GameObject.eObjectState.Active).ToArray()
            : Array.Empty<GameSiegeWeapon>();

        internal static void Changed(GameSiegeRam ram, GameLiving previous, GameLiving next)
        {
            if (previous is GameBot oldOwner && Rams.TryGetValue(oldOwner, out var oldRams))
                oldRams.TryRemove(ram, out _);
            if (next is GameBot newOwner)
                Rams.GetOrCreateValue(newOwner)[ram] = 0;
        }

        internal static void WorldPresenceChanged(GameSiegeRam ram, bool active)
        {
            lock (ChangeGate)
                Changed(ram, active ? null : ram.Owner, active ? ram.Owner : null);
        }

        public static GameSiegeRam[] Snapshot(GameBot owner)
        {
            if (owner == null || !Rams.TryGetValue(owner, out var owned)) return Array.Empty<GameSiegeRam>();
            // Ownership is native and may change before the next NPC turn.
            // Revalidate it instead of trusting an index entry as authority.
            return owned.Keys.Where(ram => ram.Owner == owner &&
                ram.ObjectState == GameObject.eObjectState.Active).ToArray();
        }
    }
}
