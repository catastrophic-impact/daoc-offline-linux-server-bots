using System;
using System.Collections.Generic;

namespace DOL.GS
{
    /// <summary>
    /// Raid-local reservations. Not a global healer lock: separate casualties can
    /// have separate casters. The raid owner must validate membership, range,
    /// interruption, mana and immediate healing needs before requesting a lease.
    /// This is deliberately not connected to ordinary/autonomous groups.
    /// </summary>
    public sealed class CompanionRaidResurrectionReservations<T> where T : class
    {
        private sealed record Reservation(T Caster, T Corpse, long Expires, bool UsesHealer);
        private readonly object _sync = new();
        private readonly List<Reservation> _reservations = new(40);

        public bool TryReserve(T caster, T corpse, long now, int castMilliseconds,
            bool eligible, bool battleOngoing, int availableHealers, bool urgentHealingNeeded, bool usesHealer = true)
        {
            if (caster == null || corpse == null || ReferenceEquals(caster, corpse))
                return false;

            lock (_sync)
            {
                _reservations.RemoveAll(entry => entry.Expires <= now);
                if (!eligible || urgentHealingNeeded)
                {
                    _reservations.RemoveAll(entry => ReferenceEquals(entry.Caster, caster));
                    return false;
                }

                foreach (Reservation entry in _reservations)
                {
                    if (ReferenceEquals(entry.Caster, caster))
                        return ReferenceEquals(entry.Corpse, corpse);
                    if (ReferenceEquals(entry.Corpse, corpse))
                        return false;
                }

                // Keep one healer on the survivors during combat. Out of combat,
                // the last remaining resurrector may revive the rest of the raid.
                int limit = battleOngoing ? Math.Max(0, availableHealers - 1) : availableHealers;
                int committedHealers = 0;
                foreach (Reservation entry in _reservations)
                    if (entry.UsesHealer) committedHealers++;
                if (_reservations.Count >= 40 || usesHealer && committedHealers >= limit)
                    return false;

                long lifetime = Math.Clamp((long)castMilliseconds + 2000, 2000, 60000);
                _reservations.Add(new Reservation(caster, corpse, now + lifetime, usesHealer));
                return true;
            }
        }

        public void ReleaseCaster(T caster)
        {
            lock (_sync)
                _reservations.RemoveAll(entry => ReferenceEquals(entry.Caster, caster));
        }

        public void ReleaseCorpse(T corpse)
        {
            lock (_sync)
                _reservations.RemoveAll(entry => ReferenceEquals(entry.Corpse, corpse));
        }

        public void Clear()
        {
            lock (_sync)
                _reservations.Clear();
        }
    }
}
