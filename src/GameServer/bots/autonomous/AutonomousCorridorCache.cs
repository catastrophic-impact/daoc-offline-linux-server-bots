using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DOL.GS
{
    /// <summary>Exact geometric answers only; never caches AI, actors, or inventory.
    /// Door/mesh changes invalidate answers through a zone revision.</summary>
    public static class AutonomousCorridorCache
    {
        public const int CapacityPerZone = 2048;
        public readonly record struct Key(Vector3 From, Vector3 To, long Revision);
        public sealed class ZoneCache
        {
            private readonly ConcurrentDictionary<Key, bool> _answers = new();
            private readonly ConcurrentQueue<Key> _order = new();
            private int _count;
            public int Count => Volatile.Read(ref _count);
            public bool TryGet(Key key, out bool answer) => _answers.TryGetValue(key, out answer);
            public void Store(Key key, bool answer)
            {
                if (!_answers.TryAdd(key, answer)) return;
                Interlocked.Increment(ref _count);
                _order.Enqueue(key);
                while (Volatile.Read(ref _count) > CapacityPerZone && _order.TryDequeue(out Key oldest))
                    if (_answers.TryRemove(oldest, out _)) Interlocked.Decrement(ref _count);
            }
        }
        private static readonly ConditionalWeakTable<Zone, ZoneCache> Zones = new();
        public static ZoneCache For(Zone zone) => Zones.GetOrCreateValue(zone);
    }
}
