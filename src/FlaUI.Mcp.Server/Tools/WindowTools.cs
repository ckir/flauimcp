using System.ComponentModel;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class WindowTools
{
    private readonly WindowManager _windows;

    public WindowTools(WindowManager windows) => _windows = windows;

    [McpServerTool, Description("List top-level desktop windows with title, process, and pid.")]
    public Task<string> DesktopListWindows() => ToolResponse.Guard(async () =>
        ToolResponse.Ok(await _windows.ListWindowsAsync()));

    [McpServerTool, Description("Open a window by pid or title and return its handle (e.g. w1).")]
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

    [McpServerTool, Description("Launch an app and return a handle to its main window.")]
    public Task<string> DesktopLaunchApp(
        [Description("Executable path.")] string path,
        [Description("Optional arguments.")] string? args = null,
        [Description("Max ms to wait for a titled window.")] int timeoutMs = 10000)
        => ToolResponse.Guard(async () =>
        {
            var (handle, pid) = await _windows.LaunchAppAsync(path, args, timeoutMs);
            return ToolResponse.Ok(new { handle = handle.Id, pid });
        });

    [McpServerTool, Description("Bring a window to the foreground.")]
    public Task<string> DesktopFocusWindow([Description("Window handle, e.g. w1.")] string window)
        => ToolResponse.Guard(async () => { await _windows.FocusAsync(new WindowHandle(window)); return ToolResponse.Ok(new { ok = true }); });

    [McpServerTool, Description("Close a window and free its handle.")]
    public Task<string> DesktopCloseWindow([Description("Window handle, e.g. w1.")] string window)
        => ToolResponse.Guard(async () => { await _windows.CloseAsync(new WindowHandle(window)); return ToolResponse.Ok(new { ok = true }); });
}
