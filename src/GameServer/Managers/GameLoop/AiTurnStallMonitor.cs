using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DOL.GS
{
    /// <summary>
    /// Bounded record of slow AI turns (the NPC flicker): counts every turn over the long-tick
    /// threshold and keeps only the five slowest of the minute. GameLoopWorkMetrics writes one
    /// SERVER_STALL summary line for a minute that had a stall, so a bad night adds kilobytes,
    /// not the 77,000 per-NPC warning lines a day the 25-99 ms turns used to write.
    /// </summary>
    public static class AiTurnStallMonitor
    {
        public const int TopTurns = 5;
        // Individual warning lines are kept for genuinely large turns only.
        public const long IndividualLineMilliseconds = 100;
        public const long BigTurnMilliseconds = 250;
        // A minute is reported when its slowest tick or GC pause reached this.
        public const double StallMilliseconds = 1000;

        public readonly record struct Turn(string Name, string Brain, long Milliseconds);

        private static readonly Lock Sync = new();
        private static readonly List<Turn> Slowest = new(TopTurns + 1);
        private static int _slowTurns;
        private static int _bigTurns;

        /// <summary>Called only for turns already over the long-tick threshold (rare), from any service thread.</summary>
        public static void Record(string name, string brain, long milliseconds)
        {
            Interlocked.Increment(ref _slowTurns);
            if (milliseconds >= BigTurnMilliseconds)
                Interlocked.Increment(ref _bigTurns);
            lock (Sync)
            {
                if (Slowest.Count == TopTurns && milliseconds <= Slowest[^1].Milliseconds)
                    return;
                Slowest.Add(new Turn(name, brain, milliseconds));
                Slowest.Sort((a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
                if (Slowest.Count > TopTurns)
                    Slowest.RemoveAt(Slowest.Count - 1);
            }
        }

        public static bool ShouldLogIndividually(long milliseconds) => milliseconds >= IndividualLineMilliseconds;

        /// <summary>Takes this minute's numbers and starts the next minute.</summary>
        public static (int Slow, int Big, Turn[] Top) TakeMinute()
        {
            lock (Sync)
            {
                Turn[] top = Slowest.ToArray();
                Slowest.Clear();
                return (Interlocked.Exchange(ref _slowTurns, 0), Interlocked.Exchange(ref _bigTurns, 0), top);
            }
        }

        public static bool IsStallMinute(double worstTickMs, double gcPauseMs) =>
            worstTickMs >= StallMilliseconds || gcPauseMs >= StallMilliseconds;

        /// <summary>A whole-process GC pause stops every thread, so every service shows the same spike.</summary>
        public static string LikelyCause(double worstTickMs, double gcPauseMs, Turn[] top) =>
            gcPauseMs >= worstTickMs * 0.6 ? "garbage-collection pause (all threads stopped)" :
            top.Length > 0 && top[0].Milliseconds >= worstTickMs * 0.6 ? $"slow AI turn: {top[0].Name} ({top[0].Brain})" :
            "mixed: many medium AI turns in one tick";

        public static string Describe(Turn[] top) =>
            top.Length == 0 ? "none" : string.Join("; ", top.Select(t => $"{t.Name} {t.Brain} {t.Milliseconds}ms"));
    }
}
