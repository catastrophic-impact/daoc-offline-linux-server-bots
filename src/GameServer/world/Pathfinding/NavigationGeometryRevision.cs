using System.Runtime.CompilerServices;
using System.Threading;

namespace DOL.GS
{
    public static class NavigationGeometryRevision
    {
        private sealed class Stamp { public long Value; }
        private static readonly ConditionalWeakTable<Zone, Stamp> Stamps = new();
        public static long Read(Zone zone) => zone == null ? -1 : Interlocked.Read(ref Stamps.GetOrCreateValue(zone).Value);
        public static void Changed(Zone zone)
        {
            if (zone != null) Interlocked.Increment(ref Stamps.GetOrCreateValue(zone).Value);
        }
    }
}
