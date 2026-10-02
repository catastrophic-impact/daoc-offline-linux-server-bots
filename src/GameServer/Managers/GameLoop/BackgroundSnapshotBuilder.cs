using System;
using System.Threading;

namespace DOL.GS
{
    /// <summary>
    /// Bounded, single-flight worker for immutable planning snapshots. It has no
    /// game-loop synchronization context. Callers capture values on the owning
    /// service and publish completed results there, never mutate actors here.
    /// At most one input OR one completed result is retained, with no backlog.
    /// </summary>
    public sealed class BackgroundSnapshotBuilder<TInput, TResult> : IDisposable
    {
        private readonly object _gate = new();
        private readonly AutoResetEvent _ready = new(false);
        private readonly Func<TInput, TResult> _build;
        private readonly Thread _worker;
        private bool _stopping;
        private bool _occupied;
        private bool _completed;
        private TInput _input;
        private TResult _result;
        private Exception _error;

        public BackgroundSnapshotBuilder(string name, Func<TInput, TResult> build)
        {
            _build = build ?? throw new ArgumentNullException(nameof(build));
            _worker = new Thread(Run) { Name = name, IsBackground = true };
            // Never inherit an actor/service's AsyncLocal state or continuations.
            if (ExecutionContext.IsFlowSuppressed()) _worker.Start();
            else
                using (ExecutionContext.SuppressFlow()) _worker.Start();
        }

        public bool TryRequest(TInput input)
        {
            lock (_gate)
            {
                if (_stopping || _occupied) return false;
                _input = input;
                _occupied = true;
                _ready.Set();
                return true;
            }
        }

        public bool TryTake(out TResult result, out Exception error)
        {
            lock (_gate)
            {
                result = default;
                error = null;
                if (!_completed || _stopping) return false;
                result = _result;
                error = _error;
                _result = default;
                _error = null;
                _completed = _occupied = false;
                return true;
            }
        }

        private void Run()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            while (true)
            {
                _ready.WaitOne();
                TInput input;
                lock (_gate)
                {
                    if (_stopping) return;
                    input = _input;
                    _input = default;
                }
                TResult result = default;
                Exception error = null;
                try { result = _build(input); }
                catch (Exception exception) { error = exception; }
                lock (_gate)
                {
                    if (_stopping) return;
                    _result = result;
                    _error = error;
                    _completed = true;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopping) return;
                _stopping = true;
                _ready.Set();
            }
            // Drain before world teardown: no abandoned work or stale result
            // can outlive the server session. Worker never waits on game loop.
            _worker.Join();
            lock (_gate)
            {
                _input = default;
                _result = default;
                _error = null;
            }
            _ready.Dispose();
        }
    }
}
