using System.ComponentModel;
using System.Text.Json;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class WindowTools
{
    private readonly WindowManager _windows;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public WindowTools(WindowManager windows) => _windows = windows;

    [McpServerTool, Description("List top-level desktop windows with title, process, and pid.")]
    public Task<string> DesktopListWindows() => Guard(async () =>
        Ok(await _windows.ListWindowsAsync()));

    [McpServerTool, Description("Open a window by pid or title and return its handle (e.g. w1).")]
    public Task<string> DesktopOpenWindow(
        [Description("Selector kind: \"pid\" or \"title\".")] string by,
        [Description("The pid (as text) or the exact window title.")] string value)
        => Guard(async () =>
        {
            var handle = by switch
            {
                "pid"   => await _windows.OpenByPidAsync(int.Parse(value)),
                "title" => await _windows.OpenByTitleAsync(value),
                _ => throw new ToolException(ToolErrorCode.WindowNotFound,
                        $"Unknown selector '{by}'.", "use by=pid or by=title")
            };
            return Ok(new { handle = handle.Id });
        });

    [McpServerTool, Description("Launch an app and return a handle to its main window.")]
    public Task<string> DesktopLaunchApp(
        [Description("Executable path.")] string path,
        [Description("Optional arguments.")] string? args = null,
        [Description("Max ms to wait for a titled window.")] int timeoutMs = 10000)
        => Guard(async () =>
        {
            var (handle, pid) = await _windows.LaunchAppAsync(path, args, timeoutMs);
            return Ok(new { handle = handle.Id, pid });
        });

    [McpServerTool, Description("Bring a window to the foreground.")]
    public Task<string> DesktopFocusWindow([Description("Window handle, e.g. w1.")] string window)
        => Guard(async () => { await _windows.FocusAsync(new WindowHandle(window)); return Ok(new { ok = true }); });

    [McpServerTool, Description("Close a window and free its handle.")]
    public Task<string> DesktopCloseWindow([Description("Window handle, e.g. w1.")] string window)
        => Guard(async () => { await _windows.CloseAsync(new WindowHandle(window)); return Ok(new { ok = true }); });

    private static string Ok(object payload) => JsonSerializer.Serialize(payload, Json);

    private static async Task<string> Guard(Func<Task<string>> body)
    {
        try { return await body(); }
        catch (ToolException ex)
        {
            return JsonSerializer.Serialize(
                new { error = ex.Code.ToString(), message = ex.Message, suggestedRecovery = ex.SuggestedRecovery }, Json);
        }
        catch (Exception ex)
        {
            // Unexpected (e.g. FormatException on a bad pid, or a COM error) — map at the
            // boundary so a single bad call never kills the process or escapes unmapped.
            return JsonSerializer.Serialize(
                new { error = "INTERNAL", message = ex.Message, suggestedRecovery = (string?)"re-check arguments and retry" }, Json);
        }
    }
}
