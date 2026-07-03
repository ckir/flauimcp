using System;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The synthetic-input decision pipeline: elevation -> lease -> deny-list/interlock ->
/// session-state -> budget -> audit -> delegate to ISyntheticInput. Pure over its injected deps; the
/// atomic pre-send re-verify is delegated into the ISyntheticInput leaf (not here, to stay on the same
/// thread as SendInput). 4b registers this in DI and wires the tools to it; in 4a it is dormant.</summary>
public sealed class InputGuard
{
    private readonly ISyntheticInput _sink;
    private readonly IPlatformEnvironment _env;
    private readonly ILeaseProvider _leases;
    private readonly ActionBudget _budget;
    private readonly InputAudit _audit;
    private readonly string _currentSid;
    private readonly bool _isElevated;
    private readonly bool _allowElevation;
    private readonly Func<DateTime> _clock;

    public InputGuard(ISyntheticInput sink, IPlatformEnvironment env, ILeaseProvider leases,
        ActionBudget budget, InputAudit audit, string currentSid, bool isElevated, bool allowElevation,
        Func<DateTime>? clock = null)
    {
        _sink = sink; _env = env; _leases = leases; _budget = budget; _audit = audit;
        _currentSid = currentSid; _isElevated = isElevated; _allowElevation = allowElevation;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Run the full pipeline; throws the mapped ToolException on any refusal. `secondary` is the
    /// second deny-list target for a drag (its drop endpoint) — both endpoints must pass the deny-list.</summary>
    private void Authorize(ActionTarget primary, string action, int payloadLength, ActionTarget? secondary = null)
    {
        if (_isElevated && !_allowElevation)
            throw new ToolException(ToolErrorCode.AccessDeniedIntegrity,
                "Synthetic input is refused while the server runs elevated.",
                "restart without elevation, or pass --unsafe-allow-elevation if you accept the risk");

        var now = _clock();
        var lease = _leases.Read(out var leaseWrite);
        if (lease is null || !lease.IsValidNow(now, _currentSid))
            throw new ToolException(ToolErrorCode.InputNotLeased,
                "Synthetic input is locked. No unexpired lease for this user.",
                "run `flaui-mcp unlock --minutes N` on the host to enable input");

        CheckTarget(primary, lease.HasCapability("shells"));
        if (secondary is { } s) CheckTarget(s, lease.HasCapability("shells"));

        if (!_env.SessionState().CanDeliverInput)
            throw new ToolException(ToolErrorCode.InputDesktopUnavailable,
                "The interactive input desktop is unavailable (locked / disconnected / secure desktop).",
                "connect and unlock the session, then retry");

        if (!_budget.TryConsume(primary.Root, now, leaseWrite))
            throw new ToolException(ToolErrorCode.InputBudgetExceeded,
                $"Synthetic-input rate limit exceeded for this window. Retry in ~{_budget.SecondsUntilFreeSlot(primary.Root, now)}s.",
                "wait for the window to clear, or re-grant the lease with `flaui-mcp unlock` to reset the budget");

        _audit.Record(primary.Root, primary.Pid, primary.ProcessName, action, payloadLength);
        if (secondary is { } drop)
            _audit.Record(drop.Root, drop.Pid, drop.ProcessName, action + "-drop", 1);
    }

    // was: CheckTarget(ActionTarget target, InputLease lease) — now lease-agnostic; caller resolves the cap.
    private static void CheckTarget(ActionTarget target, bool hasShellsCap)
    {
        var verdict = ActionPolicy.Classify(target.ProcessName, target.WindowClass);
        if (verdict == ActionVerdict.Denied)
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Synthetic input into '{target.ProcessName}' is refused (UAC/secure-desktop/credential store).",
                "target a different, non-sensitive window");
        if (verdict == ActionVerdict.Interlocked && !hasShellsCap)
            throw new ToolException(ToolErrorCode.SinkInterlocked,
                $"Synthetic input into the interlocked sink '{target.ProcessName}' requires the 'shells' lease capability.",
                "re-grant with `flaui-mcp unlock --minutes N --allow-shells` (human, out-of-band)");
    }

    /// <summary>Run every REFUSAL gate a real send runs — elevation, lease, deny-list/interlock,
    /// session-state, and a NON-consuming budget peek — WITHOUT consuming a slot or writing audit.
    /// desktop_paste_text calls this to fail-closed BEFORE it mutates the clipboard, so a paste that
    /// will be refused never clobbers the user's clipboard. The subsequent KeyChord re-runs the full
    /// Authorize (idempotent re-check + budget consume + audit).</summary>
    public void PreflightInput(ActionTarget target)
    {
        if (_isElevated && !_allowElevation)
            throw new ToolException(ToolErrorCode.AccessDeniedIntegrity,
                "Synthetic input is refused while the server runs elevated.",
                "restart without elevation, or pass --unsafe-allow-elevation if you accept the risk");

        var now = _clock();
        var lease = _leases.Read(out _);
        if (lease is null || !lease.IsValidNow(now, _currentSid))
            throw new ToolException(ToolErrorCode.InputNotLeased,
                "Synthetic input is locked. No unexpired lease for this user.",
                "run `flaui-mcp unlock --minutes N` on the host to enable input");

        CheckTarget(target, lease.HasCapability("shells"));

        if (!_env.SessionState().CanDeliverInput)
            throw new ToolException(ToolErrorCode.InputDesktopUnavailable,
                "The interactive input desktop is unavailable (locked / disconnected / secure desktop).",
                "connect and unlock the session, then retry");

        if (!_budget.HasFreeSlot(target.Root, now))
            throw new ToolException(ToolErrorCode.InputBudgetExceeded,
                $"Synthetic-input rate limit exceeded for this window. Retry in ~{_budget.SecondsUntilFreeSlot(target.Root, now)}s.",
                "wait for the window to clear, or re-grant the lease with `flaui-mcp unlock` to reset the budget");
    }

    public void KeyType(string text, ActionTarget target, int interKeyDelayMs = 0)
    {
        Authorize(target, "type", text?.Length ?? 0);
        _sink.KeyType(text ?? string.Empty, target.Root, interKeyDelayMs);
    }

    public void KeyChord(string[] modifiers, string key, ActionTarget target)
    {
        Authorize(target, "key", (modifiers?.Length ?? 0) + 1);
        _sink.KeyChord(modifiers ?? Array.Empty<string>(), key, target.Root);
    }

    public void MouseClick(int physX, int physY, string button, int count, string[] modifiers, ActionTarget target)
    {
        Authorize(target, "click", count);
        _sink.MouseClick(physX, physY, button, count, modifiers ?? Array.Empty<string>(), target.Root);
    }

    // Drag carries TWO targets: both endpoints run the deny-list; the END root is the re-verify root
    // the sink fires against (a drag can DROP INTO a denied window — spec §4 / red-team finding).
    public void MouseDrag(int sx, int sy, int ex, int ey, string button, ActionTarget startTarget, ActionTarget endTarget)
    {
        Authorize(startTarget, "drag", 1, secondary: endTarget);
        _sink.MouseDrag(sx, sy, ex, ey, button, startTarget.Root, endTarget.Root);
    }

    /// <summary>Authorize a UIA TextPattern caret/selection mutation (desktop_set_caret / _select_text_range).
    /// Spec §4: these synthesize NO OS input, so the lease + session-state + budget gates are EXEMPT — but the
    /// deny-list / sink-interlock ALWAYS run (selecting text in a denied credential window can exfiltrate it).
    /// The interlock OVERRIDE still lives in the lease's 'shells' cap, so an optional valid lease is consulted
    /// purely for that override; no lease is required for an allowed (non-interlocked) target. `target` MUST be
    /// resolved from the ELEMENT being mutated, not its host window (agy R4 #3 — an embedded cross-process
    /// interlocked element inside an allowed host must not be classified as the host). Audits event-only (len=0).
    /// Performs no input — the caller runs the TextPattern op on the automation thread after this returns.
    /// Elevation hard-fail does NOT apply here (it gates SendInput; the deny-list already blocks credential/
    /// secure-desktop targets).</summary>
    public void AuthorizeTextMutation(ActionTarget target, string action)
    {
        var lease = _leases.Read(out _);
        bool hasShellsCap = lease is { } l && l.IsValidNow(_clock(), _currentSid) && l.HasCapability("shells");
        CheckTarget(target, hasShellsCap); // shared deny-list/interlock (TargetDenied / SinkInterlocked)
        _audit.Record(target.Root, target.Pid, target.ProcessName, action, 0);
    }

    /// <summary>Read-only lease status for the pre-flight tool — no input, no side effects. Active iff a
    /// lease is present AND valid for this user right now; SecondsRemaining is clamped to >= 0.</summary>
    public LeaseStatus Status()
    {
        var now = _clock();
        var lease = _leases.Read(out _);
        if (lease is null || !lease.IsValidNow(now, _currentSid))
            return new LeaseStatus(false, 0, false);
        int secs = (int)Math.Max(0, (lease.ExpiryUtc - now).TotalSeconds);
        return new LeaseStatus(true, secs, lease.HasCapability("shells"));
    }
}

/// <summary>The resolved target of a synthetic-input action: its top-level window root + identity for
/// the deny-list/budget/audit. For the ref path the tool resolves these from the window handle; for the
/// coordinate path 4b fills them from IPlatformEnvironment.HitTestRoot.</summary>
public readonly record struct ActionTarget(nint Root, int Pid, string? ProcessName, string? WindowClass);

/// <summary>Read-only lease status surfaced by desktop_input_status (carries no secret content).</summary>
public readonly record struct LeaseStatus(bool Active, int SecondsRemaining, bool Shells);
