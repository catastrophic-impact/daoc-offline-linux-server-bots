using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DOL.GS
{
    /// <summary>
    /// Per-thread counts and time of navmesh queries and route-planning steps within one AI turn,
    /// appended to the "Long NpcService" warning so a slow turn names what it spent its time on.
    /// Reset before each brain turn; costs two timestamps per counted call.
    /// </summary>
    public static class NavQueryProfile
    {
        public enum Kind { PathStraight, ClosestPoint, OtherQuery, ZoneStep, Corridor, Road, Count }

        [ThreadStatic] private static int[] _calls;
        [ThreadStatic] private static long[] _ticks;
        [ThreadStatic] private static List<(string Site, long Ticks)> _sites;

        public static void Reset()
        {
            _sites?.Clear();
            if (_calls == null) { _calls = new int[(int)Kind.Count]; _ticks = new long[(int)Kind.Count]; return; }
            Array.Clear(_calls);
            Array.Clear(_ticks);
        }

        /// <summary>Slow zone-step planning is credited to the code that asked for it ("zoneStepBy=" in the
        /// summary): 13 callers plan zone steps and the slow turns only said "ZoneStep" (2026-10-16).</summary>
        public const long SiteThresholdMilliseconds = 20;

        public static void NoteSite(string file, string member, long started)
        {
            if (_calls == null) return;
            long ticks = Stopwatch.GetTimestamp() - started;
            if (ticks * 1000 < SiteThresholdMilliseconds * Stopwatch.Frequency) return;
            string site = System.IO.Path.GetFileNameWithoutExtension(file) + "." + member;
            _sites ??= new List<(string, long)>();
            for (int i = 0; i < _sites.Count; i++)
                if (_sites[i].Site == site) { _sites[i] = (site, _sites[i].Ticks + ticks); return; }
            _sites.Add((site, ticks));
        }

        public static long Start() => Stopwatch.GetTimestamp();

        public static void Stop(Kind kind, long started)
        {
            if (_calls == null) return;
            _calls[(int)kind]++;
            _ticks[(int)kind] += Stopwatch.GetTimestamp() - started;
        }

        public static string Summary()
        {
            if (_calls == null) return string.Empty;
            var text = new StringBuilder();
            for (int i = 0; i < (int)Kind.Count; i++)
            {
                if (_calls[i] == 0) continue;
                text.Append(' ').Append((Kind)i).Append('=').Append(_calls[i]).Append('/')
                    .Append((_ticks[i] * 1000 / Stopwatch.Frequency).ToString()).Append("ms");
            }
            if (_sites is { Count: > 0 })
            {
                _sites.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
                text.Append(" zoneStepBy=");
                for (int i = 0; i < Math.Min(3, _sites.Count); i++)
                    text.Append(i > 0 ? "," : "").Append(_sites[i].Site).Append(':')
                        .Append((_sites[i].Ticks * 1000 / Stopwatch.Frequency).ToString()).Append("ms");
            }
            return text.Length == 0 ? string.Empty : " nav:" + text;
        }
    }
}
