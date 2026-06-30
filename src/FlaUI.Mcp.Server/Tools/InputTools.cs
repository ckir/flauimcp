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

    [McpServerTool(Destructive = true), Description("Type text into the focused element via real synthetic keyboard input (SendInput). ref = the element to focus first. Up to 4096 UTF-16 units per call (InvalidArguments over cap). Focuses the element, then re-verifies the OS foreground is still that window immediately before sending; ABORTs (ElementDisappearedDuringAction) if focus was stolen. Requires an active input lease (`flaui-mcp unlock`); InputNotLeased / InputDesktopUnavailable / InputBudgetExceeded / TargetDenied / SinkInterlocked otherwise. Blocked in --read-only-mode.")]
    public Task<string> DesktopType(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref to focus and type into, e.g. e23.")] string @ref,
        [Description("Text to type (<=4096 UTF-16 units).")] string text,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            if ((text?.Length ?? 0) > MaxTypeUnits)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Text exceeds the {MaxTypeUnits} UTF-16 unit per-call cap.", "split the text across multiple desktop_type calls, slicing on a whole-character boundary (never between the two halves of a surrogate pair / an emoji)");

            var target = await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref,
                (win, el) => { el.Focus(); return InputTargeting.ResolveElementTarget(win, el); }, timeoutMs);

            await Task.Run(() => _guard.KeyType(text ?? string.Empty, target));
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

    private static (string[] mods, string key) SplitChord(string chord)
    {
        var tokens = chord.Split('+', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        var mods = tokens.Length > 1 ? tokens[..^1] : System.Array.Empty<string>();
        return (mods, tokens[^1]);
    }
}
