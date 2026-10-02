using System;

namespace DOL.GS
{
    /// <summary>Per-viewer presentation state only; never moves or dismounts an actor.</summary>
    public sealed class StableRideObserver
    {
        public const int RefreshInterval = 5000;
        private readonly object _sync = new();
        private bool _busy;
        private bool _initialized;
        private long _lastAttach;

        public void Refresh(long now, bool force, Action createPair, Action attach)
        {
            bool initialize;
            lock (_sync)
            {
                // Creating the pair invokes the NPC-create hook again. Do not recurse.
                if (_busy || (_initialized && !force && now - _lastAttach < RefreshInterval))
                    return;
                _busy = true;
                initialize = !_initialized;
            }

            // Never hold this lock while acquiring a client's world-cache/packet locks.
            try
            {
                if (initialize)
                    createPair(); // horse, then equipped rider, then attachment
                attach();
                lock (_sync)
                {
                    _initialized = true;
                    _lastAttach = now;
                }
            }
            finally
            {
                lock (_sync)
                    _busy = false;
            }
        }
    }
}
