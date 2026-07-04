using System.ComponentModel;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
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
    private const int MaxPasteUnits = 1_000_000;
    private const int VerifySettleMs = 100; // let reactive editors commit inline-prediction/IME ghost text before the after-read
    private readonly PerceptionManager _perception;
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;
    private readonly InputGuard _guard;
    private readonly IPlatformEnvironment _env;
    private readonly IActionOverlay _overlay;

    public InputTools(PerceptionManager perception, WindowManager windows, ServerOptions options,
        InputGuard guard, IPlatformEnvironment env, IActionOverlay? overlay = null)
    { _perception = perception; _windows = windows; _options = options; _guard = guard; _env = env;
      _overlay = overlay ?? NullActionOverlay.Instance; }

    // Phase 10 #2 T6a: the selector path routes through RunOnSelectorForInputAsync (T4), which mirrors
    // RunOnRefForInputAsync exactly (same offscreen guard, same transient action STA, same WriteMode) —
    // the only difference is it resolves `sel` to a live element first and mints a descriptor-only ref.
    // So the deny-list/lease/audit gates fire identically for both paths as long as the ActionTarget is
    // derived from the resolved `el` INSIDE the callback, exactly as the ref path already does.

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
        [Description("UIA character offset for the caret.")] int offset,
        [Description("Text element ref to act on, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            SelectorGating.RequireExactlyOne(@ref, selector);
            // Validate args + selector BEFORE any resolve — an invalid arg / malformed selector must not
            // flash the overlay for an action that will then fail (INV-OV-5), and the error code must not
            // depend on whether --overlay is on.
            if (offset < 0)
                throw new ToolException(ToolErrorCode.InvalidArguments, "offset must be >= 0.", "pass a non-negative offset");
            if (selector is { } s0) s0.Validate();

            // Phase-2 (authoritative) callback: resolve identity, authorize (audits ONCE), mutate.
            Func<AutomationElement, AutomationElement, bool> mutate = (win, el) =>
            {
                var target = InputTargeting.ResolveElementTarget(win, el); // identity from el, not host win (agy R4 #3)
                _guard.AuthorizeTextMutation(target, "set_caret"); // deny-list (lease-exempt) on the automation thread
                TextRangeInteractor.SetCaret(el, offset);
                return true;
            };

            // Overlay (enabled only): phase-1 resolve -> non-auditing preflight + bounds -> preview off-STA.
            if (_overlay.Enabled)
            {
                Func<AutomationElement, AutomationElement, (ActionTarget, OverlayRect)> pre = (win, el) =>
                {
                    var t = InputTargeting.ResolveElementTarget(win, el);
                    _guard.PreflightTextMutation(t); // deny-list gate, NO audit
                    return (t, ElementRectFromElement(el));
                };
                OverlayRect prect = selector is { } psel
                    ? (await _perception.RunOnSelectorForInputAsync(new WindowHandle(window), psel, pre, timeoutMs)).Value.Item2
                    : (await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref!, pre, timeoutMs)).Item2;
                await _overlay.PreviewAsync(prect);
            }

            if (selector is { } sel)
            {
                var (_, resolved) = await _perception.RunOnSelectorForInputAsync(new WindowHandle(window), sel, mutate, timeoutMs);
                return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern", resolvedElement = resolved });
            }
            await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref!, mutate, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern" });
        });

    [McpServerTool(Destructive = true), Description("Select a text range in an element via UIA TextPattern (NO OS input). ref = the text element; start = UIA character start offset; length = character count. Same deny-list gate as desktop_set_caret; NO input lease required. PatternUnsupported if no TextPattern; InvalidArguments for negative start/length. Offsets are UIA character units (may differ from raw UTF-16 for emoji/non-BMP text). Blocked in --read-only-mode.")]
    public Task<string> DesktopSelectTextRange(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("UIA character start offset.")] int start,
        [Description("Character count to select.")] int length,
        [Description("Text element ref to act on, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            SelectorGating.RequireExactlyOne(@ref, selector);
            // Validate args + selector BEFORE any resolve — an invalid arg / malformed selector must not
            // flash the overlay for an action that will then fail (INV-OV-5), and the error code must not
            // depend on whether --overlay is on.
            if (start < 0 || length < 0)
                throw new ToolException(ToolErrorCode.InvalidArguments, "start and length must be >= 0.", "pass non-negative offsets");
            if (selector is { } s0) s0.Validate();

            Func<AutomationElement, AutomationElement, bool> mutate = (win, el) =>
            {
                var target = InputTargeting.ResolveElementTarget(win, el); // identity from el, not host win (agy R4 #3)
                _guard.AuthorizeTextMutation(target, "select_text_range");
                TextRangeInteractor.SelectRange(el, start, length);
                return true;
            };

            if (_overlay.Enabled)
            {
                Func<AutomationElement, AutomationElement, (ActionTarget, OverlayRect)> pre = (win, el) =>
                {
                    var t = InputTargeting.ResolveElementTarget(win, el);
                    _guard.PreflightTextMutation(t);
                    return (t, ElementRectFromElement(el));
                };
                OverlayRect prect = selector is { } psel
                    ? (await _perception.RunOnSelectorForInputAsync(new WindowHandle(window), psel, pre, timeoutMs)).Value.Item2
                    : (await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref!, pre, timeoutMs)).Item2;
                await _overlay.PreviewAsync(prect);
            }

            if (selector is { } sel)
            {
                var (_, resolved) = await _perception.RunOnSelectorForInputAsync(new WindowHandle(window), sel, mutate, timeoutMs);
                return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern", resolvedElement = resolved });
            }
            await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref!, mutate, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern" });
        });

    [McpServerTool(Destructive = true), Description("Type text into the focused element via real synthetic keyboard input (SendInput). ref = the element to focus first. Up to 4096 UTF-16 units per call (InvalidArguments over cap). Focuses the element, then re-verifies the OS foreground is still that window immediately before sending; ABORTs (ElementDisappearedDuringAction) if focus was stolen. By default keystrokes are PACED (interKeyDelayMs=15) so slow/async consumers (e.g. the Win11 Notepad autocomplete pipeline) don't drop or garble fast input; when paced the foreground is re-verified before EACH key, so a mid-type focus-steal still aborts (leaving the partial text already typed). Pass interKeyDelayMs=0 for a single atomic blast (fastest; may garble on reactive editors). By default (verify=true) the element is read back after typing and the result carries a `verify` object; on a mismatch it advises desktop_set_value (UIA ValuePattern) — the reliable path for reactive/RichEdit editors (the new Notepad). verify NEVER throws and NEVER converts a successful type into a failure. Requires an active input lease (`flaui-mcp unlock`); InputNotLeased / InputDesktopUnavailable / InputBudgetExceeded / TargetDenied / SinkInterlocked otherwise. Blocked in --read-only-mode.")]
    public Task<string> DesktopType(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Text to type (<=4096 UTF-16 units).")] string text,
        [Description("Element ref to focus and type into, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs,
        [Description("Delay in ms BETWEEN keystrokes (default 15). Paces synthetic typing so slow/async editors keep up; the foreground is re-verified before each key (abort-on-steal preserved). 0 = one atomic blast (fastest, may garble reactive editors). Negative -> InvalidArguments.")] int interKeyDelayMs = 15,
        [Description("Read the element back after typing and report whether the committed text matches (default true). Soft advisory only — NEVER throws on mismatch and never fails a successful type; on mismatch it recommends desktop_set_value. Pass false for old fire-and-forget speed (skips a ~100ms settle + two reads).")] bool verify = true)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            if ((text?.Length ?? 0) > MaxTypeUnits)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Text exceeds the {MaxTypeUnits} UTF-16 unit per-call cap.", "split the text across multiple desktop_type calls, slicing on a whole-character boundary (never between the two halves of a surrogate pair / an emoji)");
            if (interKeyDelayMs < 0)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "interKeyDelayMs must be >= 0.", "pass 0 for a single atomic blast, or a positive per-key delay");
            SelectorGating.RequireExactlyOne(@ref, selector);
            if (selector is { } s0) s0.Validate();

            // Local response helper: the selector path additionally surfaces the minted ref as
            // `resolvedElement` on EVERY return; the ref path's shape stays byte-identical to before T6b.
            string? resolved = null;
            string Reply(object verifyValue) => resolved is null
                ? ToolResponse.Ok(new { ok = true, pathUsed = "synthetic", verify = verifyValue })
                : ToolResponse.Ok(new { ok = true, pathUsed = "synthetic", verify = verifyValue, resolvedElement = resolved });

            // Focus + resolve the action target; when verifying, also read the RAW baseline text on the
            // SAME transient STA (before-read sits between Focus and the SendInput's pre-send re-verify,
            // which still aborts on a focus-steal — the before-read cannot blast the wrong window).
            Func<AutomationElement, AutomationElement, (ActionTarget, VerifyRead)> cb = (win, el) =>
            {
                el.Focus();
                var t = InputTargeting.ResolveElementTarget(win, el);
                var b = verify ? VerifyReader.FromElement(el) : default;
                return (t, b);
            };

            ActionTarget target; VerifyRead before; string effectiveRef;
            if (selector is { } sel)
            {
                var (val, r) = await _perception.RunOnSelectorForInputAsync(new WindowHandle(window), sel, cb, timeoutMs);
                (target, before) = val;
                effectiveRef = r;
                resolved = r;
            }
            else
            {
                (target, before) = await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref!, cb, timeoutMs);
                effectiveRef = @ref!;
            }

            await PreviewSyntheticAsync(target, ElementRect(target));
            await Task.Run(() => _guard.KeyType(text ?? string.Empty, target, interKeyDelayMs));

            if (!verify)
                return Reply(VerifyResult.Disabled);

            // Short-circuit on the BEFORE state before paying the settle + after-read. If the baseline
            // already disqualifies an assertion, the after-read is wasted AND (if it later threw) would
            // MISCLASSIFY the skip as "read-failed" — masking the true reason. So decide from `before`
            // first; only the empty-field precondition proceeds to read back. (agy AGY-AFTER finding #1.)
            if (before.Redacted)
                return Reply(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "redacted", null, null)));
            if (before.Text is null)
                return Reply(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "no-textpattern", null, null)));
            if (before.Text.Length != 0)
                return Reply(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "field-not-empty", null, null)));

            // before.Text == "" : the ONLY path that asserts. Verification latency is NOT charged against
            // the caller's type timeout — settle, then read-after on a fresh independent resolution with
            // its own timeout. A failed read stays soft (never fails a successful type). Reuse the SAME
            // minted ref (effectiveRef) for the after-read — a second selector walk could match a
            // different element or re-mint a fresh ref (Phase 10 #2 T6b).
            await Task.Delay(VerifySettleMs);
            VerifyRead after;
            try
            {
                after = await _perception.RunOnRefReadAsync(new WindowHandle(window), effectiveRef,
                    el => VerifyReader.FromElement(el, readCapability: true), timeoutMs);
            }
            catch
            {
                return Reply(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "read-failed", null, null)));
            }

            VerifyOutcome outcome =
                after.Redacted
                    ? new VerifyOutcome(VerifyStatus.Skipped, "redacted", null, null)
                : after.Text is null
                    ? new VerifyOutcome(VerifyStatus.Skipped, "read-failed", null, null)
                    : TypedTextVerifier.Check(before.Text, after.Text, text ?? string.Empty);

            return Reply(VerifyResult.From(outcome, after.CanSetValue));
        });

    [McpServerTool(Destructive = true), Description("Paste text into the focused element via an atomic clipboard-backed Ctrl+V — the reliable path for reactive editors (new Win11 Notepad, Chromium contenteditable) that garble desktop_type keystrokes. ref = the element to focus. Up to 1,000,000 UTF-16 units. ALL input gates (lease/deny-list/budget/session) are checked BEFORE the clipboard is touched. By default (verify=true) the element is read back and a soft `verify` object is returned; the prior clipboard is restored ONLY when the paste is confirmed to have landed (else `clipboardRestored:\"abandoned\"`, leaving your text on the clipboard — expect this in reactive editors that transform pasted text, and whenever verify=false). A NON-text clipboard (image/files) is refused (ClipboardHoldsNonText) unless forceOverwriteClipboard=true. Mixed text+rich clipboards restore as plain text (`clipboardRestored:\"text-degraded\"`). Requires an active input lease; InputNotLeased/TargetDenied/etc. otherwise. Blocked in --read-only-mode.")]
    public Task<string> DesktopPasteText(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Text to paste (<=1,000,000 UTF-16 units).")] string text,
        [Description("Element ref to focus and paste into, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs,
        [Description("Read the element back and report whether the paste landed (default true). Soft — never throws. ALSO gates clipboard restore: with verify=false the prior clipboard is not restored (clipboardRestored:\"abandoned\").")] bool verify = true,
        [Description("Proceed even if the clipboard holds NON-text content (image/files) that cannot be preserved. Default false = refuse with ClipboardHoldsNonText before any mutation.")] bool forceOverwriteClipboard = false)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            if (string.IsNullOrEmpty(text))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "No text to paste.", "pass the text to paste (empty is rejected so a degenerate call can't clobber the clipboard)");
            if (text.Length > MaxPasteUnits)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Text exceeds the {MaxPasteUnits} UTF-16 unit per-call cap.", "split the paste across calls on a whole-character boundary");
            SelectorGating.RequireExactlyOne(@ref, selector);
            if (selector is { } s0) s0.Validate();

            var fx = new PasteEffects(_perception, _guard, new WindowHandle(window), @ref, selector, timeoutMs, _overlay);
            var outcome = await PasteFlow.RunAsync(fx, text, verify, forceOverwriteClipboard, ms => Task.Delay(ms));
            return fx.ResolvedRef is null
                ? ToolResponse.Ok(new { ok = true, pathUsed = "clipboard-paste",
                    clipboardRestored = outcome.ClipboardRestored, verify = outcome.Verify })
                : ToolResponse.Ok(new { ok = true, pathUsed = "clipboard-paste",
                    clipboardRestored = outcome.ClipboardRestored, verify = outcome.Verify, resolvedElement = fx.ResolvedRef });
        });

    /// <summary>Production IPasteEffects: focus/read via the perception STA, gate via InputGuard, and
    /// borrow the clipboard via ClipboardAccess. Effect ORDER + gating live in PasteFlow (tested headless).
    /// Phase 10 #2 T6b: the selector path resolves ONCE (atomic focus+before via RunOnSelectorForInputAsync)
    /// and captures the minted ref in `_resolvedRef`; ReadAfterAsync reuses that SAME ref instead of
    /// re-walking the selector, so it can't match a different element or re-mint on the after-read.</summary>
    private sealed class PasteEffects : IPasteEffects
    {
        private readonly PerceptionManager _p; private readonly InputGuard _g;
        private readonly WindowHandle _win; private readonly string? _ref; private readonly Selector? _selector;
        private readonly int _timeout;
        private readonly IActionOverlay _overlay;
        private string? _resolvedRef;
        public string? ResolvedRef => _resolvedRef;

        public PasteEffects(PerceptionManager p, InputGuard g, WindowHandle win, string? @ref, Selector? selector, int timeout, IActionOverlay overlay)
        { _p = p; _g = g; _win = win; _ref = @ref; _selector = selector; _timeout = timeout; _overlay = overlay; }

        public async Task<(ActionTarget, VerifyRead)> FocusAndBeforeReadAsync(bool verify)
        {
            Func<AutomationElement, AutomationElement, (ActionTarget, VerifyRead)> cb = (win, el) =>
            {
                el.Focus();
                var t = InputTargeting.ResolveElementTarget(win, el);
                var b = verify ? VerifyReader.FromElement(el) : default;
                return (t, b);
            };
            if (_selector is { } sel)
            {
                var (val, r) = await _p.RunOnSelectorForInputAsync(_win, sel, cb, _timeout);
                _resolvedRef = r;
                return val;
            }
            return await _p.RunOnRefForInputAsync(_win, _ref!, cb, _timeout);
        }

        public void Preflight(ActionTarget target) => _g.PreflightInput(target);
        public Task PreviewAsync(OverlayRect rect) =>
            // Deny-list already ran in Preflight (called just before this); the clipboard is not yet borrowed.
            // Just show — GdiActionOverlay swallows any failure (INV-OV-4) and no-ops a degenerate/disabled rect.
            _overlay.Enabled && !rect.IsDegenerate ? _overlay.PreviewAsync(rect) : Task.CompletedTask;
        public Task<ClipboardSnapshot> SnapshotAsync() => ClipboardAccess.Snapshot();
        public Task SetClipboardAsync(string text) => ClipboardAccess.SetTextAsync(text);
        public Task PasteAsync(ActionTarget target) => Task.Run(() => _g.KeyChord(new[] { "Ctrl" }, "V", target));
        public void AuditForceOverwrite() => System.Console.Error.WriteLine("[audit] desktop_paste_text: force-overwrite of a non-text clipboard.");
        public Task<VerifyRead> ReadAfterAsync() =>
            _p.RunOnRefReadAsync(_win, _resolvedRef ?? _ref!, el => VerifyReader.FromElement(el, readCapability: true), _timeout);
    }

    [McpServerTool(Destructive = true), Description("Send one keyboard chord via real synthetic input. chord grammar: `+`-delimited, zero-or-more modifiers Ctrl|Alt|Shift|Win + one key (letter/digit; Enter Tab Esc Backspace Delete Home End PageUp PageDown Up Down Left Right Space; F1-F24). e.g. \"Ctrl+S\", \"Enter\". Omit ref/window to target the current FOREGROUND window; pass BOTH ref AND window to focus a specific element first. Unknown token -> InvalidArguments. Same lease/deny-list/session gates as desktop_type. Blocked in --read-only-mode.")]
    public Task<string> DesktopKey(
        [Description("Chord, e.g. \"Ctrl+S\" or \"Enter\".")] string chord,
        [Description("Optional element ref to focus first; omit to target the current foreground window. At most one of ref | selector.")] string? @ref = null,
        [Description("Window handle (REQUIRED only when ref or selector is given), e.g. w1.")] string? window = null,
        [Description("Optional selector to focus first; omit to target the current foreground window. At most one of ref | selector. " + SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            KeyChordParser.Parse(chord); // validate grammar up front -> InvalidArguments (discard result)
            var (modNames, keyToken) = SplitChord(chord);
            bool haveRef = !string.IsNullOrEmpty(@ref);
            bool haveSel = selector is not null;
            if (haveRef && haveSel)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "provide at most one of ref or selector.", "drop one — they are mutually exclusive; omit both to target the foreground window");
            if (haveRef && string.IsNullOrEmpty(window))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "A ref needs its window handle.", "pass `window` alongside `ref`, or omit both to target the foreground window");
            if (haveSel && string.IsNullOrEmpty(window))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "A selector needs its window handle.", "pass `window` alongside `selector`");

            ActionTarget target;
            string? resolved = null;
            if (haveSel)
            {
                selector!.Validate();
                var r = await _perception.RunOnSelectorForInputAsync(new WindowHandle(window!), selector,
                    (win, el) => { el.Focus(); return InputTargeting.ResolveElementTarget(win, el); }, timeoutMs);
                target = r.Value;
                resolved = r.ResolvedRef;
            }
            else if (haveRef)
            {
                target = await _perception.RunOnRefForInputAsync(new WindowHandle(window!), @ref!,
                    (win, el) => { el.Focus(); return InputTargeting.ResolveElementTarget(win, el); }, timeoutMs);
            }
            else
            {
                target = await ResolveForegroundTargetAsync();
            }

            await PreviewSyntheticAsync(target, ElementRect(target));
            await Task.Run(() => _guard.KeyChord(modNames, keyToken, target));
            return resolved is null
                ? ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" })
                : ToolResponse.Ok(new { ok = true, pathUsed = "synthetic", resolvedElement = resolved });
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
        [Description("Element ref to click, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("left|right|middle (default left).")] string button = "left",
        [Description("1 or 2 (default 1).")] int count = 1,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            SelectorGating.RequireExactlyOne(@ref, selector);
            // BLOCKER (agy): the click point may belong to a SEPARATE top-level window — a context menu,
            // tooltip, or WPF Popup is its own HWND, NOT win's root. So derive the ActionTarget from a
            // hit-test of the element's clickable point (the surface actually under the pixel), not from
            // ResolveRefTarget(win) — otherwise every menu/dropdown click spuriously aborts the leaf's
            // HitTestRoot(point)==root re-verify, and the deny-list would classify the wrong window. The
            // leaf re-hit-tests the same point just before send, so the TOCTOU check still holds (two
            // hit-tests at different instants catch an overlay that slides in after resolution).
            Func<AutomationElement, AutomationElement, (ActionTarget target, int px, int py)> cb = (win, el) =>
            {
                System.Drawing.Point p;
                try { p = el.GetClickablePoint(); }
                catch (FlaUI.Core.Exceptions.NoClickablePointException) { var b = el.BoundingRectangle; p = new System.Drawing.Point(b.Left + b.Width / 2, b.Top + b.Height / 2); }
                var pt = _env.HitTestRoot(p.X, p.Y); // Win32, thread-agnostic — safe on the action STA
                var t = new ActionTarget(pt.Root, 0, pt.ProcessName, pt.WindowClass);
                return (t, p.X, p.Y);
            };

            ActionTarget target; int px, py; string? resolved = null;
            if (selector is { } sel)
            {
                sel.Validate();
                var (val, r) = await _perception.RunOnSelectorForInputAsync(new WindowHandle(window), sel, cb, timeoutMs);
                (target, px, py) = val;
                resolved = r;
            }
            else
            {
                (target, px, py) = await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref!, cb, timeoutMs);
            }
            await PreviewSyntheticAsync(target, Crosshair(px, py));
            await Task.Run(() => _guard.MouseClick(px, py, button, count, System.Array.Empty<string>(), target));
            return resolved is null
                ? ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" })
                : ToolResponse.Ok(new { ok = true, pathUsed = "synthetic", resolvedElement = resolved });
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
            await PreviewSyntheticAsync(target, Crosshair(px, py));
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
            await PreviewSyntheticAsync(startTarget, Crosshair(sx, sy));
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

    // Post-authorization, pre-effect overlay preview for the SYNTHETIC path (spec §5.3). Non-consuming
    // preflight gate FIRST so a refused act flashes nothing (INV-OV-5), then show+delay off the STA. No-op
    // (and no preflight) when the overlay is off or the rect is degenerate — the effect's own Authorize
    // still gates the action, so behavior is byte-identical when disabled (INV-OV-1).
    private async Task PreviewSyntheticAsync(ActionTarget target, OverlayRect rect)
    {
        if (!_overlay.Enabled || rect.IsDegenerate) return;
        _guard.PreflightInput(target);         // throws on refusal -> no flash, effect never runs
        await _overlay.PreviewAsync(rect);
    }

    // The overlay rect for an element action = the SAME bounds T8 recorded (spec §1), from the resolved
    // element's identity. Null (window/foreground) target -> degenerate rect -> skipped.
    private static OverlayRect ElementRect(ActionTarget target) =>
        target.Element is { } id ? new OverlayRect(id.Bounds.L, id.Bounds.T, id.Bounds.W, id.Bounds.H) : default;

    // Live-element bounds (the two-phase text path holds the live `el`), best-effort.
    private static OverlayRect ElementRectFromElement(AutomationElement el)
    {
        try { var r = el.BoundingRectangle; return new OverlayRect(r.Left, r.Top, r.Width, r.Height); }
        catch { return default; }
    }

    // A small crosshair box centered on a physical point, for coordinate/click actions (no element rect).
    private static OverlayRect Crosshair(int px, int py)
    {
        const int half = 20; // 40x40 box
        return new OverlayRect(px - half, py - half, 2 * half, 2 * half);
    }
}
