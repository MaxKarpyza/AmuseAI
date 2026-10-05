using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Amuse.App.Services
{
    /// <summary>
    /// Records what the UI thread is doing, so a window that stops responding leaves
    /// a record instead of nothing at all.
    ///
    /// A stall raises no exception, writes no log line and blocks only the thread it
    /// blocks, so the last line in the log stays exactly where it was. Worse, the
    /// input is not always ours: an automatic keyboard layout switcher injects a hook
    /// module into the application process, and that module runs inside our own
    /// message pump. This monitor therefore keeps the last window messages, the input
    /// state sampled just before the stall, the native wait state of the UI thread,
    /// and every module loaded from outside the application and system directories,
    /// so the stall can be attributed after the fact.
    /// </summary>
    public sealed class UiStallMonitor : IDisposable
    {
        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private static class Native
        {
            public const int GHITHREADINFO = 0;

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();

            [DllImport("user32.dll")]
            public static extern IntPtr GetKeyboardLayout(uint threadId);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetKeyboardLayoutName(StringBuilder name);

            [DllImport("user32.dll")]
            public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetLocaleInfo(int locale, int lcType, StringBuilder data, int count);

            /// <summary>
            /// Names a keyboard layout handle, e.g. 0x04190419 becomes ru-RU.
            /// </summary>
            public static string GetLayoutName(IntPtr hkl)
            {
                const int LOCALE_SNAME = 0x0002;
                var sb = new StringBuilder(16);
                return GetLocaleInfo(hkl.ToInt32(), LOCALE_SNAME, sb, sb.Capacity) > 0
                    ? sb.ToString()
                    : "unknown";
            }
        }

        #endregion

        /// <summary>
        /// Stalls shorter than this are normal. Layout, image decode and the first
        /// pipeline load all hold the UI thread for a few hundred milliseconds.
        /// </summary>
        private const int StallThresholdMs = 750;

        /// <summary>
        /// Latency above this is worth a debug line even when it stays under the
        /// stall threshold, because it shows how close a freeze came to happening.
        /// </summary>
        private const int SlowProbeMs = 250;

        /// <summary>
        /// How often the UI thread is probed.
        /// </summary>
        private const int IntervalMs = 250;

        /// <summary>
        /// One stall is reported on its own; a burst is reported once per window, so a
        /// frozen UI cannot flood the log.
        /// </summary>
        private const int CooldownMs = 3000;

        /// <summary>
        /// How many window messages are remembered for the stall report.
        /// </summary>
        private const int RecentMessageCount = 40;

        private static readonly Dictionary<int, string> _messages = new()
        {
            { 0x0006, "WM_ACTIVATE" },
            { 0x0007, "WM_SETFOCUS" },
            { 0x0008, "WM_KILLFOCUS" },
            { 0x0010, "WM_CLOSE" },
            { 0x0050, "WM_INPUTLANGCHANGEREQUEST" },
            { 0x0051, "WM_INPUTLANGCHANGE" },
            { 0x0100, "WM_KEYDOWN" },
            { 0x0101, "WM_KEYUP" },
            { 0x0102, "WM_CHAR" },
            { 0x0281, "WM_IME_SETCONTEXT" },
            { 0x0283, "WM_IME_COMPOSITION" },
            { 0x0284, "WM_IME_COMPOSITIONSTART" },
            { 0x0285, "WM_IME_COMPOSITIONEND" },
            { 0x0286, "WM_IME_CHAR" },
            { 0x0287, "WM_IME_NOTIFY" },
            { 0x0288, "WM_IME_REQUEST" },
            { 0x00D2, "WM_INPUT" },
            { 0x031E, "WM_DWMCOMPOSITIONCHANGED" }
        };

        private static readonly HashSet<int> _loggedMessages = new()
        {
            0x0006, 0x0007, 0x0008, 0x0050, 0x0051, 0x0281, 0x0283, 0x0284, 0x0285, 0x0286, 0x0287, 0x0288
        };

        private readonly Dispatcher _dispatcher;
        private readonly int _uiManagedThreadId;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Queue<string> _recent = new();
        private readonly object _recentLock = new();
        private uint _uiNativeThreadId;
        private string _lastLayout;
        private string _lastFocus;
        private string _lastCulture;
        private HwndSource _source;
        private long _stallCount;
        private long _stallTotalMs;
        private long _worstStallMs;
        private long _lastReportTimestamp;
        private long _probeFailures;
        private bool _disposed;


        /// <summary>
        /// Initializes a new instance of the <see cref="UiStallMonitor"/> class.
        /// </summary>
        /// <param name="dispatcher">The dispatcher of the UI thread to watch.</param>
        public UiStallMonitor(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _uiManagedThreadId = dispatcher.Thread.ManagedThreadId;
        }


        /// <summary>
        /// Starts watching the UI thread and the messages of <paramref name="window"/>.
        /// </summary>
        /// <param name="window">The main window, watched for input and IME messages.</param>
        public void Start(Window window)
        {
            // Window messages are the only record of what the input stack was asked to
            // do, and they arrive on the thread that is about to stall.
            if (window != null)
            {
                _source = PresentationSource.FromVisual(window) as HwndSource;
                _source?.AddHook(WndProc);
            }

            Log.Logger.Information("[UiStallMonitor] Watching UI thread {_uiManagedThreadId} (managed), messages hooked: {Hooked}. Stall threshold: {ThresholdMs} ms, probe interval: {IntervalMs} ms.",
                _uiManagedThreadId, _source != null, StallThresholdMs, IntervalMs);
            Log.Logger.Information("[UiStallMonitor] Render mode: {RenderMode}.", RenderOptions.ProcessRenderMode);
            Log.Logger.Information("[UiStallMonitor] Foreign modules in this process: {Modules}", GetForeignModules(Process.GetCurrentProcess().Id));
            Log.Logger.Information("[UiStallMonitor] Child processes: {Children}", string.Join("; ", GetChildProcesses()));

            _ = Task.Run(() => WatchAsync(_cancellation.Token));
        }


        /// <summary>
        /// Writes the totals collected so far. Called on shutdown, when the UI thread
        /// is busy tearing the application down and any fresh stall says nothing.
        /// </summary>
        public void LogSummary()
        {
            var failures = Volatile.Read(ref _probeFailures);
            if (failures > 0)
                Log.Logger.Warning("[UiStallMonitor] Input sampling failed {Failures} times; the samples below are incomplete.", failures);

            if (Volatile.Read(ref _stallCount) == 0)
            {
                Log.Logger.Information("[UiStallMonitor] No UI stalls detected.");
                return;
            }

            Log.Logger.Warning("[UiStallMonitor] UI stalls: Count: {Count}, Total: {Total} ms, Worst: {Worst} ms. Foreign modules: {Modules}. Child processes: {Children}.",
                Volatile.Read(ref _stallCount), Volatile.Read(ref _stallTotalMs), Volatile.Read(ref _worstStallMs),
                GetForeignModules(Process.GetCurrentProcess().Id), string.Join("; ", GetChildProcesses()));
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
            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source = null;
            }
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
                    ReportProbeFailure(e);
                }
            }
        }


        /// <summary>
        /// Reports a failed probe once. A diagnostic that repeats itself every
        /// interval can write millions of lines and hang the application it watches,
        /// so the first failure is logged and the rest are only counted.
        /// </summary>
        private void ReportProbeFailure(Exception e)
        {
            var count = Interlocked.Increment(ref _probeFailures);
            if (count == 1)
                Log.Logger.Warning(e, "[UiStallMonitor] Input sampling failed, further failures will only be counted.");
        }


        /// <summary>
        /// Posts an empty callback to the UI thread, waits for it, and samples the
        /// input state from inside it so the sample belongs to the moment the UI
        /// thread is actually responsive.
        /// </summary>
        private async Task ProbeAsync(CancellationToken token)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            await _dispatcher.InvokeAsync(() =>
            {
                // The sample must never be able to fault the dispatcher operation or
                // leave the signal unset: either would turn a broken diagnostic into a
                // stall that is not there.
                try
                {
                    SampleInputState();
                }
                catch (Exception e)
                {
                    ReportProbeFailure(e);
                }
                finally
                {
                    signal.TrySetResult();
                }
            }, DispatcherPriority.Background);
            var finished = await Task.WhenAny(signal.Task, Task.Delay(StallThresholdMs, token));
            if (ReferenceEquals(finished, signal.Task))
            {
                watch.Stop();
                if (watch.ElapsedMilliseconds >= SlowProbeMs)
                {
                    Log.Logger.Debug("[UiStallMonitor] UI round trip took {ElapsedMs} ms.", watch.ElapsedMilliseconds);
                }
                return;
            }

            // The callback is still queued, so the UI thread has been busy for at
            // least as long as the delay we just waited out.
            Report(watch.ElapsedMilliseconds);
        }


        /// <summary>
        /// Samples the input state on the UI thread and logs whatever changed.
        /// </summary>
        private void SampleInputState()
        {
            if (_uiNativeThreadId == 0)
                _uiNativeThreadId = Native.GetCurrentThreadId();

            var name = new StringBuilder(9);
            Native.GetKeyboardLayoutName(name);
            var hkl = Native.GetKeyboardLayout(0);
            var layout = $"{name} (0x{(long)hkl:X}, {Native.GetLayoutName(hkl)})";

            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            var focus = "(none)";
            if (Native.GetGUIThreadInfo(_uiNativeThreadId, ref info) && info.hwndFocus != IntPtr.Zero)
            {
                var cls = new StringBuilder(256);
                Native.GetClassName(info.hwndFocus, cls, cls.Capacity);
                focus = $"{cls} (0x{(long)info.hwndFocus:X})";
            }

            var culture = Thread.CurrentThread.CurrentUICulture?.Name ?? "(none)";

            var changed = layout != _lastLayout || focus != _lastFocus || culture != _lastCulture;
            _lastLayout = layout;
            _lastFocus = focus;
            _lastCulture = culture;
            if (changed)
            {
                // The culture is reported as a culture, not as an input language: the
                // thread culture follows the application language and never follows the
                // keyboard layout, so it must not be read as the latter.
                Log.Logger.Information("[UiStallMonitor] Input state: layout {Layout}, focus {Focus}, UI culture {Culture}.",
                    layout, focus, culture);
            }
        }


        /// <summary>
        /// Remembers input and IME messages, and logs the ones that matter on their
        /// own: a layout change is the trigger we are looking for.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_messages.TryGetValue(msg, out var name))
            {
                var text = $"{DateTime.Now:HH:mm:ss.fff} {name} w=0x{(long)wParam:X} l=0x{(long)lParam:X}";
                lock (_recentLock)
                {
                    _recent.Enqueue(text);
                    while (_recent.Count > RecentMessageCount)
                        _recent.Dequeue();
                }
                if (msg == 0x0050 || msg == 0x0051)
                {
                    Log.Logger.Information("[UiStallMonitor] {Message} received on the UI thread.", text);
                }
                else if (_loggedMessages.Contains(msg))
                {
                    // Keys and characters are kept in the ring for the stall report
                    // but never logged one by one: a typing session would bury it.
                    Log.Logger.Debug("[UiStallMonitor] {Message}", text);
                }
            }
            return IntPtr.Zero;
        }


        /// <summary>
        /// Records a stall and logs everything known about it, unless it is part of a
        /// burst that was already reported.
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

            int messageCount;
            string messages;
            lock (_recentLock)
            {
                messageCount = _recent.Count;
                messages = string.Join(" | ", _recent);
            }

            var process = Process.GetCurrentProcess();
            var wait = GetUiThreadWaitState();

            Log.Logger.Warning("[UiStallMonitor] UI thread blocked for at least {ElapsedMs} ms. Input state before the stall: layout {Layout}, focus {Focus}, UI culture {Culture}. Window messages: {Messages}. UI thread native state: {WaitState}. Stalls so far: {Count} (total {Total} ms, worst {Worst} ms). Foreign modules: {Modules}. Child processes: {Children}. Window messages recorded: {MessageCount}.",
                elapsedMs, _lastLayout ?? "(unknown)", _lastFocus ?? "(unknown)", _lastCulture ?? "(unknown)",
                messages, wait,
                Volatile.Read(ref _stallCount), Volatile.Read(ref _stallTotalMs), Volatile.Read(ref _worstStallMs),
                GetForeignModules(process.Id), string.Join("; ", GetChildProcesses()), messageCount);
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
        /// Reads the state and wait reason of the UI thread. A thread blocked in a
        /// message send to another process shows up here, which is what separates a
        /// stall caused by foreign injected code from ordinary long work.
        /// </summary>
        private string GetUiThreadWaitState()
        {
            if (_uiNativeThreadId == 0)
                return "(unknown)";
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    foreach (ProcessThread thread in process.Threads)
                    {
                        if (thread.Id != _uiNativeThreadId)
                            continue;
                        return $"{thread.ThreadState}, wait: {thread.WaitReason}, cpu: {thread.UserProcessorTime.TotalSeconds:F2}s, started: {thread.StartTime:HH:mm:ss}";
                    }
                }
            }
            catch (Exception e)
            {
                return $"(unavailable: {e.GetType().Name})";
            }
            return "(thread not found)";
        }


        /// <summary>
        /// Lists the modules of a process that do not belong to the application,
        /// Windows or the .NET runtime. A hook injected by another process shows up
        /// here.
        /// </summary>
        private static string GetForeignModules(int processId)
        {
            try
            {
                var own = new List<string>
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    RuntimeEnvironment.GetRuntimeDirectory()
                }.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();

                var modules = new List<string>();
                using (var process = Process.GetProcessById(processId))
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


        /// <summary>
        /// Lists the child processes of this application, which is where the embedded
        /// browser runs. Input handling that stalls there stalls the window too.
        /// </summary>
        private static string GetChildProcesses()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher($"SELECT ProcessId, Name FROM Win32_Process WHERE ParentProcessId = {Process.GetCurrentProcess().Id}"))
                using (var results = searcher.Get())
                {
                    var names = new List<string>();
                    foreach (var item in results)
                    {
                        using (item)
                            names.Add(item["Name"]?.ToString() ?? "?");
                    }
                    return names.Count == 0 ? "(none)" : string.Join(", ", names);
                }
            }
            catch (Exception e)
            {
                return $"(unavailable: {e.GetType().Name})";
            }
        }
    }
}
