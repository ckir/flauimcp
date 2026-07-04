using System.ComponentModel;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class WindowTools
{
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;

    public WindowTools(WindowManager windows, ServerOptions options) { _windows = windows; _options = options; }

    [McpServerTool(ReadOnly = true), Description("List top-level desktop windows (Title, ProcessName, Pid, IsForeground). Opt-in includeBounds adds absolute physical-px Bounds + ZOrder (0=topmost, for occlusion reasoning). Opt-in includeHandles adds a reusable handle (e.g. w1) to each window so you can snapshot/find/interact directly, skipping a separate desktop_open_window call. Pure Win32 — never blocks on an unresponsive window. For per-window control counts, open a window and call desktop_snapshot_stats.")]
    public Task<string> DesktopListWindows(
        [Description("Add Bounds + ZOrder to each window (default false).")] bool includeBounds = false,
        [Description("Add a reusable handle (wN) to each window, so you can act/read without desktop_open_window (default false).")] bool includeHandles = false)
        => ToolResponse.Guard(async () => ToolResponse.Ok(await _windows.ListWindowsAsync(includeBounds, includeHandles)));

    // Read-only of the environment: resolves a handle only (no focus/render/launch). Marked ReadOnly
    // rather than renamed — rename would break the shipped v0.1.x tool name.
    [McpServerTool(ReadOnly = true), Description("Open a window by pid or title and return its handle (e.g. w1).")]
    public Task<string> DesktopOpenWindow(
        [Description("Selector kind: \"pid\" or \"title\".")] string by,
        [Description("The pid (as text) or the exact window title.")] string value)
        => ToolResponse.Guard(async () =>
        {
            var handle = by switch
            {
                "pid"   => await _windows.OpenByPidAsync(int.Parse(value)),
                "title" => await _windows.OpenByTitleAsync(value),
                _ => throw new ToolException(ToolErrorCode.WindowNotFound,
                        $"Unknown selector '{by}'.", "use by=pid or by=title")
            };
            return ToolResponse.Ok(new { handle = handle.Id });
        });

    [McpServerTool(Destructive = true), Description("Launch an app and return a handle to its main window. Blocked in --read-only-mode.")]
    public Task<string> DesktopLaunchApp(
        [Description("Executable path.")] string path,
        [Description("Optional arguments.")] string? args = null,
        [Description("Max ms to wait for a titled window.")] int timeoutMs = 10000)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            var (handle, pid) = await _windows.LaunchAppAsync(path, args, timeoutMs);
            return ToolResponse.Ok(new { handle = handle.Id, pid });
        });

    [McpServerTool(Destructive = true), Description("Bring a window to the foreground. Blocked in --read-only-mode.")]
    public Task<string> DesktopFocusWindow([Description("Window handle, e.g. w1.")] string window)
        => ToolResponse.GuardWrite(_options, async () => { await _windows.FocusAsync(new WindowHandle(window)); return ToolResponse.Ok(new { ok = true }); });

    [McpServerTool(Destructive = true), Description("Close a window and free its handle. Blocked in --read-only-mode.")]
    public Task<string> DesktopCloseWindow([Description("Window handle, e.g. w1.")] string window)
        => ToolResponse.GuardWrite(_options, async () => { await _windows.CloseAsync(new WindowHandle(window)); return ToolResponse.Ok(new { ok = true }); });
}
