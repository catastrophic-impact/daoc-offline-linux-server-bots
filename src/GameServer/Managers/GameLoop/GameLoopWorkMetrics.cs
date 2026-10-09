using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>Game-loop-owner-only, bounded aggregate measurements. No actor
    /// references, per-bot allocations, background sampler or per-tick log IO.</summary>
    public static class GameLoopWorkMetrics
    {
        private sealed class Counters
        {
            public long Count;
            public double TotalMs;
            public double MaxMs;
            public double WaitMs;
            public long OverBudget;
        }

        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Dictionary<string, Counters> Stages = new();
        private static long _reportAt;
        private static double _stageWaitMs;
        private static TimeSpan _lastGcPause = GC.GetTotalPauseDuration();
        private static int _lastGen2 = GC.CollectionCount(2);

        public static long BeginStage()
        {
            _stageWaitMs = 0;
            return Stopwatch.GetTimestamp();
        }

        public static void RecordBarrierWait(long started) =>
            _stageWaitMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        public static void EndStage(string stage, long started)
        {
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!Stages.TryGetValue(stage, out Counters counters))
                Stages[stage] = counters = new();
            counters.Count++;
            counters.TotalMs += ms;
            counters.MaxMs = Math.Max(counters.MaxMs, ms);
            counters.WaitMs += _stageWaitMs;
            if (ms > GameLoop.TickDuration) counters.OverBudget++;

            long now = Stopwatch.GetTimestamp();
            if (_reportAt == 0) _reportAt = now + 60 * Stopwatch.Frequency;
            if (now < _reportAt) return;
            _reportAt = now + 60 * Stopwatch.Frequency;
            if (Stages.TryGetValue("NpcService", out Counters npc) && npc.Count > 0)
                PublishBotAiDelay(new BotAiDelayReport(DateTime.UtcNow, npc.TotalMs / npc.Count, npc.MaxMs, npc.Count));
            ReportStallMinute();
            foreach (var pair in Stages)
            {
                Counters value = pair.Value;
                if (value.Count == 0) continue;
                Log.Info(FormattableString.Invariant($"SERVER_WORK stage={pair.Key} samples={value.Count} avgMs={value.TotalMs / value.Count:F3} maxMs={value.MaxMs:F3} barrierAvgMs={value.WaitMs / value.Count:F3} overBudget={value.OverBudget}"));
                value.Count = value.OverBudget = 0;
                value.TotalMs = value.MaxMs = value.WaitMs = 0;
            }
        }

        /// <summary>
        /// One line for a minute with a stall (a tick or GC pause of a second or more):
        /// slowest stage, GC pause and full collections, heap size, and the five slowest
        /// AI turns. Quiet minutes write nothing.
        /// </summary>
        private static void ReportStallMinute()
        {
            var (slowTurns, bigTurns, top) = AiTurnStallMonitor.TakeMinute();
            TimeSpan gcPause = GC.GetTotalPauseDuration();
            int gen2 = GC.CollectionCount(2);
            double gcPauseMs = (gcPause - _lastGcPause).TotalMilliseconds;
            int gen2Delta = gen2 - _lastGen2;
            _lastGcPause = gcPause;
            _lastGen2 = gen2;
            var worst = Stages.Where(pair => pair.Value.Count > 0).OrderByDescending(pair => pair.Value.MaxMs).FirstOrDefault();
            double worstMs = worst.Value?.MaxMs ?? 0;
            if (!AiTurnStallMonitor.IsStallMinute(worstMs, gcPauseMs))
                return;
            string likely = AiTurnStallMonitor.LikelyCause(worstMs, gcPauseMs, top);
            string slowest = AiTurnStallMonitor.Describe(top);
            Log.Warn(FormattableString.Invariant($"SERVER_STALL worstTickMs={worstMs:F0} stage={worst.Key ?? "none"} gcPauseMs={gcPauseMs:F0} gen2={gen2Delta} heapMB={GC.GetTotalMemory(false) / 1048576} slowAiTurns={slowTurns} bigAiTurns={bigTurns} likely=\"{likely}\" slowest=\"{slowest}\""));
        }

        // For the launcher's BOT AI DELAY card. Written off the game loop so a slow disk
        // can never hold a tick.
        private static void PublishBotAiDelay(BotAiDelayReport report) =>
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { BotAiDelayReport.Write(System.IO.Path.Combine(AppContext.BaseDirectory, BotAiDelayReport.FileName), report); }
                catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
                { Log.Warn("Bot AI delay report could not be written", exception); }
            });
    }
}
