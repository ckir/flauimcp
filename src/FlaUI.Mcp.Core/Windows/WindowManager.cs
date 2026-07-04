using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.UIA3;

namespace FlaUI.Mcp.Core.Windows;

public sealed record WindowBounds(int X, int Y, int W, int H);
public sealed record WindowInfo(
    string Title, string ProcessName, int Pid, bool IsForeground,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowBounds? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ZOrder = null);

public sealed class WindowManager : IDisposable
{
    private readonly AutomationDispatcher _dispatcher;
    private readonly UIA3Automation _automation;
    private readonly ConcurrentDictionary<string, Window> _handles = new();
    private readonly ConcurrentDictionary<string, IntPtr> _hwnds = new();
    private readonly ConcurrentDictionary<string, Process> _watched = new();
    private int _counter;

    /// <summary>Raised when a window handle is invalidated (process exit, close_window, or a
    /// PruneClosedWindows sweep observing a dead HWND). Carries the windowId. Fires at most once per
    /// invalidation and only when tracked state was actually removed. Subscribers must be thread-safe:
    /// this can fire on a ThreadPool thread (via proc.Exited). Subscriber exceptions are swallowed on
    /// the invalidation path (they do NOT propagate to the invalidator).</summary>
    public event Action<string>? WindowInvalidated;

    public WindowManager(AutomationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // UIA objects must be created on the STA thread that uses them.
        _automation = _dispatcher.RunQueryAsync(() => new UIA3Automation()).GetAwaiter().GetResult();
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() => ListWindowsAsync(false);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds) =>
        _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
        {
            PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
            // PURE Win32 — no UIA. A UIA Title/ProcessId read on the query STA blocks with no
            // timeout on ANY momentarily-unresponsive desktop window; Win32 GetWindowText does not.
            var foreground = GetForegroundWindow();
            var list = new List<WindowInfo>(); int z = 0;
            foreach (var (hwnd, title, pid) in EnumTopLevel())
            {
                WindowBounds? b = null;
                if (includeBounds && GetWindowRect(hwnd, out var r))
                    b = new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                list.Add(new WindowInfo(title, SafeProcessName(pid), pid, hwnd == foreground, b, includeBounds ? z : (int?)null));
                z++;
            }
            return list;
        });

    /// <summary>Run an arbitrary read on the query STA (used by full-desktop capture, which needs an STA hop without a specific window).</summary>
    public Task<T> RunOnQueryAsync<T>(Func<T> func) => _dispatcher.RunQueryAsync(func);

    /// <summary>Fire-and-forget: marshal an action onto the single query STA so it serializes BEHIND any
    /// in-flight query (snapshot/find walk). Used to run RefRegistry.EvictWindow on the SAME thread that
    /// Register/BeginSnapshot run on — restoring RefRegistry's single-STA invariant even when the
    /// invalidation originates off-STA (a proc.Exited ThreadPool callback). The marshaled action
    /// (EvictWindow) only does locked dictionary removals and cannot throw, so the un-awaited task can
    /// never surface an unobserved fault.</summary>
    public void PostToQuerySta(Action action) => _ = _dispatcher.RunQueryAsync(action);

    public Task<WindowHandle> OpenByPidAsync(int pid) =>
        _dispatcher.RunQueryAsync(() =>
        {
            // A just-launched app's top-level window isn't always enumerable the instant the process
            // goes input-idle. Poll briefly so launch->open (and slow-rendering apps under load) don't
            // spuriously fail with "No window for pid".
            var deadline = DateTime.UtcNow.AddSeconds(12);
            IntPtr hwnd;
            while (true)
            {
                hwnd = EnumTopLevel().FirstOrDefault(w => w.Pid == pid).Hwnd;
                if (hwnd != IntPtr.Zero || DateTime.UtcNow >= deadline) break;
                Thread.Sleep(100);
            }
            if (hwnd == IntPtr.Zero)
                throw new ToolException(ToolErrorCode.WindowNotFound,
                    $"No window for pid {pid}.", "re-list windows");
            // Touch UIA only for the ONE resolved target (our app's window — responsive).
            return Register(_automation.FromHandle(hwnd).AsWindow(), pid);
        });

    public Task<T> RunOnWindowAsync<T>(WindowHandle handle, Func<Window, T> func) =>
        _dispatcher.RunQueryAsync(() =>
        {
            if (!_handles.TryGetValue(handle.Id, out var w))
                throw new ToolException(ToolErrorCode.WindowHandleStale,
                    $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
            return func(w);
        });

    /// <summary>Run a read callback on the query STA with the resolved window AND the Desktop
    /// element (the snapshot engine needs the Desktop to graft owner-process popups, which are
    /// children of the Desktop — not the target window). Reuses the stale-handle guard.</summary>
    public Task<T> RunWithWindowAndDesktopAsync<T>(WindowHandle handle, Func<Window, AutomationElement, T> func) =>
        _dispatcher.RunQueryAsync(() =>
        {
            if (!_handles.TryGetValue(handle.Id, out var w))
                throw new ToolException(ToolErrorCode.WindowHandleStale,
                    $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
            return func(w, _automation.GetDesktop());
        });

    public void Invalidate(WindowHandle handle)
    {
        // Exactly-once gate. _handles is populated UNCONDITIONALLY by Register for every id, and
        // ConcurrentDictionary.TryRemove returns true to EXACTLY ONE caller per key — so electing
        // _handles as the SOLE gate makes WindowInvalidated fire at most once even when two threads
        // invalidate the same handle concurrently (proc.Exited racing CloseAsync, or a sweep racing
        // proc.Exited). _hwnds/_watched are best-effort (populated inside try/catch) so they cannot
        // serve as the gate — remove them unconditionally (still disposing the watched process), but
        // do NOT let their removal set `removed`.
        bool removed = _handles.TryRemove(handle.Id, out _);
        _hwnds.TryRemove(handle.Id, out _);
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
        if (removed)
            // Swallow subscriber faults: this can fire on a raw ThreadPool thread (proc.Exited), where an
            // unhandled exception is process-fatal, and from CloseAsync's finally, where it would mask an
            // in-flight exception. Invalidation-path robustness > surfacing a subscriber bug here (mirrors
            // the _watched Dispose guard above).
            try { WindowInvalidated?.Invoke(handle.Id); } catch { }
    }

    /// <summary>Pure liveness decision (no UIA / no instance state beyond the passed pairs) so it is
    /// unit-testable headless: given the tracked (windowId, hwnd) pairs and an aliveness predicate,
    /// return the windowIds whose HWND is gone (IntPtr.Zero, or !isAlive). IntPtr.Zero is dead without
    /// consulting the predicate.</summary>
    internal static IReadOnlyList<string> DeadWindowIds(
        IReadOnlyCollection<KeyValuePair<string, IntPtr>> tracked, Func<IntPtr, bool> isAlive)
    {
        var dead = new List<string>();
        foreach (var (id, hwnd) in tracked)
            if (hwnd == IntPtr.Zero || !isAlive(hwnd))
                dead.Add(id);
        return dead;
    }

    /// <summary>Best-effort memory hygiene: invalidate any tracked handle whose HWND is no longer a live
    /// window (user/app closed it without a process exit, so neither proc.Exited nor CloseAsync fired).
    /// Routes each dead id through Invalidate, so _handles/_hwnds/_watched AND (via the WindowInvalidated
    /// event) RefRegistry are all reclaimed on one path. Pure Win32 (IsWindow) + ConcurrentDictionary ops
    /// — needs no STA, safe to call off the query STA. Snapshots _hwnds first so the Invalidate-driven
    /// TryRemove inside the loop can't corrupt the enumeration. <paramref name="isAlive"/> is injectable
    /// for tests (default = Win32 IsWindow).</summary>
    internal void PruneClosedWindows(Func<IntPtr, bool>? isAlive = null)
    {
        isAlive ??= IsWindow;
        foreach (var id in DeadWindowIds(_hwnds.ToArray(), isAlive))
            Invalidate(new WindowHandle(id));
    }

    internal WindowHandle Register(Window window, int pid)
    {
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _handles[id] = window;
        try { _hwnds[id] = window.Properties.NativeWindowHandle.ValueOrDefault; } catch { /* no hwnd */ }
        TryWatchProcessExit(id, pid);
        return new WindowHandle(id);
    }

    private void TryWatchProcessExit(string id, int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => Invalidate(new WindowHandle(id));
            _watched[id] = proc;
        }
        catch { /* process already gone; next RunOnWindow surfaces WindowHandleStale */ }
    }

    private static string SafeProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "unknown"; }
    }

    // ── Win32 top-level window enumeration ────────────────────────────────────────────────
    // A UIA Title/ProcessId read (the old GetDesktop().FindAllChildren() path) on the query STA
    // blocks with NO timeout on ANY momentarily-unresponsive desktop window. GetWindowText, by
    // MSDN design, does NOT block on a hung window of another process (it returns the cached
    // caption). So we enumerate/match via pure Win32 and touch UIA only for the ONE resolved
    // target window (via FromHandle).
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>True for a window that perception must never surface — currently only the intent-overlay's
    /// sentinel class (spec §5.4). Extracted so the enum filter is unit-testable without a live window.</summary>
    public static bool ShouldSkipTopLevel(string className) =>
        string.Equals(className, FlaUI.Mcp.Core.Interaction.OverlaySentinel.ClassName, System.StringComparison.Ordinal);

    /// <summary>Enumerate visible, titled top-level windows via pure Win32 — never blocks on an
    /// unresponsive window of another process (unlike a UIA Title read). Mirrors the old filter
    /// (visible + non-empty title).</summary>
    private static List<(IntPtr Hwnd, string Title, int Pid)> EnumTopLevel()
    {
        var result = new List<(IntPtr, string, int)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLengthW(hwnd);
            if (len == 0) return true; // mirrors the old !string.IsNullOrEmpty(w.Title) filter
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrEmpty(title)) return true;
            var clsSb = new StringBuilder(64);
            GetClassNameW(hwnd, clsSb, clsSb.Capacity);
            if (ShouldSkipTopLevel(clsSb.ToString())) return true; // skip the intent overlay (spec §5.4)
            GetWindowThreadProcessId(hwnd, out uint pid);
            result.Add((hwnd, title, (int)pid));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public async Task<(WindowHandle handle, int pid)> LaunchAppAsync(string path, string? args, int timeoutMs)
    {
        // Snapshot all existing PIDs before launch so we can detect newly spawned ones
        // (needed for apps like Win11 Notepad that delegate to a host process under a different PID).
        var preExistingPids = Process.GetProcesses().Select(p => p.Id).ToHashSet();
        // The launched exe's base name (e.g. "notepad"), used to attach only to a window
        // owned by the app we actually started — not the first unrelated window that opens.
        var expectedProcessName = Path.GetFileNameWithoutExtension(path);

        Process proc;
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            proc = Process.Start(psi)
                   ?? throw new ToolException(ToolErrorCode.LaunchTimeout, $"Failed to start {path}.", "verify the path");
        }
        catch (ToolException) { throw; }
        catch (Exception ex)
        {
            throw new ToolException(ToolErrorCode.LaunchTimeout, $"Could not launch {path}: {ex.Message}", "verify the path");
        }

        try { proc.WaitForInputIdle(Math.Min(timeoutMs, 5000)); } catch { /* console/no message loop */ }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            // First try the exact launched PID.
            var handle = await TryOpenByPidQuiet(proc.Id);
            if (handle is { } h) return (h, proc.Id);

            // Also scan NEW pids whose process name matches the launched exe (e.g. Win11
            // Notepad's host spawns the real editor under a different PID with the same name).
            // Matching by name avoids grabbing an unrelated window that opened mid-launch.
            var result = await TryOpenByMatchingNewPidQuiet(preExistingPids, expectedProcessName);
            if (result is { } r) return r;

            await Task.Delay(150);
        }
        throw new ToolException(ToolErrorCode.LaunchTimeout,
            $"{path} started but showed no titled window within {timeoutMs} ms.",
            "increase timeoutMs or check for a splash screen");
    }

    private Task<WindowHandle?> TryOpenByPidQuiet(int pid) =>
        _dispatcher.RunQueryAsync<WindowHandle?>(() =>
        {
            var hwnd = EnumTopLevel().FirstOrDefault(w => w.Pid == pid).Hwnd;
            return hwnd == IntPtr.Zero
                ? (WindowHandle?)null
                : Register(_automation.FromHandle(hwnd).AsWindow(), pid);
        });

    private Task<(WindowHandle handle, int pid)?> TryOpenByMatchingNewPidQuiet(
        HashSet<int> preExistingPids, string expectedProcessName) =>
        _dispatcher.RunQueryAsync<(WindowHandle, int)?>(() =>
        {
            foreach (var (hwnd, _, pid) in EnumTopLevel())
            {
                if (preExistingPids.Contains(pid)) continue;
                if (!LaunchedWindowMatcher.IsExpectedApp(expectedProcessName, SafeProcessName(pid))) continue;
                return (Register(_automation.FromHandle(hwnd).AsWindow(), pid), pid);
            }
            return ((WindowHandle, int)?)null;
        });

    public Task<WindowHandle> OpenByTitleAsync(string title) =>
        _dispatcher.RunQueryAsync(() =>
        {
            var matches = EnumTopLevel().Where(w => w.Title == title).ToList();
            if (matches.Count == 0)
                throw new ToolException(ToolErrorCode.WindowNotFound, $"No window titled '{title}'.", "re-list windows");
            if (matches.Count > 1)
            {
                var candidates = string.Join("; ", matches.Select(m => $"pid={m.Pid} hwnd={m.Hwnd}"));
                throw new ToolException(ToolErrorCode.AmbiguousMatch,
                    $"{matches.Count} windows titled '{title}': {candidates}",
                    "re-open by:pid using one of the listed pids");
            }
            var match = matches[0];
            return Register(_automation.FromHandle(match.Hwnd).AsWindow(), match.Pid);
        });

    /// <summary>Run a callback on a transient ACTION STA with the window and Desktop resolved
    /// by that thread's OWN automation (via the cached HWND) — so no query-STA COM object is
    /// marshaled across apartments. Used by all state-changing pattern actions.</summary>
    public Task<T> RunOnWindowActionAsync<T>(
        WindowHandle handle, Func<AutomationElement, AutomationElement, T> func, int timeoutMs)
    {
        if (!_hwnds.TryGetValue(handle.Id, out var hwnd) || hwnd == IntPtr.Zero)
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
        return _dispatcher.RunActionAsync(() =>
        {
            using var automation = new UIA3Automation();
            var win = automation.FromHandle(hwnd);
            var desktop = automation.GetDesktop();
            return func(win, desktop);
        }, timeoutMs);
    }

    public Task FocusAsync(WindowHandle handle) =>
        RunOnWindowAsync(handle, w => { w.Focus(); w.SetForeground(); return true; });

    public Task<(WindowHandle Handle, string Title, int Pid)?> ResolveFocusedWindowAsync() =>
        _dispatcher.RunQueryAsync<(WindowHandle, string, int)?>(() =>
        {
            var focused = _automation.FocusedElement();
            if (focused is null) return null;
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = focused.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            // True top-level window via Win32 GA_ROOT; fall back to the foreground window
            // (a focused element is always in it) when the element exposes no own HWND.
            hwnd = hwnd != IntPtr.Zero ? GetAncestor(hwnd, GA_ROOT) : GetForegroundWindow();
            int pid = -1; string title = "";
            try { pid = focused.Properties.ProcessId.ValueOrDefault; } catch { }
            try { title = focused.Properties.Name.ValueOrDefault ?? ""; } catch { }
            if (hwnd == IntPtr.Zero) return null;
            var handle = Register(_automation.FromHandle(hwnd).AsWindow(), pid);
            return (handle, title, pid);
        });

    /// <summary>The owning process base-name + class of the CURRENT UIA focused element (for the no-ref
    /// foreground-key deny-list — classify the element that will actually receive the keystroke, not just
    /// its host window). Null if nothing is focused. Read on the query STA like ResolveFocusedWindowAsync.</summary>
    public Task<(int Pid, string? Process, string? Class)?> ResolveFocusedIdentityAsync() =>
        _dispatcher.RunQueryAsync<(int, string?, string?)?>(() =>
        {
            var focused = _automation.FocusedElement();
            if (focused is null) return null;
            int pid = -1; string? proc = null; string? cls = null;
            try { pid = focused.Properties.ProcessId.ValueOrDefault; } catch { }
            if (pid >= 0) { try { using var p = Process.GetProcessById(pid); proc = p.ProcessName; } catch { } }
            try { cls = focused.Properties.ClassName.ValueOrDefault; } catch { }
            return (pid < 0 ? 0 : pid, proc, cls);
        });

    public async Task CloseAsync(WindowHandle handle)
    {
        try { await RunOnWindowAsync(handle, w => { w.Close(); return true; }); }
        catch (ToolException) { /* already gone */ }
        finally { Invalidate(handle); }
    }

    public void Dispose()
    {
        foreach (var p in _watched.Values) try { p.Dispose(); } catch { }
        _dispatcher.RunQueryAsync(() => _automation.Dispose()).GetAwaiter().GetResult();
    }
}
