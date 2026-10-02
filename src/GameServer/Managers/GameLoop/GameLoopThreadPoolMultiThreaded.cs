using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DOL.Logging;

namespace DOL.GS
{
    // A very specialized thread pool, meant to be used by the game loop and its dedicated thread exclusively.
    public sealed class GameLoopThreadPoolMultiThreaded : GameLoopThreadPool
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Shapes an inverse power-law curve for work distribution.
        // Higher values -> smaller, more numerous chunks, better for uneven workloads.
        // Lower values -> larger chunks, better for uniform workloads.
        // 2.5 is a good balance found through empirical testing.
        private const double WORK_SPLIT_BIAS_FACTOR = 2.5;
        private const int MAX_DEGREE_OF_PARALLELISM = 128;

        // Thread pool configuration and state.
        private bool _running;
        private int _degreeOfParallelism;               // Total threads, including caller.
        private int _workerCount;                       // Number of dedicated worker threads (= _degreeOfParallelism - 1).
        private double[] _workSplitBiasTable;           // Lookup table for chunk size biasing.

        // Thread management.
        private Thread[] _workers;                      // Worker threads (excludes caller).
        private long[] _workerCycle;                    // Worker cycle phase, used to detect stuck threads.
        private GameLoopThreadPoolWatchdog _watchdog;   // Monitors worker health; restarts if needed.
        private CancellationTokenSource _shutdownToken;

        // Work coordination.
        private CountdownEvent _workerStartLatch;       // Signals when all workers are initialized.
        private ManualResetEventSlim[] _workReady;      // Per-worker event to trigger work.

        // Work processing.
        private WorkProcessor _workProcessor;           // Current work processor instance, reused for each execution.
        private readonly WorkState _workState = new();  // Shared state for work distribution and completion tracking.
        private readonly CountdownEvent _workCompleted = new(0);
        private ExecutionContext _workContext;          // Execution context captured from the thread entering ExecuteForEach.
        private Exception _workException;

        [StructLayout(LayoutKind.Explicit)]
        private class WorkState
        {
            [FieldOffset(0)]   public int RemainingWork;         // Total items left to process.
            [FieldOffset(128)] public int CompletedWorkerCount;  // Count of workers finished for current iteration.
        }

        public GameLoopThreadPoolMultiThreaded(int degreeOfParallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(degreeOfParallelism, MAX_DEGREE_OF_PARALLELISM);
            _degreeOfParallelism = degreeOfParallelism;
        }

        public override void Init()
        {
            if (Interlocked.CompareExchange(ref _running, true, false))
                return;

            Configure();
            BuildChunkDivisorTable();
            StartWorkers();
            StartWatchdog();

            void Configure()
            {
                _workerCount = _degreeOfParallelism - 1;
                _workers = new Thread[_workerCount];
                _workerCycle = new long[_workerCount];
                _workerStartLatch = new(_workerCount);
                _workReady = new ManualResetEventSlim[_workerCount];

                for (int i = 0; i < _workerCount; i++)
                    _workReady[i] = new(false);

                _shutdownToken = new();
                base.Init();
            }

            void BuildChunkDivisorTable()
            {
                _workSplitBiasTable = new double[_degreeOfParallelism + 1];

                for (int i = 1; i <= _degreeOfParallelism; i++)
                    _workSplitBiasTable[i] = Math.Pow(i, WORK_SPLIT_BIAS_FACTOR);

                _workSplitBiasTable[0] = 1; // Prevent division by zero, fallback.
            }

            void StartWorkers()
            {
                for (int i = 0; i < _workerCount; i++)
                {
                    Thread worker = new(new ParameterizedThreadStart(InitWorker))
                    {
                        Name = $"{GameLoop.THREAD_NAME}_Worker_{i}",
                        IsBackground = true
                    };
                    worker.Start((i, false));
                }

                _workerStartLatch.Wait(); // If for some reason a thread fails to start, we'll be waiting here forever.
            }

            void StartWatchdog()
            {
                _watchdog = new(_workers, _workerCycle, RestartWorkers);
                _watchdog.Start();
            }
        }

        public override void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            try
            {
                int count = Math.Min(items.Count, toExclusive);

                if (count <= 0)
                    return;

                // Most client stages on an offline server contain one human.
                // Do not wake eleven threads or capture execution context just
                // to process that one actor. The caller already owns context.
                if (count == 1)
                {
                    CheckResetTick();
                    action(items[0]);
                    return;
                }

                _workContext = ExecutionContext.Capture();
                _workProcessor = WorkProcessorCache<T>.Instance.Set(items, action);
                _workState.RemainingWork = count;
                _workState.CompletedWorkerCount = 0;
                _workException = null;

                // If the count is less than the degree of parallelism, only signal the required number of workers.
                // The caller thread will also be used, so in this case we need to subtract one from the amount of workers to start.
                int workersToStart = count < _degreeOfParallelism ? count - 1 : _workerCount;
                _workCompleted.Reset(workersToStart + 1);

                for (int i = 0; i < workersToStart; i++)
                    _workReady[i].Set();

                try { ProcessWorkActions(); }
                catch (Exception exception) { Interlocked.CompareExchange(ref _workException, exception, null); }
                finally
                {
                    Interlocked.Increment(ref _workState.CompletedWorkerCount);
                    _workCompleted.Signal();
                }

                // Brief adaptive spinning is built into CountdownEvent. Longer
                // waits park the caller, leaving CPU capacity for the workers,
                // network and database instead of burning a core at a barrier.
                long waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _workCompleted.Wait();
                GameLoopWorkMetrics.RecordBarrierWait(waitStarted);
                if (_workException != null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_workException).Throw();
            }
            catch (Exception e)
            {
                if (log.IsFatalEnabled)
                    log.Fatal($"Critical error encountered in \"{nameof(GameLoopThreadPoolMultiThreaded)}\"", e);

                // The owning game loop handles fatal shutdown only after all
                // workers have left this batch. A worker must never join the
                // game-loop thread while that thread is waiting for it.
                throw;
            }
            finally
            {
                (_workProcessor as WorkProcessor<T>)?.Clear();
            }
        }

        public override void Dispose()
        {
            if (!Interlocked.CompareExchange(ref _running, false, true))
                return;

            _watchdog.Stop();
            _workerStartLatch.Wait(); // Make sure any worker being (re)started has finished.
            _shutdownToken.Cancel();

            for (int i = 0; i < _workers.Length; i++)
            {
                Thread worker = _workers[i];

                if (worker != null && Thread.CurrentThread != worker && worker.IsAlive)
                    worker.Join();
            }
            foreach (ManualResetEventSlim ready in _workReady) ready.Dispose();
            _workCompleted.Dispose();
            _workerStartLatch.Dispose();
            _shutdownToken.Dispose();
        }

        protected override void InitWorker(object obj)
        {
            (int Id, bool Restart) = ((int, bool)) obj;
            _workers[Id] = Thread.CurrentThread;
            _workerCycle[Id] = GameLoopThreadPoolWatchdog.IDLE_CYCLE;
            base.InitWorker(obj);
            _workerStartLatch.Signal();
            RunWorkerLoop(Id, _shutdownToken.Token);
        }

        private void RestartWorkers(List<int> _workersToRestart)
        {
            _workerStartLatch = new(_workersToRestart.Count);

            foreach (int id in _workersToRestart)
            {
                if (log.IsWarnEnabled)
                    log.Warn($"Restarting thread \"{_workers[id].Name}\"");

                _workers[id] = null;
                Thread newThread = new(new ParameterizedThreadStart(InitWorker))
                {
                    Name = $"{GameLoop.THREAD_NAME}_Worker_{id}",
                    IsBackground = true,
                };
                newThread.Start((id, true));
            }
        }

        private void RunWorkerLoop(int id, CancellationToken cancellationToken)
        {
            ManualResetEventSlim workReady = _workReady[id];
            ref long workerCycle = ref _workerCycle[id];
            long cycle = GameLoopThreadPoolWatchdog.IDLE_CYCLE;

            while (Volatile.Read(ref _running))
            {
                bool participating = false;
                try
                {
                    workReady.Wait(cancellationToken);
                    participating = true;
                    workerCycle = ++cycle;
                    ExecutionContext.Run(_workContext, static state => ((GameLoopThreadPoolMultiThreaded) state).ProcessWorkActions(), this);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ThreadInterruptedException)
                {
                    if (log.IsWarnEnabled)
                        log.Warn($"Thread \"{Thread.CurrentThread.Name}\" was interrupted");
                    // A watchdog interrupt can arrive after work has finished,
                    // at the next idle wait. Keep this worker available: exiting
                    // and clearing an unconsumed signal strands the next batch.
                    continue;
                }
                catch (Exception e)
                {
                    if (log.IsFatalEnabled)
                        log.Fatal($"Critical error encountered in \"{nameof(GameLoopThreadPoolMultiThreaded)}\"", e);

                    Interlocked.CompareExchange(ref _workException, e, null);
                }
                finally
                {
                    workerCycle = GameLoopThreadPoolWatchdog.IDLE_CYCLE;
                    if (participating)
                    {
                        workReady.Reset();
                        Interlocked.Increment(ref _workState.CompletedWorkerCount);
                        _workCompleted.Signal();
                    }
                }
            }

            if (log.IsInfoEnabled)
                log.Info($"Thread \"{Thread.CurrentThread.Name}\" is stopping");
        }

        private void ProcessWorkActions()
        {
            CheckResetTick();
            int remainingWork = Volatile.Read(ref _workState.RemainingWork);

            while (remainingWork > 0)
            {
                int workersRemaining = _degreeOfParallelism - Volatile.Read(ref _workState.CompletedWorkerCount);
                int chunkSize = (int) (remainingWork / _workSplitBiasTable[workersRemaining]);

                if (chunkSize < 1)
                    chunkSize = 1;
                // Never strand a large tail on one worker when the remaining
                // items include uneven-cost bot decisions or navigation jobs.
                chunkSize = Math.Min(chunkSize, 32);

                int start = Interlocked.Add(ref _workState.RemainingWork, -chunkSize);
                int end = start + chunkSize;

                if (end < 1)
                    break;

                if (start < 0)
                    start = 0;

                for (int i = start; i < end; i++)
                {
                    try
                    {
                        _workProcessor.Process(i);
                    }
                    catch (Exception exception)
                    {
                        // Finish the batch barrier, then propagate on its
                        // owner. Never replay a partially executed action.
                        Interlocked.CompareExchange(ref _workException, exception, null);
                    }
                }

                // start is the number of unclaimed items, not an inclusive
                // item index. Subtracting one can strand item zero when only
                // one worker remains. Re-read shared work after every chunk.
                remainingWork = Volatile.Read(ref _workState.RemainingWork);
            }
        }

        private static class WorkProcessorCache<T>
        {
            public static readonly WorkProcessor<T> Instance = new();
        }

        private sealed class WorkProcessor<T> : WorkProcessor
        {
            private List<T> _items;
            private Action<T> _action;

            public WorkProcessor() { }

            public WorkProcessor<T> Set(List<T> items, Action<T> action)
            {
                _items = items;
                _action = action;
                return this;
            }

            public override void Process(int index)
            {
                _action(_items[index]);
            }

            public void Clear()
            {
                _items = default;
                _action = null;
            }
        }

        private abstract class WorkProcessor
        {
            public abstract void Process(int index);
        }
    }
}
