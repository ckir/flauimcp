// src/FlaUI.Mcp.Server/Tools/FindTextTools.cs  (desktop_find_text; desktop_wait_for_text added in Task 11)
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Vision;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Prong B MCP surface (§3/§6): OCR a window/region and return every text run matching a query with
/// coordinates in BOTH physical screen px and desktop_click_at window-fractions. ReadOnly + lease-exempt.</summary>
[McpServerToolType]
public sealed class FindTextTools
{
    private readonly PerceptionManager _perception;
    private readonly TextFinder _finder;
    public FindTextTools(PerceptionManager perception, TextFinder finder)
    { _perception = perception; _finder = finder; }

    [McpServerTool(ReadOnly = true), Description(
        "OCR a window (or a sub-region of it) and return every visible text run matching your query, with click " +
        "coordinates. Use for opaque/canvas/game surfaces or an editor's text body where UIA can't see the text. " +
        "Returns {matches:[{text, bounds:[x,y,w,h] (physical screen px), center:[x,y] (screen px), xPct, yPct " +
        "(window fractions for desktop_click_at), confidence}]}, best match first. matchMode defaults to 'fuzzy' " +
        "(OCR mis-reads UI text - 'Submit' may read '5ubmit'); pass 'exact' to require an exact normalized match. " +
        "region (optional) is window-relative FRACTIONS [xPct,yPct,wPct,hPct] in [0,1] to OCR only part of the " +
        "window. IMPORTANT: a fuzzy query can match inside body text ('Click Submit below') - inspect each match's " +
        "text+bounds (or the screenshot) before desktop_click_at. all defaults true (return every occurrence so " +
        "you pick the right one). ReadOnly + lease-exempt. OcrUnavailable if no OCR language pack is installed.")]
    public Task<string> DesktopFindText(
        [Description("Text to find (fuzzy by default).")] string query,
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Optional window-relative region fractions [xPct,yPct,wPct,hPct] in [0,1].")] double[]? region = null,
        [Description("'fuzzy' (default) or 'exact'.")] string matchMode = "fuzzy",
        [Description("Return all matches (default true) or only the best.")] bool all = true)
        => ToolResponse.Guard(async () =>
        {
            var mode = matchMode.Equals("exact", System.StringComparison.OrdinalIgnoreCase) ? MatchMode.Exact : MatchMode.Fuzzy;
            // Resolve capture geometry (capture rect + full window rect) on the STA + deny-list; capture OFF the STA.
            var geo = await _perception.ResolveTextCaptureGeometryAsync(new WindowHandle(window), region); // Step 0 shape
            if (geo.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"OCR of windows owned by '{geo.DeniedProcess}' is blocked.", "target a non-sensitive window");
            if (geo.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");
            var cap = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.CaptureBounds, geo.PasswordRects, maxWidth: 0)); // maxWidth:0 -> best OCR accuracy (still 1920-clamped)
            var matches = await _finder.FindAsync(query, cap.Png, mode, all,
                cap.ScaleApplied, cap.X, cap.Y, geo.WindowLeft, geo.WindowTop, geo.WindowWidth, geo.WindowHeight);
            return ToolResponse.Ok(new
            {
                matches = matches.Select(m => new
                {
                    text = m.Text, confidence = m.Confidence,
                    bounds = new[] { m.BoundsX, m.BoundsY, m.BoundsW, m.BoundsH },
                    center = new[] { m.CenterX, m.CenterY },
                    xPct = m.XPct, yPct = m.YPct
                })
            });
        });
}
