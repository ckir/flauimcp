using System.ComponentModel;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Perception.Geometry;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ScreenshotTools
{
    private readonly PerceptionManager _perception;
    public ScreenshotTools(PerceptionManager perception) => _perception = perception;

    [McpServerTool(ReadOnly = true), Description("Capture a window, an element (window+ref), or the full virtual desktop as a PNG. Returns a native image block + JSON metadata {bounds,dpiScale,scaleApplied,redactions}. Password fields are redacted at capture time (window/element scope covers popups; full-desktop is refused if a denylisted credential window is visible — capture a specific window instead). output must be 'inline' (file→NotImplemented). Focus the window first (no occlusion handling). Minimized→ElementNotActionable. Width is clamped to 1920.")]
    public Task<CallToolResult> DesktopScreenshot(
        [Description("Window handle, e.g. w1. Omit (and omit ref) for the full virtual desktop.")] string? window = null,
        [Description("Element ref to capture (requires window).")] string? @ref = null,
        [Description("Only 'inline' is implemented (default). 'file' returns NotImplemented.")] string output = "inline",
        [Description("Downscale so width <= maxWidth (default 1600; 0 disables, but a hard 1920 ceiling always applies).")] int maxWidth = 1600)
        => ToolResponse.GuardImage(async () =>
        {
            if (output != "inline")
                throw new ToolException(ToolErrorCode.NotImplemented, "Only output:'inline' is supported in v0.4.0.", "omit output or pass output:'inline'");
            if (!ScreenCapture.IsDesktopRenderable())
                throw new ToolException(ToolErrorCode.CaptureUnavailable, "The desktop session is disconnected or locked.", "reconnect to restore rendering");

            CaptureResult result;
            if (string.IsNullOrEmpty(window))
            {
                var present = await _perception.DenylistedWindowsVisibleAsync();
                if (present)
                    throw new ToolException(ToolErrorCode.TargetDenied, "A credential/denylisted window is currently visible; full-desktop capture is refused.", "capture a specific non-sensitive window: desktop_screenshot window=<handle>");
                var vbounds = ScreenCapture.VirtualScreenBounds();
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(vbounds, System.Array.Empty<System.Drawing.Rectangle>(), maxWidth));
            }
            else
            {
                var geo = await _perception.ResolveWindowCaptureGeometryAsync(new WindowHandle(window!), @ref);
                if (geo.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"Capturing windows owned by '{geo.DeniedProcess}' is blocked.", "capture a non-sensitive window");
                if (geo.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.Bounds, geo.PasswordRects, maxWidth));
            }
            var dpi = DpiHelper.ScaleForPoint(result.X, result.Y);
            return ToolResponse.Image(result.Png, new { bounds = new { x = result.X, y = result.Y, w = result.W, h = result.H }, dpiScale = dpi, scaleApplied = result.ScaleApplied, redactions = result.Redactions });
        });

    [McpServerTool(ReadOnly = true), Description("Get an element's absolute physical-pixel screen bounds {x,y,w,h} (signed, multi-monitor safe), its monitor dpiScale (informational), and isOffscreen (UIA scrolled/virtualized-out, NOT occlusion).")]
    public Task<string> DesktopGetBounds(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref)
        => ToolResponse.Guard(async () =>
        {
            var r = await _perception.RunOnRefAsync(new WindowHandle(window), @ref, el =>
            {
                var rect = el.BoundingRectangle;
                return (rect.X, rect.Y, rect.Width, rect.Height, el.Properties.IsOffscreen.ValueOrDefault);
            });
            var dpi = DpiHelper.ScaleForPoint(r.X, r.Y);
            return ToolResponse.Ok(new { bounds = new { x = r.X, y = r.Y, w = r.Width, h = r.Height }, dpiScale = dpi, isOffscreen = r.Item5 });
        });
}
