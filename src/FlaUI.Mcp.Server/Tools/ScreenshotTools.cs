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
    private readonly FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator _coordinator;
    private readonly FlaUI.Mcp.Server.Capture.CaptureAuditSignal _auditSignal;

    public ScreenshotTools(PerceptionManager perception,
                           FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator coordinator,
                           FlaUI.Mcp.Server.Capture.CaptureAuditSignal auditSignal)
    {
        _perception = perception;
        _coordinator = coordinator;
        _auditSignal = auditSignal;
    }

    [McpServerTool(ReadOnly = true), Description("Capture a window, an element (window+ref), or the full virtual desktop as a PNG. Returns a native image block + JSON metadata {bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses,captureMethod,captureWarnings}. captureMethod: 'printWindow' = the window's OWN pixels (occlusion irrelevant); 'screenScrape' = the screen (overlaps are IN the image). Window/element falls back to screenScrape if the target is unresponsive or moving, so CHECK it, never assume it; a fallback re-walks the WHOLE desktop for masks and costs SECONDS. Full-desktop is always screenScrape. printWindow pixels are NOT clickable (the window may be occluded or off-screen): act via desktop_snapshot + desktop_click ref. An occluded Chromium window can suspend rendering: printWindow then returns its last painted frame, possibly very old; re-capturing will not wake it - desktop_focus_window first if freshness matters. Redacted elements (OS password fields or an operator rule) are masked at capture time, full-desktop included. If a redacted element cannot report usable bounds its mask comes from an ancestor: maskEscalations counts those ELEMENTS (not levels climbed); escalated lists their {automationId,controlType}, naming the control with a broken provider. If no ancestor works either, the capture is REFUSED with RedactionUnmaskable rather than returning an all-black image - retry once the UI settles. unmaskedProcesses lists processes whose windows contributed NO masks; on a screenScrape fallback it, maskEscalations and escalated cover the WHOLE DESKTOP and may name processes with no pixels here - empty still means fully redacted. captureWarnings is ALWAYS present (empty when fine); entries are {code,recourse}.")]
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
            // AB-9. Only the full-desktop path can SKIP a window and carry on; a window- or element-scoped
            // capture names its target, so a failure to resolve it throws rather than degrading. Empty on
            // that branch is therefore the truth, not a placeholder.
            IReadOnlyList<string> unmaskedProcesses;
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
                unmaskedProcesses = desk.UnmaskedProcesses;
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(
                    vbounds, desk.Rects, maxWidth, CaptureScope.FullDesktop,
                    System.Array.Empty<CaptureWarning>()));
            }
            else
            {
                // The coordinator owns the walk, the retry loop, the bookend validation walk and the
                // scrape fallbacks (canonical steps 1-9). The geometry-time refusals it raises are the
                // SAME ones this method used to raise inline at :54 and :55.
                var scope = string.IsNullOrEmpty(@ref) ? CaptureScope.Window : CaptureScope.Element;
                var outcome = await _coordinator.CaptureAsync(new WindowHandle(window!), @ref, scope, maxWidth);
                result = outcome.Result;
                // ⚠ From the OUTCOME, not the geometry. On a fallback scrape the masks come from the
                // DESKTOP walk, so the target walk's escalations would describe a different mask set from
                // the one painted. *(Driver's solo pass, round 7.)*
                escalations = outcome.Escalations;
                // ⚠ NOT hardcoded empty. On the PrintWindow path this IS empty and that is the truth --
                // the image contains one window and its own mask set covered it. On a FALLBACK SCRAPE the
                // image contains whatever overlapped the target, and the coordinator's desktop mask walk
                // is what fills this in. Hardcoding empty there told the agent "everything needing masking
                // was masked" while background windows sat unmasked in the pixels.
                // *(AGY-AFTER panel over this plan, round 6, Guard-Consistency Auditor.)*
                unmaskedProcesses = outcome.UnmaskedProcesses;

                // §2.6, canonical step 10. AFTER the result is final, never before: a signal raised
                // earlier can appear in the captured pixels on the scrape path.
                _auditSignal.SignalIfOcclusionBypassed(new WindowHandle(window!),
                                                       outcome.Geometry.NativeWindowHandle,
                                                       result.CaptureMethod);
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
                escalated = escalations.Select(e => new { automationId = e.AutomationId, controlType = e.ControlType }),
                // AB-9: ALWAYS present, empty when nothing was skipped — same reasoning as the two fields
                // above. A NON-EMPTY list means this image is NOT fully redacted: those processes' windows
                // are in the pixels and their redactions are not. Full-desktop only; a named window that
                // cannot be resolved throws instead of degrading.
                unmaskedProcesses,
                // Which backend produced these pixels. The two scopes now use different mechanisms and a
                // caller comparing images needs to know which it holds. camelCase, matching every other
                // field here.
                captureMethod = result.CaptureMethod,
                // ALWAYS present, EMPTY in the normal case — the unmaskedProcesses idiom, for the same
                // AB-9 reason: a diagnostic that appears only on failure teaches consumers to ignore its
                // absence. Entries are {code, recourse} objects: `code` is what you branch on, `recourse`
                // is what to do instead.
                captureWarnings = result.CaptureWarnings.Select(w => new { code = w.Code, recourse = w.Recourse }),
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
