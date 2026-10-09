using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>
    /// Learns which camps kill solo bots without paying for it and benches them
    /// for a while. A death on the way to a camp counts against that camp, so a
    /// camp whose route crosses aggressive monsters (Lough Derg luricaduane) or
    /// whose spawns sit beside them (skeletal pawn, botonid seedling) drops out
    /// of solo planning once several different bots have died there.
    ///
    /// Cost: one counter bump per kill and one small check per solo death. Bots
    /// never scan their routes, so this adds no per-tick work at any population.
    /// Nothing in the world changes; a benched camp returns when the bench ends.
    /// </summary>
    public static class AutonomousCampDangerBench
    {
        public const int DistinctBotsToBench = 3;
        // A healthy solo camp gives about 50 kills per death; the deadly ones in
        // the 2026-10-01 run gave 0-8.
        public const int MinimumKillsPerDeath = 15;
        public static readonly TimeSpan Window = TimeSpan.FromHours(3);
        public static readonly TimeSpan FirstBench = TimeSpan.FromHours(3);
        public static readonly TimeSpan LongestBench = TimeSpan.FromHours(24);

        private const int BucketCount = 18; // 10-minute kill buckets across the window
        private static readonly long BucketTicks = Window.Ticks / BucketCount;

        private sealed class Record
        {
            public readonly object Gate = new();
            public readonly List<(DateTime At, long BotId)> Deaths = new();
            public readonly long[] BucketIds = new long[BucketCount];
            public readonly int[] BucketKills = new int[BucketCount];
            public DateTime BenchedUntilUtc;
            public int TimesBenched;
        }

        private static readonly ConcurrentDictionary<string, Record> Records = new(StringComparer.OrdinalIgnoreCase);

        public static void RecordKill(string campId, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(campId))
                return;
            DateTime now = nowUtc ?? DateTime.UtcNow;
            Record record = Records.GetOrAdd(campId, _ => new Record());
            long bucket = now.Ticks / BucketTicks;
            int slot = (int)(bucket % BucketCount);
            lock (record.Gate)
            {
                if (record.BucketIds[slot] != bucket)
                {
                    record.BucketIds[slot] = bucket;
                    record.BucketKills[slot] = 0;
                }
                record.BucketKills[slot]++;
            }
        }

        /// <summary>Returns the bench length when this death benched the camp.</summary>
        public static TimeSpan? RecordDeath(string campId, long botId, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(campId) || botId <= 0)
                return null;
            DateTime now = nowUtc ?? DateTime.UtcNow;
            Record record = Records.GetOrAdd(campId, _ => new Record());
            lock (record.Gate)
            {
                if (record.BenchedUntilUtc > now)
                    return null;
                record.Deaths.RemoveAll(death => now - death.At > Window);
                record.Deaths.Add((now, botId));
                if (record.Deaths.Select(death => death.BotId).Distinct().Count() < DistinctBotsToBench)
                    return null;
                long oldestBucket = (now - Window).Ticks / BucketTicks;
                int kills = 0;
                for (int i = 0; i < BucketCount; i++)
                    if (record.BucketIds[i] > oldestBucket)
                        kills += record.BucketKills[i];
                if (kills >= record.Deaths.Count * MinimumKillsPerDeath)
                    return null;

                TimeSpan bench = TimeSpan.FromTicks(Math.Min(LongestBench.Ticks,
                    FirstBench.Ticks << Math.Min(record.TimesBenched, 3)));
                record.TimesBenched++;
                record.BenchedUntilUtc = now + bench;
                record.Deaths.Clear();
                return bench;
            }
        }

        public static bool IsBenched(string campId, DateTime? nowUtc = null) =>
            !string.IsNullOrWhiteSpace(campId) && Records.TryGetValue(campId, out Record record) &&
            record.BenchedUntilUtc > (nowUtc ?? DateTime.UtcNow);

        public static void ResetForTests() => Records.Clear();
    }
}
