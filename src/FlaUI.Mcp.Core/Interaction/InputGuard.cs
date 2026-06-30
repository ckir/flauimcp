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

        CheckTarget(primary, lease);
        if (secondary is { } s) CheckTarget(s, lease);

        if (!_env.SessionState().CanDeliverInput)
            throw new ToolException(ToolErrorCode.InputDesktopUnavailable,
                "The interactive input desktop is unavailable (locked / disconnected / secure desktop).",
                "connect and unlock the session, then retry");

        if (!_budget.TryConsume(primary.Root, now, leaseWrite))
            throw new ToolException(ToolErrorCode.InputBudgetExceeded,
                "Synthetic-input rate limit exceeded for this window.",
                "slow down, or re-grant the lease with `flaui-mcp unlock` to reset the budget");

        _audit.Record(primary.Root, primary.Pid, primary.ProcessName, action, payloadLength);
    }

    private static void CheckTarget(ActionTarget target, InputLease lease)
    {
        var verdict = ActionPolicy.Classify(target.ProcessName, target.WindowClass);
        if (verdict == ActionVerdict.Denied)
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Synthetic input into '{target.ProcessName}' is refused (UAC/secure-desktop/credential store).",
                "target a different, non-sensitive window");
        if (verdict == ActionVerdict.Interlocked && !lease.HasCapability("shells"))
            throw new ToolException(ToolErrorCode.SinkInterlocked,
                $"Synthetic input into the interlocked sink '{target.ProcessName}' requires the 'shells' lease capability.",
                "re-grant with `flaui-mcp unlock --minutes N --allow-shells` (human, out-of-band)");
    }

    public void KeyType(string text, ActionTarget target)
    {
        Authorize(target, "type", text?.Length ?? 0);
        _sink.KeyType(text ?? string.Empty, target.Root);
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
        _sink.MouseDrag(sx, sy, ex, ey, button, endTarget.Root);
    }
}

/// <summary>The resolved target of a synthetic-input action: its top-level window root + identity for
/// the deny-list/budget/audit. For the ref path the tool resolves these from the window handle; for the
/// coordinate path 4b fills them from IPlatformEnvironment.HitTestRoot.</summary>
public readonly record struct ActionTarget(nint Root, int Pid, string? ProcessName, string? WindowClass);
