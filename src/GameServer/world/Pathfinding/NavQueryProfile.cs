using System;
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
        public enum Kind { PathStraight, ClosestPoint, OtherQuery, ZoneStep, Corridor, Count }

        [ThreadStatic] private static int[] _calls;
        [ThreadStatic] private static long[] _ticks;

        public static void Reset()
        {
            if (_calls == null) { _calls = new int[(int)Kind.Count]; _ticks = new long[(int)Kind.Count]; return; }
            Array.Clear(_calls);
            Array.Clear(_ticks);
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
            return text.Length == 0 ? string.Empty : " nav:" + text;
        }
    }
}
