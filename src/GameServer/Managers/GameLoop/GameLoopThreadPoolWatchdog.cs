using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using DOL.Logging;

namespace DOL.GS
{
    public class GameLoopThreadPoolWatchdog
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public const long IDLE_CYCLE = -1;             // Represents an idle worker thread cycle, used to detect stuck threads.
        private const int WORKER_TIMEOUT_MS = 7500;    // Slow-worker diagnostic threshold, not permission to interrupt native work.

        private bool _running;
        private Thread _watchdogThread;
        private Thread[] _workers;
        private long[] _workerCycle;
        private Action<List<int>> _restartWorkersAction;

        public GameLoopThreadPoolWatchdog(Thread[] workers, long[] workerCycle, Action<List<int>> restartWorkersAction)
        {
            _workers = workers ?? throw new ArgumentNullException(nameof(workers));
            _workerCycle = workerCycle ?? throw new ArgumentNullException(nameof(workerCycle));
            _restartWorkersAction = restartWorkersAction ?? throw new ArgumentNullException(nameof(restartWorkersAction));
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;

            _watchdogThread = new(WatchdogLoop)
            {
                Name = $"{GameLoop.THREAD_NAME}_Watchdog",
                Priority = ThreadPriority.Lowest,
                IsBackground = true,
            };
            _watchdogThread.Start();
        }

        public void Stop()
        {
            if (!_running)
                return;

            _running = false;

            if (_watchdogThread != Thread.CurrentThread && _watchdogThread.IsAlive)
                _watchdogThread.Join();

            _watchdogThread = null;
        }

        private void WatchdogLoop()
        {
            List<int> _workersToRestart = new();
            long[] lastReportedCycle = new long[_workers.Length];
            Array.Fill(lastReportedCycle, IDLE_CYCLE);

            while (Volatile.Read(ref _running))
            {
                try
                {
                    for (int i = 0; i < _workers.Length; i++)
                    {
                        Thread worker = _workers[i];

                        // Thread is null if it was removed by the watchdog. At this point it's already being replaced, hopefully.
                        if (worker == null)
                            continue;

                        if (worker.Join(100))
                        {
                            if (log.IsWarnEnabled)
                                log.Warn($"Watchdog: Thread \"{worker.Name}\" has exited unexpectedly. Restarting...");

                            _workersToRestart.Add(i);
                        }
                        else
                        {
                            long cycle = Volatile.Read(ref _workerCycle[i]);

                            if (cycle > IDLE_CYCLE)
                            {
                                // A slow inventory/native operation may recover normally.
                                // Thread.Interrupt cannot safely cancel an arbitrary actor
                                // action; it can instead land in the following idle wait.
                                if (worker.Join(WORKER_TIMEOUT_MS))
                                {
                                    if (log.IsWarnEnabled)
                                        log.Warn($"Watchdog: Thread \"{worker.Name}\" has exited unexpectedly. Restarting...");

                                    _workersToRestart.Add(i);
                                }
                                else if (Volatile.Read(ref _workerCycle[i]) == cycle)
                                {
                                    if (lastReportedCycle[i] != cycle && log.IsWarnEnabled)
                                        log.Warn($"Watchdog: Thread \"{worker.Name}\" is taking too long; preserving its in-flight work and waiting for completion.");
                                    lastReportedCycle[i] = cycle;
                                }
                            }
                        }
                    }

                    if (_workersToRestart.Count == 0)
                        continue;

                    _restartWorkersAction(_workersToRestart);
                    _workersToRestart.Clear();
                }
                catch (ThreadInterruptedException)
                {
                    if (log.IsWarnEnabled)
                        log.Warn($"Thread \"{Thread.CurrentThread.Name}\" was interrupted");
                }
                catch (Exception e)
                {
                    if (log.IsErrorEnabled)
                        log.Error($"Thread \"{Thread.CurrentThread.Name}\"", e);
                }
            }

            if (log.IsInfoEnabled)
                log.Info($"Thread \"{Thread.CurrentThread.Name}\" is stopping");
        }
    }
}
