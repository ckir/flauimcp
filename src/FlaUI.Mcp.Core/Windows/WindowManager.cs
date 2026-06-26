using System.Collections.Concurrent;
using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.UIA3;

namespace FlaUI.Mcp.Core.Windows;

public sealed record WindowInfo(string Title, string ProcessName, int Pid, bool IsForeground);

public sealed class WindowManager : IDisposable
{
    private readonly AutomationDispatcher _dispatcher;
    private readonly UIA3Automation _automation;
    private readonly ConcurrentDictionary<string, Window> _handles = new();
    private readonly ConcurrentDictionary<string, IntPtr> _hwnds = new();
    private readonly ConcurrentDictionary<string, Process> _watched = new();
    private int _counter;

    public WindowManager(AutomationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // UIA objects must be created on the STA thread that uses them.
        _automation = _dispatcher.RunQueryAsync(() => new UIA3Automation()).GetAwaiter().GetResult();
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() =>
        _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
        {
            var desktop = _automation.GetDesktop();
            var focused = _automation.FocusedElement();
            var list = new List<WindowInfo>();
            foreach (var child in desktop.FindAllChildren())
            {
                var w = child.AsWindow();
                if (string.IsNullOrEmpty(w.Title)) continue;
                int pid = w.Properties.ProcessId.ValueOrDefault;
                bool fg = focused != null && focused.Properties.ProcessId.ValueOrDefault == pid;
                list.Add(new WindowInfo(w.Title, SafeProcessName(pid), pid, fg));
            }
            return list;
        });

    public Task<WindowHandle> OpenByPidAsync(int pid) =>
        _dispatcher.RunQueryAsync(() =>
        {
            // A just-launched app's top-level window isn't always enumerable the instant the process
            // goes input-idle. Poll briefly so launch->open (and slow-rendering apps under load) don't
            // spuriously fail with "No window for pid".
            var deadline = DateTime.UtcNow.AddSeconds(12);
            Window? match;
            while (true)
            {
                match = _automation.GetDesktop().FindAllChildren()
                    .Select(c => c.AsWindow())
                    .FirstOrDefault(w => w.Properties.ProcessId.ValueOrDefault == pid
                                         && !string.IsNullOrEmpty(w.Title));
                if (match != null || DateTime.UtcNow >= deadline) break;
                Thread.Sleep(100);
            }
            if (match is null)
                throw new ToolException(ToolErrorCode.WindowNotFound,
                    $"No window for pid {pid}.", "re-list windows");
            return Register(match, pid);
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
        _handles.TryRemove(handle.Id, out _);
        _hwnds.TryRemove(handle.Id, out _);
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
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
            var match = _automation.GetDesktop().FindAllChildren()
                .Select(c => c.AsWindow())
                .FirstOrDefault(w => w.Properties.ProcessId.ValueOrDefault == pid
                                     && !string.IsNullOrEmpty(w.Title));
            return match is null ? (WindowHandle?)null : Register(match, pid);
        });

    private Task<(WindowHandle handle, int pid)?> TryOpenByMatchingNewPidQuiet(
        HashSet<int> preExistingPids, string expectedProcessName) =>
        _dispatcher.RunQueryAsync<(WindowHandle, int)?>(() =>
        {
            foreach (var child in _automation.GetDesktop().FindAllChildren())
            {
                var w = child.AsWindow();
                if (string.IsNullOrEmpty(w.Title)) continue;
                int pid = w.Properties.ProcessId.ValueOrDefault;
                if (preExistingPids.Contains(pid)) continue;
                if (!LaunchedWindowMatcher.IsExpectedApp(expectedProcessName, SafeProcessName(pid))) continue;
                return (Register(w, pid), pid);
            }
            return ((WindowHandle, int)?)null;
        });

    public Task<WindowHandle> OpenByTitleAsync(string title) =>
        _dispatcher.RunQueryAsync(() =>
        {
            var matches = _automation.GetDesktop().FindAllChildren()
                .Select(c => c.AsWindow())
                .Where(w => w.Title == title)
                .ToList();
            if (matches.Count == 0)
                throw new ToolException(ToolErrorCode.WindowNotFound, $"No window titled '{title}'.", "re-list windows");
            if (matches.Count > 1)
            {
                var candidates = string.Join("; ", matches.Select(m =>
                    $"pid={m.Properties.ProcessId.ValueOrDefault} bounds={m.BoundingRectangle}"));
                throw new ToolException(ToolErrorCode.AmbiguousMatch,
                    $"{matches.Count} windows titled '{title}': {candidates}",
                    "re-open by:pid using one of the listed pids");
            }
            var win = matches[0];
            return Register(win, win.Properties.ProcessId.ValueOrDefault);
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
