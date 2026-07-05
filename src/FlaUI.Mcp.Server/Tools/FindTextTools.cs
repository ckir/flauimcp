// src/FlaUI.Mcp.Server/Tools/FindTextTools.cs  (desktop_find_text; desktop_wait_for_text added in Task 11)
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Vision;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Prong B MCP surface (§3/§6): OCR a window/region and return every text run matching a query with
/// coordinates in BOTH physical screen px and desktop_click_at window-fractions. ReadOnly + lease-exempt.
/// Also hosts desktop_wait_for_foreground (SP-A T8) — grouped with the other DesktopWaitFor* siblings for
/// naming consistency, even though its deps (foreground waiter/attention/window manager) differ from OCR.</summary>
[McpServerToolType]
public sealed class FindTextTools
{
    private readonly PerceptionManager _perception;
    private readonly TextFinder _finder;
    private readonly IForegroundWaiter _foregroundWaiter;
    private readonly WaitForForeground.WaiterGate _waiterGate;
    private readonly IAttentionSignal _attention;
    private readonly WindowManager _windows;
    private readonly IPlatformEnvironment _env;

    public FindTextTools(PerceptionManager perception, TextFinder finder,
        IForegroundWaiter foregroundWaiter, WaitForForeground.WaiterGate waiterGate,
        IAttentionSignal attention, WindowManager windows, IPlatformEnvironment env)
    {
        _perception = perception; _finder = finder;
        _foregroundWaiter = foregroundWaiter; _waiterGate = waiterGate;
        _attention = attention; _windows = windows; _env = env;
    }

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

    [McpServerTool(ReadOnly = true), Description(
        "Poll a window (or region) with OCR until visible text matching your query appears, or timeout. Use to " +
        "wait for an opaque/canvas surface to render text UIA can't see (desktop_wait_for is the UIA equivalent). " +
        "Fuzzy match. Timeout returns {satisfied:false} (NOT an error). On success returns {satisfied:true, match:" +
        "{text,bounds,center,xPct,yPct,confidence}}. OCR is heavy, so polling is throttled to >= 750ms between " +
        "passes; pick a timeout accordingly. ReadOnly + lease-exempt. OcrUnavailable if no OCR language pack.")]
    public Task<string> DesktopWaitForText(
        [Description("Text to wait for (fuzzy).")] string query,
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Optional window-relative region fractions [xPct,yPct,wPct,hPct] in [0,1].")] double[]? region = null,
        [Description("Total wait budget ms (default 10000).")] int timeoutMs = 10000)
        => ToolResponse.Guard(async () =>
        {
            // Resolve geometry ONCE up-front to fail fast on deny/minimized before entering the poll loop.
            var initial = await _perception.ResolveTextCaptureGeometryAsync(new WindowHandle(window), region);
            if (initial.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"OCR of windows owned by '{initial.DeniedProcess}' is blocked.", "target a non-sensitive window");
            if (initial.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");

            System.Collections.Generic.IReadOnlyList<TextFindMatch>? found = null;
            var result = await TextWaiter.WaitAsync(async () =>
            {
                // AGY-AFTER R1 Seat 1: RE-RESOLVE geometry EACH pass — a window can MOVE/RESIZE during a multi-second
                // wait; a once-resolved rect would capture the stale location and compute wrong xPct/yPct. The rect
                // read is a cheap (~1ms) STA op (fine at a >=750ms cadence — §9's off-STA rule targets the EXPENSIVE
                // ~50-150ms CAPTURE, which stays on Task.Run). If the window vanished mid-wait, treat as not-found.
                TextCaptureGeometry geo;
                try { geo = await _perception.ResolveTextCaptureGeometryAsync(new WindowHandle(window), region); }
                catch (ToolException) { return false; } // window vanished/closed mid-wait -> not found; an UNEXPECTED exception propagates (surfaced by ToolResponse.Guard) so a real bug isn't hidden as a timeout
                if (geo.Denied || geo.Minimized) return false;
                var cap = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.CaptureBounds, geo.PasswordRects, maxWidth: 0));
                var matches = await _finder.FindAsync(query, cap.Png, MatchMode.Fuzzy, all: false,
                    cap.ScaleApplied, cap.X, cap.Y, geo.WindowLeft, geo.WindowTop, geo.WindowWidth, geo.WindowHeight);
                if (matches.Count > 0) { found = matches; return true; }
                return false;
            }, timeoutMs, TextWaiter.MinPollIntervalMs);

            if (!result.Satisfied || found is null || found.Count == 0)
                return ToolResponse.Ok(new { satisfied = false });
            var m = found[0];
            return ToolResponse.Ok(new
            {
                satisfied = true,
                match = new
                {
                    text = m.Text, confidence = m.Confidence,
                    bounds = new[] { m.BoundsX, m.BoundsY, m.BoundsW, m.BoundsH },
                    center = new[] { m.CenterX, m.CenterY }, xPct = m.XPct, yPct = m.YPct
                }
            });
        });

    [McpServerTool(ReadOnly = true), Description("Block until a window gains the OS foreground (the human clicks it), the window is closed, or timeout. Flashes the window first. Lease-EXEMPT (no synthetic input). timeoutMs is server-capped to 45s — on a \"timeout\" result, CALL THIS TOOL AGAIN to keep waiting (do NOT yield the chat turn). Returns { foregroundGained, reason: \"gained\"|\"timeout\"|\"window-destroyed\", currentForeground }. Use after a targetNotForeground result to let the human bring your target forward.")]
    public Task<string> DesktopWaitForForeground(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Max ms to block (server-capped to 45000).")] int timeoutMs = 45000)
        => ToolResponse.Guard(() =>
        {
            if (!_windows.TryGetHwnd(new WindowHandle(window), out var hwnd))
                throw new ToolException(ToolErrorCode.WindowHandleStale, $"Handle {window} is no longer valid.", "re-list windows and re-open");
            if (!_waiterGate.TryEnter())
                throw new ToolException(ToolErrorCode.TooManyPendingActions, "Another wait_for_foreground is already in progress.", "wait for it to finish, or retry shortly");
            try
            {
                _attention.Signal(new WindowHandle(window));                 // flash (+ speak if autosound)
                var r = _foregroundWaiter.Wait(hwnd, WaitForForeground.ClampTimeout(timeoutMs));
                var fg = _env.GetForegroundRoot();
                // SEAT-D fold: build currentForeground via the SHARED leak-safe helper so this tool's shape is
                // IDENTICAL to desktop_type/desktop_focus_window — incl. the owner-modal title rule.
                var cf = FlaUI.Mcp.Core.Attention.ForegroundGate.DescribeForeground(
                    foregroundRoot: fg, targetRoot: hwnd,
                    resolveProcess: h => _env.ResolveRoot(h).ProcessName,
                    ownerHwnd: _windows.OwnerHwnd, resolveTitle: _windows.WindowTitle);
                return Task.FromResult(ToolResponse.Ok(new
                {
                    foregroundGained = r.ForegroundGained,
                    reason = r.Reason switch { WaitReason.Gained => "gained", WaitReason.WindowDestroyed => "window-destroyed", _ => "timeout" },
                    currentForeground = cf,
                }));
            }
            finally { _waiterGate.Exit(); }
        });
}
