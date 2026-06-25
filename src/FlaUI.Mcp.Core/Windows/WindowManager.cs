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
            var match = _automation.GetDesktop().FindAllChildren()
                .Select(c => c.AsWindow())
                .FirstOrDefault(w => w.Properties.ProcessId.ValueOrDefault == pid
                                     && !string.IsNullOrEmpty(w.Title))
                ?? throw new ToolException(ToolErrorCode.WindowNotFound,
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

    public void Invalidate(WindowHandle handle)
    {
        _handles.TryRemove(handle.Id, out _);
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
    }

    internal WindowHandle Register(Window window, int pid)
    {
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _handles[id] = window;
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

    public void Dispose()
    {
        foreach (var p in _watched.Values) try { p.Dispose(); } catch { }
        _dispatcher.RunQueryAsync(() => _automation.Dispose()).GetAwaiter().GetResult();
    }
}
