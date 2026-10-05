using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Amuse.App.Services
{
    /// <summary>
    /// Watches the UI thread and reports every stall together with the modules that
    /// are loaded into this process.
    ///
    /// A UI stall raises no exception and no event, so nothing else in the log can
    /// show it: the window stops responding and the last log line simply stays where
    /// it was. Automatic keyboard layout switchers make this visible by injecting a
    /// hook module into the application process, and that module runs inside our
    /// own message pump, so the app itself cannot tell such a stall apart from any
    /// other one. Listing the modules loaded from outside the app and system
    /// directories is what makes the stall attributable after the fact.
    /// </summary>
    public sealed class UiStallMonitor : IDisposable
    {
        /// <summary>
        /// Stalls shorter than this are normal. Layout, image decode and the first
        /// pipeline load all hold the UI thread for a few hundred milliseconds.
        /// </summary>
        private const int ThresholdMs = 1000;

        /// <summary>
        /// How often the UI thread is probed.
        /// </summary>
        private const int IntervalMs = 500;

        /// <summary>
        /// One stall is reported on its own; a burst of them is reported once per
        /// window, so a frozen UI cannot flood the log.
        /// </summary>
        private const int CooldownMs = 5000;

        private readonly Dispatcher _dispatcher;
        private readonly int _uiThreadId;
        private readonly CancellationTokenSource _cancellation = new();
        private long _stallCount;
        private long _stallTotalMs;
        private long _worstStallMs;
        private long _lastReportTimestamp;
        private bool _disposed;


        /// <summary>
        /// Initializes a new instance of the <see cref="UiStallMonitor"/> class.
        /// </summary>
        /// <param name="dispatcher">The dispatcher of the UI thread to watch.</param>
        public UiStallMonitor(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _uiThreadId = dispatcher.Thread.ManagedThreadId;
        }


        /// <summary>
        /// Starts watching.
        /// </summary>
        public void Start()
        {
            Log.Logger.Information("[UiStallMonitor] Watching UI thread {_uiThreadId} for stalls over {ThresholdMs} ms. Modules loaded from outside the app and system directories: {Modules}",
                _uiThreadId, ThresholdMs, GetForeignModules());
            _ = Task.Run(() => WatchAsync(_cancellation.Token));
        }


        /// <summary>
        /// Writes the totals collected so far. Called on shutdown, when the UI thread
        /// is busy tearing the application down and any fresh stall is not meaningful.
        /// </summary>
        public void LogSummary()
        {
            if (Volatile.Read(ref _stallCount) == 0)
            {
                Log.Logger.Information("[UiStallMonitor] No UI stalls detected.");
                return;
            }

            Log.Logger.Warning("[UiStallMonitor] UI stalls: Count: {Count}, Total: {Total} ms, Worst: {Worst} ms. Foreign modules: {Modules}",
                Volatile.Read(ref _stallCount), Volatile.Read(ref _stallTotalMs), Volatile.Read(ref _worstStallMs), GetForeignModules());
        }


        /// <summary>
        /// Releases the resources used by the <see cref="UiStallMonitor"/> class.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cancellation.Cancel();
            _cancellation.Dispose();
        }


        /// <summary>
        /// Probes the UI thread until cancelled.
        /// </summary>
        private async Task WatchAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ProbeAsync(token);
                    await Task.Delay(IntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log.Logger.Debug(e, "[UiStallMonitor] Probe failed.");
                }
            }
        }


        /// <summary>
        /// Posts an empty callback to the UI thread and waits for it to run.
        /// </summary>
        private async Task ProbeAsync(CancellationToken token)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            await _dispatcher.InvokeAsync(() => signal.TrySetResult(), DispatcherPriority.Background);
            var finished = await Task.WhenAny(signal.Task, Task.Delay(ThresholdMs, token));
            if (ReferenceEquals(finished, signal.Task))
                return;

            // The callback is still queued, so the UI thread has been busy for at
            // least as long as the delay we just waited out.
            Report(watch.ElapsedMilliseconds);
        }


        /// <summary>
        /// Records a stall and logs it, unless it is part of a burst that was already
        /// reported.
        /// </summary>
        private void Report(long elapsedMs)
        {
            Interlocked.Increment(ref _stallCount);
            Interlocked.Add(ref _stallTotalMs, elapsedMs);
            InterlockedMax(ref _worstStallMs, elapsedMs);

            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastReportTimestamp) < CooldownMs)
                return;
            Interlocked.Exchange(ref _lastReportTimestamp, now);

            Log.Logger.Warning("[UiStallMonitor] UI thread {UiThreadId} did not respond for at least {ElapsedMs} ms (still busy). Stalls so far: {Count}. Modules loaded from outside the app and system directories: {Modules}",
                _uiThreadId, elapsedMs, Volatile.Read(ref _stallCount), GetForeignModules());
        }


        /// <summary>
        /// Assigns <paramref name="value"/> when it is larger than the current one.
        /// </summary>
        private static void InterlockedMax(ref long target, long value)
        {
            long current = Interlocked.Read(ref target);
            while (value > current)
            {
                long previous = Interlocked.CompareExchange(ref target, value, current);
                if (previous == current)
                    return;
                current = previous;
            }
        }


        /// <summary>
        /// Lists the modules that do not belong to the application, Windows or the
        /// .NET runtime. A hook injected by another process shows up here.
        /// </summary>
        private static string GetForeignModules()
        {
            try
            {
                var own = new List<string>
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Path.GetDirectoryName(typeof(object).Assembly.Location),
                    RuntimeEnvironment.GetRuntimeDirectory()
                }.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();

                var modules = new List<string>();
                using (var process = Process.GetCurrentProcess())
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        var path = module.FileName;
                        if (string.IsNullOrEmpty(path))
                            continue;
                        if (own.Any(x => path.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        modules.Add(module.ModuleName);
                    }
                }

                if (modules.Count == 0)
                    return "(none)";
                modules.Sort(StringComparer.OrdinalIgnoreCase);
                return string.Join(", ", modules);
            }
            catch (Exception e)
            {
                // Module enumeration is a diagnostic, never worth failing over.
                return $"(unavailable: {e.GetType().Name})";
            }
        }
    }
}
