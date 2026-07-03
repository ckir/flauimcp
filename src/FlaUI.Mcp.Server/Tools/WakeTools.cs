using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Watch;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Prong A MCP surface (§3/§4): activate + HOLD a Chromium/Electron window's native UIA tree so
/// desktop_snapshot/find/interact work on it with full precision. ReadOnly + lease-exempt (observation only —
/// synthesizes no input). Waking is a held registration; it auto-releases when the window closes.</summary>
[McpServerToolType]
public sealed class WakeTools
{
    private readonly WindowManager _windows;
    private readonly WakeService _wake;
    public WakeTools(WindowManager windows, WakeService wake) { _windows = windows; _wake = wake; }

    [McpServerTool(ReadOnly = true), Description(
        "Activate and HOLD an opaque Chromium/Electron window's native accessibility tree so desktop_snapshot / " +
        "desktop_find / interaction tools can see and target its contents with full precision. Use when " +
        "desktop_snapshot returns one big empty Pane AND its 'wakeable' hint is true (VS Code, Slack, Teams, " +
        "Discord, Chrome). Returns {wakeId, window, alreadyAwake}. The wake is HELD until desktop_release_" +
        "accessibility or the window closes; while held, re-snapshot to get the hydrated tree. Idempotent: waking " +
        "an already-awake window returns the existing wakeId (alreadyAwake:true). NOTE: an editor's document TEXT " +
        "may stay behind a screen-reader gate even when woken — use desktop_find_text for that. ReadOnly + " +
        "lease-exempt. Caps: 32 wakes/session (TooManyWatches).")]
    public Task<string> DesktopWakeAccessibility(
        [Description("Window handle, e.g. w1.")] string window)
        => ToolResponse.Guard(async () =>
        {
            // Idempotent reuse: already awake -> return the held wake id.
            var existing = _wake.ActiveWakeFor(window);
            if (existing is not null)
                return ToolResponse.Ok(new { wakeId = existing, window, alreadyAwake = true });

            // Deny-list + PID read on the query STA (mirrors WatchService.WatchAsync guard).
            var pid = await _windows.RunWithWindowAndDesktopAsync(new WindowHandle(window), (win, desktop) =>
            {
                var procName = SafeProcessName(win);
                if (PerceptionPolicy.IsDenied(procName))
                    throw new ToolException(ToolErrorCode.TargetDenied,
                        $"Waking windows owned by '{procName}' is blocked (credential store).",
                        "wake a different, non-sensitive window");
                int p = -1;
                try { p = win.Properties.ProcessId.ValueOrDefault; } catch { }
                return p;
            });

            var wakeId = await _wake.WakeAsync(window, pid);
            return ToolResponse.Ok(new { wakeId, window, alreadyAwake = false });
        });

    [McpServerTool(ReadOnly = true), Description(
        "Release a held accessibility wake from desktop_wake_accessibility. The wake is no longer held, so Chromium " +
        "re-collapses the window's tree to opaque lazily once it goes idle (not necessarily immediately). " +
        "Idempotent: an unknown/already-released wakeId returns ok:true.")]
    public Task<string> DesktopReleaseAccessibility(
        [Description("The wakeId from desktop_wake_accessibility, e.g. k1.")] string wakeId)
        => ToolResponse.Guard(async () =>
        {
            await _wake.ReleaseAsync(wakeId);
            return ToolResponse.Ok(new { ok = true, wakeId });
        });

    [McpServerTool(ReadOnly = true), Description(
        "List your active accessibility wakes (recover them after a context loss). Returns " +
        "wakes[{wakeId, window}].")]
    public Task<string> DesktopListWakes()
        => ToolResponse.Guard(() =>
        {
            var wakes = _wake.List().Select(w => new { wakeId = w.WakeId, window = w.WindowId });
            return Task.FromResult(ToolResponse.Ok(new { wakes }));
        });

    // Mirrors WatchService.SafeProcessName.
    private static string? SafeProcessName(AutomationElement el)
    {
        int pid;
        try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { pid = -1; }
        if (pid < 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }
}
