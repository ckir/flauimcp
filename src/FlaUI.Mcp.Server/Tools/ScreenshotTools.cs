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

    [McpServerTool(ReadOnly = true), Description("Capture a window, an element (window+ref), or the full virtual desktop as a PNG. Returns a native image block + JSON metadata {bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated}. Redacted elements (OS password fields, or an operator rule) are masked at capture time, full-desktop included (window/element scope covers popups; full-desktop is refused if a denylisted credential window is visible — capture a specific window instead). If a redacted element cannot report usable bounds its mask is taken from an ancestor: maskEscalations counts those ELEMENTS (not levels climbed) and escalated lists their {automationId,controlType} so you can tell which control has a broken provider. If no ancestor is usable either, the capture is REFUSED with RedactionUnmaskable rather than returning an all-black image - retry once the UI settles, or capture a different window. NOTE redactions counts rects PAINTED, so when several elements escalate to the SAME ancestor it exceeds the number of distinct black regions you can see; compare it against maskEscalations rather than reading it as a control count. output must be 'inline' (file→NotImplemented). Focus the window first (no occlusion handling). Minimized→ElementNotActionable. Width is clamped to 1920.")]
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
            IReadOnlyList<MaskEscalationEntry> escalations;
            if (string.IsNullOrEmpty(window))
            {
                var present = await _perception.DenylistedWindowsVisibleAsync();
                if (present)
                    throw new ToolException(ToolErrorCode.TargetDenied, "A credential/denylisted window is currently visible; full-desktop capture is refused.", "capture a specific non-sensitive window: desktop_screenshot window=<handle>");
                var vbounds = ScreenCapture.VirtualScreenBounds();
                // DEF-2: this passed Array.Empty<Rectangle>() — full-desktop capture masked NOTHING, even
                // though window- and element-scoped capture both masked correctly. The refusal above only
                // covers DENYLISTED windows; an ordinary window holding a password field was photographed
                // in the clear.
                var desk = await _perception.AllMaskRectsAsync();
                escalations = desk.Escalations;
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(vbounds, desk.Rects, maxWidth));
            }
            else
            {
                var geo = await _perception.ResolveWindowCaptureGeometryAsync(new WindowHandle(window!), @ref);
                if (geo.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"Capturing windows owned by '{geo.DeniedProcess}' is blocked.", "capture a non-sensitive window");
                if (geo.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");
                escalations = geo.Escalations;
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.Bounds, geo.MaskRects, maxWidth));
            }
            var dpi = DpiHelper.ScaleForPoint(result.X, result.Y);
            // A1: maskEscalations counts ELEMENTS whose own rect was unusable and whose mask therefore came
            // from an ancestor — NOT levels climbed. One element climbing three levels counts as 1. Both
            // fields are ALWAYS present (0 / empty when nothing escalated), because a diagnostic that
            // appears only on failure teaches a consumer to ignore its absence.
            //
            // `escalated` carries automationId + controlType and NEVER the Name: a bare count is not a
            // diagnostic — an operator seeing a giant black box and maskEscalations:2 cannot tell which
            // control has the broken provider — while the Name is the identity the mask exists to hide.
            // Both fields are already published for redacted elements by desktop_find, so this surfaces
            // nothing the query path does not.
            return ToolResponse.Image(result.Png, new
            {
                bounds = new { x = result.X, y = result.Y, w = result.W, h = result.H },
                dpiScale = dpi,
                scaleApplied = result.ScaleApplied,
                redactions = result.Redactions,
                maskEscalations = escalations.Count,
                escalated = escalations.Select(e => new { automationId = e.AutomationId, controlType = e.ControlType })
            });
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
