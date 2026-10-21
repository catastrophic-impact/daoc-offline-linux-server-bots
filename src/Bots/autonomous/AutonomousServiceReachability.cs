using System.Collections.Concurrent;

namespace DOL.GS
{
    /// <summary>
    /// World services (merchants, the Realm Exchange) a bot could not find a way to. On 2026-10-17 bots that needed to
    /// sell picked Kel and Kuz, the Grimspound merchants on top of the hill in Dartmoor, which our mesh does not reach:
    /// each AI turn re-ran the failing approach search (~40 corridors, up to 2.7 s), walked toward the merchant anyway,
    /// gave up, and picked the same merchant again, for 20+ minutes.
    /// The bot that failed skips that service for <see cref="SkipMilliseconds"/>; once a second bot fails too, every
    /// bot skips it (one bot in a pocket must not close a good merchant for everyone).
    /// </summary>
    public static class AutonomousServiceReachability
    {
        public const long SkipMilliseconds = 30 * 60_000;

        private static readonly ConcurrentDictionary<(string Service, long Bot), long> BotSkips = new();
        private static readonly ConcurrentDictionary<string, (long Bot, long Until)> FirstFailures = new();
        private static readonly ConcurrentDictionary<string, long> SharedSkips = new();

        public static string Key(GameNPC npc) =>
            npc == null ? null : !string.IsNullOrEmpty(npc.InternalID) ? npc.InternalID : $"object:{npc.CurrentRegionID}:{npc.ObjectID}";

        /// <summary>Returns true when the service is now skipped by every bot.</summary>
        public static bool ReportFailure(string service, long bot, long now)
        {
            if (string.IsNullOrEmpty(service)) return false;
            if (BotSkips.Count > 8_192) BotSkips.Clear();
            BotSkips[(service, bot)] = now + SkipMilliseconds;
            if (FirstFailures.TryGetValue(service, out var first) && now < first.Until && first.Bot != bot)
            {
                SharedSkips[service] = now + SkipMilliseconds;
                FirstFailures.TryRemove(service, out _);
                return true;
            }
            FirstFailures[service] = (bot, now + SkipMilliseconds);
            return false;
        }

        public static bool IsSkipped(string service, long bot, long now) =>
            !string.IsNullOrEmpty(service) &&
            (SharedSkips.TryGetValue(service, out long shared) && now < shared ||
             BotSkips.TryGetValue((service, bot), out long own) && now < own);

        public const int FailuresBeforeSkip = 3;
        public const long GiveUpMilliseconds = 60_000;
        public const float ProgressDistance = 500f;

        /// <summary>
        /// One bot's failed approach searches for its current service. The search also fails while the bot is simply far
        /// away (the Camelot Realm Exchange from across the city, 2026-10-17), and walking there still works; a service is
        /// given up only after <see cref="FailuresBeforeSkip"/> failures over <see cref="GiveUpMilliseconds"/> in which the
        /// bot got no <see cref="ProgressDistance"/> closer.</summary>
        public sealed class ApproachFailures
        {
            private string _service;
            private int _count;
            private long _firstTick;
            private float _bestDistance;

            /// <summary>Records a failed search; true when the service should be given up.</summary>
            public bool Observe(string service, float distance, long now)
            {
                if (!string.Equals(service, _service, System.StringComparison.Ordinal) || distance < _bestDistance - ProgressDistance)
                {
                    _service = service;
                    _count = 0;
                    _firstTick = now;
                    _bestDistance = distance;
                }
                _count++;
                return _count >= FailuresBeforeSkip && now - _firstTick >= GiveUpMilliseconds;
            }

            public void Reset() => _service = null;
        }

        /// <summary>Tests only.</summary>
        public static void Clear()
        {
            BotSkips.Clear();
            FirstFailures.Clear();
            SharedSkips.Clear();
        }
    }
}
