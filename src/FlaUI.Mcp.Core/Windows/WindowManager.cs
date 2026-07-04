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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ZOrder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Handle = null);

public sealed class WindowManager : IDisposable
{
    private readonly AutomationDispatcher _dispatcher;
    private readonly UIA3Automation _automation;
    private readonly ConcurrentDictionary<string, Window> _handles = new();
    private readonly ConcurrentDictionary<string, IntPtr> _hwnds = new();
    // Exactly-once invalidation gate + recorded pid. Written UNCONDITIONALLY by every mint path
    // (eager Register AND lazy list-mint) — unlike _handles (a lazy COM cache, absent for a never-read
    // lazy handle) and _hwnds/_watched (best-effort, inside try/catch). So _ids is the only honest gate.
    private readonly ConcurrentDictionary<string, int> _ids = new();
    // Reverse map hwnd -> current wN, so list(includeHandles) polling reuses ids (mint-or-reuse) instead
    // of leaking a fresh handle per call. Populated/cleaned alongside _hwnds.
    private readonly ConcurrentDictionary<IntPtr, string> _byHwnd = new();
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

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() => ListWindowsAsync(false, false);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds) => ListWindowsAsync(includeBounds, false);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds, bool includeHandles) =>
        _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
        {
            PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
            // PURE Win32 — no UIA. A UIA Title/ProcessId read on the query STA blocks with no
            // timeout on ANY momentarily-unresponsive desktop window; Win32 GetWindowText does not.
            // includeHandles mints via LazyHandleFor (pure Win32 + dict writes) — still no UIA touch.
            var foreground = GetForegroundWindow();
            var list = new List<WindowInfo>(); int z = 0;
            foreach (var (hwnd, title, pid) in EnumTopLevel())
            {
                WindowBounds? b = null;
                if (includeBounds && GetWindowRect(hwnd, out var r))
                    b = new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                string? handle = includeHandles ? LazyHandleFor(hwnd, pid) : null;
                list.Add(new WindowInfo(title, SafeProcessName(pid), pid, hwnd == foreground, b,
                    includeBounds ? z : (int?)null, handle));
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
        _dispatcher.RunQueryAsync(() => func(ResolveWindow(handle)));

    /// <summary>Run a read callback on the query STA with the resolved window AND the Desktop
    /// element (the snapshot engine needs the Desktop to graft owner-process popups, which are
    /// children of the Desktop — not the target window). Reuses the stale-handle guard.</summary>
    public Task<T> RunWithWindowAndDesktopAsync<T>(WindowHandle handle, Func<Window, AutomationElement, T> func) =>
        _dispatcher.RunQueryAsync(() => func(ResolveWindow(handle), _automation.GetDesktop()));

    /// <summary>Resolve a handle to its UIA Window ON THE QUERY STA. Eager handles hit the cached
    /// _handles Window directly (unchanged). A lazily-minted handle (list includeHandles) has no _handles
    /// entry yet: bind it now from the recorded HWND — but ONLY after the M2 Win32 pid-reverify confirms
    /// the HWND still belongs to the recorded pid, so a recycled HWND can never inject UIA into a
    /// different process. Single query STA ⇒ the check-then-bind is race-free and GetOrAdd is safe.</summary>
    private Window ResolveWindow(WindowHandle handle)
    {
        if (_handles.TryGetValue(handle.Id, out var cached)) return cached;
        if (!_ids.TryGetValue(handle.Id, out var recordedPid)
            || !_hwnds.TryGetValue(handle.Id, out var hwnd) || hwnd == IntPtr.Zero)
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
        GetWindowThreadProcessId(hwnd, out uint curPid);
        if (!HwndStillOwnedBy(recordedPid, (int)curPid))
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} no longer refers to its original window (its HWND was recycled).",
                "re-list windows and re-open");
        var bound = _handles.GetOrAdd(handle.Id, _ => _automation.FromHandle(hwnd).AsWindow());
        // Close the Invalidate-vs-lazy-bind race (AGY-AFTER seat B): a ThreadPool proc.Exited can run
        // Invalidate BETWEEN the _ids check above and this GetOrAdd — consuming the exactly-once gate and
        // clearing _hwnds while _handles was still empty. Our GetOrAdd would then insert an ORPHANED COM
        // wrapper that (a) leaks (the gate is spent, so it is never evicted) AND (b) keeps resolving on the
        // fast _handles path above with NO pid-reverify, silently defeating the invalidation. Re-check the
        // gate after binding: if it is gone, evict our own insert and fail closed. (ids are monotonic /
        // never-reused, so removing our own orphan is unambiguous; TryRemove is idempotent vs Invalidate's.)
        if (!_ids.ContainsKey(handle.Id))
        {
            _handles.TryRemove(handle.Id, out _);
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} was invalidated during resolution.", "re-list windows and re-open");
        }
        return bound;
    }

    public void Invalidate(WindowHandle handle)
    {
        // Exactly-once gate on _ids — the ONLY dict written UNCONDITIONALLY by every mint path (eager
        // Register AND lazy list-mint). ConcurrentDictionary.TryRemove returns true to EXACTLY ONE caller
        // per key, so electing _ids as the sole gate makes WindowInvalidated fire at most once even under
        // concurrent invalidation (proc.Exited racing CloseAsync, or a sweep racing proc.Exited). _handles
        // is now a lazy COM cache (may be absent for a never-read lazy handle); _hwnds/_watched are
        // best-effort — none can serve as the gate. Remove all three unconditionally; do NOT let their
        // removal set `removed`. Capture the hwnd before removing _hwnds so the _byHwnd reverse entry is
        // cleaned (only if it still points at THIS id — a recycled hwnd may already point elsewhere).
        bool removed = _ids.TryRemove(handle.Id, out _);
        _handles.TryRemove(handle.Id, out _);
        if (_hwnds.TryRemove(handle.Id, out var hwnd))
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<IntPtr, string>>)_byHwnd)
                .Remove(new System.Collections.Generic.KeyValuePair<IntPtr, string>(hwnd, handle.Id));
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
        if (removed)
            // Swallow subscriber faults: this can fire on a raw ThreadPool thread (proc.Exited), where an
            // unhandled exception is process-fatal, and from CloseAsync's finally, where it would mask an
            // in-flight exception. Invalidation-path robustness > surfacing a subscriber bug here.
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

    /// <summary>Reuse a cached handle for an enumerated (hwnd,pid) only when the cached id's recorded pid
    /// equals the currently-enumerated pid. A mismatch means the OS recycled the HWND integer to a
    /// DIFFERENT process, so a fresh id must be minted. Pure/headless.</summary>
    internal static bool CanReuseHandle(int cachedPid, int enumeratedPid) => cachedPid == enumeratedPid;

    /// <summary>M2 pure identity check for lazy resolution: the HWND recorded at mint time must still
    /// belong to the recorded pid before UIA binds to it (a recycled HWND may now host a different,
    /// possibly sensitive process). The live GetWindowThreadProcessId read is the caller's; this is the
    /// pure decision. currentPidForHwnd==0 (dead/invalid hwnd) never matches a real recorded pid.</summary>
    internal static bool HwndStillOwnedBy(int recordedPid, int currentPidForHwnd) =>
        currentPidForHwnd == recordedPid;

    internal WindowHandle Register(Window window, int pid)
    {
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _ids[id] = pid;                 // unconditional gate/identity write (mirrors the lazy mint site)
        _handles[id] = window;
        try
        {
            var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            _hwnds[id] = hwnd;
            if (hwnd != IntPtr.Zero) _byHwnd[hwnd] = id;
        }
        catch { /* no hwnd */ }
        TryWatchProcessExit(id, pid);
        return new WindowHandle(id);
    }

    /// <summary>Lazy handle for `list includeHandles` (fork 1a). Reuse the existing wN for this HWND when
    /// its recorded pid still matches (same live window); else mint a fresh wN — first sight, or the HWND
    /// was recycled (drop the stale id first so refs/watches/wakes for the gone window are reclaimed).
    /// PURE Win32 + dict writes, NO UIA touch, so ListWindowsAsync stays non-blocking; the UIA Window is
    /// bound later, lazily, by ResolveWindow (with the M2 pid-reverify). Populates _hwnds so
    /// PruneClosedWindows reclaims this handle if its window closes without a process exit. Runs on the
    /// query STA (called from inside ListWindowsAsync).</summary>
    private string LazyHandleFor(IntPtr hwnd, int pid)
    {
        if (_byHwnd.TryGetValue(hwnd, out var existing)
            && _ids.TryGetValue(existing, out var cachedPid)
            && CanReuseHandle(cachedPid, pid))
            return existing;
        // Recycled (stale id bound to this hwnd but a different pid): reclaim the gone window's state.
        if (existing is not null) Invalidate(new WindowHandle(existing));
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _ids[id] = pid;
        _hwnds[id] = hwnd;
        _byHwnd[hwnd] = id;
        TryWatchProcessExit(id, pid);
        return id;
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

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
        // Capture the pid recorded at mint time for the M2 reverify done on the action STA below. If _ids
        // has no entry the handle was invalidated (its gate is gone) — fail closed.
        if (!_ids.TryGetValue(handle.Id, out var recordedPid))
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
        return _dispatcher.RunActionAsync(() =>
        {
            // M2 on the WRITE path (mirrors ResolveWindow's read-path guard): reverify the HWND still
            // belongs to the recorded pid IMMEDIATELY before binding UIA and acting on it. An HWND is
            // immutably owned by its creating process for the window's lifetime, so a differing current
            // owner pid proves the OS destroyed the original window and recycled the integer to a DIFFERENT
            // (possibly sensitive) process — acting on it would be cross-process injection. Lazy handles are
            // held/reused across long gaps, widening this recycle window, so the action path must guard it
            // too, not just the read path. Pure-Win32, no false-positive for a live window. A dead hwnd
            // yields curPid 0, which never matches a real recordedPid → fail closed. Run here (action STA,
            // right before FromHandle) to minimize the check-to-bind TOCTOU.
            GetWindowThreadProcessId(hwnd, out uint curPid);
            if (!HwndStillOwnedBy(recordedPid, (int)curPid))
                throw new ToolException(ToolErrorCode.WindowHandleStale,
                    $"Handle {handle.Id} no longer refers to its original window (its HWND was recycled).",
                    "re-list windows and re-open");
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
        try
        {
            await RunOnWindowAsync(handle, w =>
            {
                // Capture the target's hwnd + whether it OWNS the OS foreground BEFORE closing it, so we
                // can heal the keyboard-focus orphan that closing the foreground window causes: this server
                // is a BACKGROUND process, so Windows' auto-activation of the next window is unreliable
                // under foreground-lock and the human's keystrokes land nowhere until they click a window.
                IntPtr hwnd = IntPtr.Zero;
                try { hwnd = w.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
                bool wasForeground = hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
                w.Close();
                if (wasForeground) RestoreForegroundAfterClose(hwnd);
                return true;
            });
        }
        catch (ToolException) { /* already gone */ }
        finally { Invalidate(handle); }
    }

    /// <summary>Best-effort keyboard-focus restoration after closing the OS-foreground window. The Win32
    /// foreground-lock (which defeats a background process's SetForegroundWindow) only relaxes once the
    /// closed window is actually DESTROYED — and UIA <c>Window.Close()</c> is async, returning before
    /// destruction — so spin-wait (bounded) for <c>!IsWindow</c> before claiming foreground for the next
    /// Z-ordered top-level. Runs on the query STA; uses pure Win32 (no UIA). NEVER throws: the close has
    /// already succeeded, so a failed refocus must not surface as an error to the agent. Deliberately does
    /// NOT use AttachThreadInput (deadlock-prone against a thread hanging during shutdown).</summary>
    private void RestoreForegroundAfterClose(IntPtr closedHwnd)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (IsWindow(closedHwnd) && DateTime.UtcNow < deadline) Thread.Sleep(25);
            var next = PickForegroundFallback(EnumTopLevel(), closedHwnd);
            if (next != IntPtr.Zero) SetForegroundWindow(next);
        }
        catch { /* focus restoration is best-effort; the window is already closed */ }
    }

    /// <summary>Pick the window to give foreground after a close: the first Z-ordered top-level that is
    /// NOT the one we just closed. <see cref="EnumTopLevel"/> already filters to visible, titled,
    /// non-sentinel windows in Z-order (Alt+Tab parity), so this only has to drop the closed hwnd. Pure
    /// over its input (no Win32) -> headless-testable.</summary>
    internal static IntPtr PickForegroundFallback(IReadOnlyList<(IntPtr Hwnd, string Title, int Pid)> zorder, IntPtr closedHwnd)
    {
        foreach (var w in zorder)
            if (w.Hwnd != closedHwnd) return w.Hwnd;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var p in _watched.Values) try { p.Dispose(); } catch { }
        _dispatcher.RunQueryAsync(() => _automation.Dispose()).GetAwaiter().GetResult();
    }
}
