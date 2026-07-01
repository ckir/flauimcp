using System.ComponentModel;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class InputTools
{
    private const int DefaultTimeoutMs = 4000;
    private const int MaxTypeUnits = 4096;
    private readonly PerceptionManager _perception;
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;
    private readonly InputGuard _guard;
    private readonly IPlatformEnvironment _env;

    public InputTools(PerceptionManager perception, WindowManager windows, ServerOptions options,
        InputGuard guard, IPlatformEnvironment env)
    { _perception = perception; _windows = windows; _options = options; _guard = guard; _env = env; }

    [McpServerTool(ReadOnly = true), Description("Report the synthetic-input lease status WITHOUT firing any input or touching any window. Returns { leaseStatus: \"active\"|\"locked\", secondsRemaining, shells }. Call this BEFORE a multi-step input plan to confirm a human has granted input via `flaui-mcp unlock` (the synthetic-input tools fail InputNotLeased until then), instead of discovering the lock by failing. Always safe / read-only.")]
    public Task<string> DesktopInputStatus()
        => ToolResponse.Guard(() =>
        {
            var s = _guard.Status();
            return Task.FromResult(ToolResponse.Ok(new
            { leaseStatus = s.Active ? "active" : "locked", secondsRemaining = s.SecondsRemaining, shells = s.Shells }));
        });

    [McpServerTool(Destructive = true), Description("Position the text caret in an element via UIA TextPattern (NO OS input — synthesizes no keystrokes). ref = the text element to act on; offset = UIA character offset for the caret. Routes through the deny-list (TargetDenied for credential/secure windows; interlocked shells need the 'shells' lease cap) but needs NO input lease. PatternUnsupported if the element exposes no TextPattern. Offsets are UIA character units (may differ from raw UTF-16 for emoji/non-BMP text). Blocked in --read-only-mode.")]
    public Task<string> DesktopSetCaret(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Text element ref to act on, e.g. e23.")] string @ref,
        [Description("UIA character offset for the caret.")] int offset,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, () =>
            _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref, (win, el) =>
            {
                if (offset < 0)
                    throw new ToolException(ToolErrorCode.InvalidArguments, "offset must be >= 0.", "pass a non-negative offset");
                var target = InputTargeting.ResolveElementTarget(win, el); // identity from el, not host win (agy R4 #3)
                _guard.AuthorizeTextMutation(target, "set_caret"); // deny-list (lease-exempt) on the automation thread
                TextRangeInteractor.SetCaret(el, offset);
                return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern" });
            }, timeoutMs));

    [McpServerTool(Destructive = true), Description("Select a text range in an element via UIA TextPattern (NO OS input). ref = the text element; start = UIA character start offset; length = character count. Same deny-list gate as desktop_set_caret; NO input lease required. PatternUnsupported if no TextPattern; InvalidArguments for negative start/length. Offsets are UIA character units (may differ from raw UTF-16 for emoji/non-BMP text). Blocked in --read-only-mode.")]
    public Task<string> DesktopSelectTextRange(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Text element ref to act on, e.g. e23.")] string @ref,
        [Description("UIA character start offset.")] int start,
        [Description("Character count to select.")] int length,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, () =>
            _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref, (win, el) =>
            {
                if (start < 0 || length < 0)
                    throw new ToolException(ToolErrorCode.InvalidArguments, "start and length must be >= 0.", "pass non-negative offsets");
                var target = InputTargeting.ResolveElementTarget(win, el); // identity from el, not host win (agy R4 #3)
                _guard.AuthorizeTextMutation(target, "select_text_range");
                TextRangeInteractor.SelectRange(el, start, length);
                return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern" });
            }, timeoutMs));

    [McpServerTool(Destructive = true), Description("Type text into the focused element via real synthetic keyboard input (SendInput). ref = the element to focus first. Up to 4096 UTF-16 units per call (InvalidArguments over cap). Focuses the element, then re-verifies the OS foreground is still that window immediately before sending; ABORTs (ElementDisappearedDuringAction) if focus was stolen. By default keystrokes are PACED (interKeyDelayMs=15) so slow/async consumers (e.g. the Win11 Notepad autocomplete pipeline) don't drop or garble fast input; when paced the foreground is re-verified before EACH key, so a mid-type focus-steal still aborts (leaving the partial text already typed). Pass interKeyDelayMs=0 for a single atomic blast (fastest; may garble on reactive editors). Requires an active input lease (`flaui-mcp unlock`); InputNotLeased / InputDesktopUnavailable / InputBudgetExceeded / TargetDenied / SinkInterlocked otherwise. Blocked in --read-only-mode.")]
    public Task<string> DesktopType(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref to focus and type into, e.g. e23.")] string @ref,
        [Description("Text to type (<=4096 UTF-16 units).")] string text,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs,
        [Description("Delay in ms BETWEEN keystrokes (default 15). Paces synthetic typing so slow/async editors keep up; the foreground is re-verified before each key (abort-on-steal preserved). 0 = one atomic blast (fastest, may garble reactive editors). Negative -> InvalidArguments.")] int interKeyDelayMs = 15)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            if ((text?.Length ?? 0) > MaxTypeUnits)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Text exceeds the {MaxTypeUnits} UTF-16 unit per-call cap.", "split the text across multiple desktop_type calls, slicing on a whole-character boundary (never between the two halves of a surrogate pair / an emoji)");
            if (interKeyDelayMs < 0)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "interKeyDelayMs must be >= 0.", "pass 0 for a single atomic blast, or a positive per-key delay");

            var target = await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref,
                (win, el) => { el.Focus(); return InputTargeting.ResolveElementTarget(win, el); }, timeoutMs);

            await Task.Run(() => _guard.KeyType(text ?? string.Empty, target, interKeyDelayMs));
            return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" });
        });

    [McpServerTool(Destructive = true), Description("Send one keyboard chord via real synthetic input. chord grammar: `+`-delimited, zero-or-more modifiers Ctrl|Alt|Shift|Win + one key (letter/digit; Enter Tab Esc Backspace Delete Home End PageUp PageDown Up Down Left Right Space; F1-F24). e.g. \"Ctrl+S\", \"Enter\". Omit ref/window to target the current FOREGROUND window; pass BOTH ref AND window to focus a specific element first. Unknown token -> InvalidArguments. Same lease/deny-list/session gates as desktop_type. Blocked in --read-only-mode.")]
    public Task<string> DesktopKey(
        [Description("Chord, e.g. \"Ctrl+S\" or \"Enter\".")] string chord,
        [Description("Optional element ref to focus first; omit to target the current foreground window.")] string? @ref = null,
        [Description("Window handle (REQUIRED only when ref is given), e.g. w1.")] string? window = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            KeyChordParser.Parse(chord); // validate grammar up front -> InvalidArguments (discard result)
            var (modNames, keyToken) = SplitChord(chord);
            bool haveRef = !string.IsNullOrEmpty(@ref);
            if (haveRef && string.IsNullOrEmpty(window))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "A ref needs its window handle.", "pass `window` alongside `ref`, or omit both to target the foreground window");

            ActionTarget target = haveRef
                ? await _perception.RunOnRefForInputAsync(new WindowHandle(window!), @ref!,
                    (win, el) => { el.Focus(); return InputTargeting.ResolveElementTarget(win, el); }, timeoutMs)
                : await ResolveForegroundTargetAsync();

            await Task.Run(() => _guard.KeyChord(modNames, keyToken, target));
            return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" });
        });

    // Option A (user-approved 2026-06-30): the no-ref foreground key classifies the FOCUSED element's owner,
    // so an embedded cross-process interlocked pane (e.g. an integrated terminal inside an Allowed host) is
    // caught by the deny-list. Root stays the env's foreground root (so the leaf's pre-send re-verify matches);
    // identity prefers the focused element, falling back to the root's own identity (no regression) if nothing
    // is focused / unresolvable.
    private async Task<ActionTarget> ResolveForegroundTargetAsync()
    {
        nint root = _env.GetForegroundRoot();
        if (root == 0)
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "No foreground window to target.", "focus a window, or pass an explicit ref");
        var id = await _windows.ResolveFocusedIdentityAsync();
        if (id is { } f && !string.IsNullOrEmpty(f.Process))
            return new ActionTarget(root, f.Pid, f.Process, f.Class);
        var r = _env.ResolveRoot(root); // fallback: root-level identity (no regression vs root-classification)
        return new ActionTarget(root, 0, r.ProcessName, r.WindowClass);
    }

    [McpServerTool(Destructive = true), Description("Synthetic mouse click at an element's clickable point (ref path). button=left|right|middle, count=1|2, modifiers optional. Re-hit-tests that the point still maps to the target window immediately before sending. Same lease/deny-list/session gates. Blocked in --read-only-mode.")]
    public Task<string> DesktopClick(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref to click, e.g. e23.")] string @ref,
        [Description("left|right|middle (default left).")] string button = "left",
        [Description("1 or 2 (default 1).")] int count = 1,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            // BLOCKER (agy): the click point may belong to a SEPARATE top-level window — a context menu,
            // tooltip, or WPF Popup is its own HWND, NOT win's root. So derive the ActionTarget from a
            // hit-test of the element's clickable point (the surface actually under the pixel), not from
            // ResolveRefTarget(win) — otherwise every menu/dropdown click spuriously aborts the leaf's
            // HitTestRoot(point)==root re-verify, and the deny-list would classify the wrong window. The
            // leaf re-hit-tests the same point just before send, so the TOCTOU check still holds (two
            // hit-tests at different instants catch an overlay that slides in after resolution).
            var (target, px, py) = await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref,
                (win, el) =>
                {
                    System.Drawing.Point p;
                    try { p = el.GetClickablePoint(); }
                    catch (FlaUI.Core.Exceptions.NoClickablePointException) { var b = el.BoundingRectangle; p = new System.Drawing.Point(b.Left + b.Width / 2, b.Top + b.Height / 2); }
                    var pt = _env.HitTestRoot(p.X, p.Y); // Win32, thread-agnostic — safe on the action STA
                    var t = new ActionTarget(pt.Root, 0, pt.ProcessName, pt.WindowClass);
                    return (t, p.X, p.Y);
                }, timeoutMs);
            await Task.Run(() => _guard.MouseClick(px, py, button, count, System.Array.Empty<string>(), target));
            return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" });
        });

    [McpServerTool(Destructive = true), Description("Synthetic mouse click at a window-relative point. xPct/yPct in [0,1] relative to the target window's bounding rect (the same fractional space desktop_screenshot/desktop_get_bounds publish). The point is hit-tested + deny-listed in the immediate pre-send instant; an unidentifiable point is refused (TargetDenied). button=left|right|middle, count=1|2. Blocked in --read-only-mode.")]
    public Task<string> DesktopClickAt(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("X fraction 0..1 of the window width.")] double xPct,
        [Description("Y fraction 0..1 of the window height.")] double yPct,
        [Description("left|right|middle (default left).")] string button = "left",
        [Description("1 or 2 (default 1).")] int count = 1,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            var (px, py) = await ResolveWindowPctAsync(window, xPct, yPct, timeoutMs);
            var pt = _env.HitTestRoot(px, py);
            var target = new ActionTarget(pt.Root, 0, pt.ProcessName, pt.WindowClass);
            await Task.Run(() => _guard.MouseClick(px, py, button, count, System.Array.Empty<string>(), target));
            return ToolResponse.Ok(new { ok = true, pathUsed = "coordinate" });
        });

    [McpServerTool(Destructive = true), Description("Synthetic mouse drag between two window-relative points (§5 pct space). BOTH endpoints are hit-tested + deny-listed; the END point is re-hit-tested immediately before the mouse-up. button=left|right|middle. Blocked in --read-only-mode.")]
    public Task<string> DesktopDrag(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Start X fraction 0..1.")] double startXPct,
        [Description("Start Y fraction 0..1.")] double startYPct,
        [Description("End X fraction 0..1.")] double endXPct,
        [Description("End Y fraction 0..1.")] double endYPct,
        [Description("left|right|middle (default left).")] string button = "left",
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            var (sx, sy) = await ResolveWindowPctAsync(window, startXPct, startYPct, timeoutMs);
            var (ex, ey) = await ResolveWindowPctAsync(window, endXPct, endYPct, timeoutMs);
            var sPt = _env.HitTestRoot(sx, sy);
            var ePt = _env.HitTestRoot(ex, ey);
            var startTarget = new ActionTarget(sPt.Root, 0, sPt.ProcessName, sPt.WindowClass);
            var endTarget = new ActionTarget(ePt.Root, 0, ePt.ProcessName, ePt.WindowClass);
            await Task.Run(() => _guard.MouseDrag(sx, sy, ex, ey, button, startTarget, endTarget));
            return ToolResponse.Ok(new { ok = true, pathUsed = "coordinate" });
        });

    // Resolve a window-relative fraction to a physical screen pixel via the window's physical bounds.
    private Task<(int px, int py)> ResolveWindowPctAsync(string window, double xPct, double yPct, int timeoutMs)
        => _windows.RunOnWindowActionAsync(new WindowHandle(window), (win, _) =>
        {
            var r = win.BoundingRectangle; // System.Drawing.Rectangle, physical px
            return CoordinateMath.PctToPhysical(r.Left, r.Top, r.Width, r.Height, xPct, yPct);
        }, timeoutMs);

    private static (string[] mods, string key) SplitChord(string chord)
    {
        var tokens = chord.Split('+', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        var mods = tokens.Length > 1 ? tokens[..^1] : System.Array.Empty<string>();
        return (mods, tokens[^1]);
    }
}
