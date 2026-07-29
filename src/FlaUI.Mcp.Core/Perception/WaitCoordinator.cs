using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

public sealed record WaitForResult(bool Satisfied, string? Ref, int ElapsedMs, string? SnapshotId);
public sealed record WaitStableResult(bool Stable, int ElapsedMs, string? SnapshotId);

/// <summary>Polling read-only wait conditions. Each poll issues ONE short query-STA Build with a
/// THROWAWAY RefRegistry (no durable-registry growth) and Task.Delays off-STA. Steady-state polling
/// culls offscreen subtrees (PollOptions) to bound per-poll cost. Two DECISION POINTS deliberately
/// walk without the spatial cull (UnculledPollOptions): confirming a `gone` before satisfying it, and
/// diagnosing an exists/enabled timeout. Once a `gone` confirmation proves an element is present but
/// culled, the call LATCHES to the unculled walk so the confirmation cannot double every poll.</summary>
public sealed class WaitCoordinator
{
    private readonly PerceptionManager _perception;
    public WaitCoordinator(PerceptionManager perception) => _perception = perception;

    // Test observability. WalkCount is the TOTAL walks issued by this coordinator.
    // PollWalkCount and ConfirmationWalkCount are split out because the latch's contract is only
    // assertable by distinguishing them: an UNLATCHED build issues one confirmation walk PER POLL,
    // a latched build issues exactly ONE for the whole call. Asserting on the total cannot tell the
    // two apart when the budget only affords a single poll -- which is how the original latch test
    // came to pass whether or not the latch existed. NOT thread-safe by design: one wait call owns
    // one coordinator in the tests that read these, and production never reads them.
    internal int WalkCount { get; private set; }
    internal int PollWalkCount { get; private set; }
    internal int ConfirmationWalkCount { get; private set; }
    private void CountWalk() => WalkCount++;
    private void CountPollWalk() { WalkCount++; PollWalkCount++; }
    private void CountConfirmationWalk() { WalkCount++; ConfirmationWalkCount++; }

    internal static bool Matches(SnapshotNode n, string by, string value) => by switch
    {
        "automationId" => string.Equals(n.AutomationId, value, System.StringComparison.Ordinal),
        "name" => string.Equals(n.Name, value, System.StringComparison.Ordinal),
        "controlType" => string.Equals(n.ControlType.ToString(), value, System.StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    internal static SnapshotOptions PollOptions => new() { InteractiveOnly = false, IncludeOffscreen = false };

    /// <summary>PollOptions with the spatial cull disabled and the IsOffscreen filter KEPT. Used only
    /// at a decision point — the `gone` confirmation and the exists/enabled timeout diagnostic — never
    /// for steady-state polling, so per-poll cost is unchanged.
    /// IncludeOffscreen MUST stay false here. Setting it true would disable BOTH filters, so the
    /// `gone` confirmation walk would find an IsOffscreen=true element, latch, and loop to timeout —
    /// breaking the spec's guarantee that such an element still satisfies `gone` instantly. The one
    /// place IncludeOffscreen=true is correct is the CALLER's opt-in in Task 7, which is a different
    /// options object; do not conflate the two.</summary>
    internal static SnapshotOptions UnculledPollOptions =>
        new() { InteractiveOnly = false, IncludeOffscreen = false, CullToWindowBounds = false };

    private static string Signature(IEnumerable<SnapshotNode> nodes, bool includeText)
        => string.Join("\n", nodes.Select(n => includeText
            ? $"{n.ControlType}:{n.AutomationId}:{n.Depth}:{n.Name}"
            : $"{n.ControlType}:{n.AutomationId}:{n.Depth}"));

    private static IReadOnlyList<SnapshotNode> Subtree(SnapshotModel model, string? by, string? value)
    {
        var nodes = model.Nodes.ToList();
        if (string.IsNullOrEmpty(by) || string.IsNullOrEmpty(value)) return nodes;
        int start = nodes.FindIndex(n => Matches(n, by, value));
        if (start < 0) return System.Array.Empty<SnapshotNode>();
        var scope = nodes[start]; var sub = new List<SnapshotNode> { scope };
        for (int i = start + 1; i < nodes.Count && nodes[i].Depth > scope.Depth; i++) sub.Add(nodes[i]);
        return sub;
    }

    public async Task<WaitStableResult> WaitForStableAsync(WindowHandle handle, string? by, string? value,
        bool includeText, int quietMs, int timeoutMs, int pollIntervalMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int needed = (int)System.Math.Ceiling((double)quietMs / System.Math.Max(1, pollIntervalMs));
        string? last = null; int stableCount = 0;
        bool scopeRequested = !string.IsNullOrEmpty(by) && !string.IsNullOrEmpty(value);
        while (true)
        {
            CountWalk();
            var (_, model) = await _perception.BuildModelAsync(handle, PollOptions, new RefRegistry());
            var sub = Subtree(model, by, value);
            if (scopeRequested && sub.Count == 0)
                throw new ToolException(ToolErrorCode.SelectorNoMatch, $"No element matched {by}={value} to scope stability.", "widen or correct the selector");
            var sig = Signature(sub, includeText);
            stableCount = sig == last ? stableCount + 1 : 0; last = sig;
            if (stableCount >= needed)
            {
                CountWalk();
                var (snapId, _) = await _perception.SnapshotModelForWaitAsync(handle, PollOptions);
                return new WaitStableResult(true, (int)sw.ElapsedMilliseconds, snapId);
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) return new WaitStableResult(false, (int)sw.ElapsedMilliseconds, null);
            await Task.Delay(pollIntervalMs);
        }
    }

    public async Task<WaitForResult> WaitForAsync(WindowHandle handle, string by, string value,
        string until, string? equals, int timeoutMs, int pollIntervalMs)
    {
        if (until == "valueEquals" && equals is null)
            throw new ToolException(ToolErrorCode.InvalidArguments, "until:valueEquals requires 'equals'.", "pass equals=<expected value>");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool latched = false; // per-call; never outlives this wait
        while (true)
        {
            bool satisfied;
            if (until == "valueEquals")
            {
                var (found, live) = await _perception.EvaluateSelectorValueAsync(handle, by, value);
                satisfied = found && string.Equals(live, equals, System.StringComparison.Ordinal);
            }
            else
            {
                var pollOptions = latched ? UnculledPollOptions : PollOptions;
                CountPollWalk();
                var (_, model) = await _perception.BuildModelAsync(handle, pollOptions, new RefRegistry());
                var match = model.Nodes.FirstOrDefault(n => Matches(n, by, value));
                satisfied = until switch
                {
                    "exists" => match is not null,
                    "gone" => match is null,
                    "enabled" => match is { Enabled: true },
                    _ => match is not null
                };

                // `gone` is the one predicate where the spatial cull causes a false POSITIVE: a culled
                // element is absent from the model, so `match is null` reports it destroyed when it is
                // merely clipped. Before satisfying, confirm against a walk without the spatial cull.
                // Skipped when the poll walk is ALREADY unculled (latched, or the caller opted in) —
                // the confirmation would be byte-identical to the poll we just did.
                // Deliberately NOT budget-guarded. Gating this on remaining time was tried and
                // withdrawn: it cannot distinguish "never confirmed" from "confirmed absent" (the
                // clock advances during the confirmation walk itself), and on any host where one
                // walk exceeds the whole budget it makes `gone` unsatisfiable at realistic timeouts.
                // Overshooting timeoutMs by one bounded walk beats an unusable predicate.
                if (satisfied && until == "gone" && !latched)
                {
                    CountConfirmationWalk();
                    bool stillThere;
                    try
                    {
                        var (_, unculled) = await _perception.BuildModelAsync(handle, UnculledPollOptions, new RefRegistry());
                        stillThere = unculled.Nodes.Any(n => Matches(n, by, value));
                    }
                    catch
                    {
                        // Could not confirm. Do NOT satisfy on an unconfirmed `gone` -- that is the exact
                        // false positive this confirmation exists to prevent, and a transient fault on a
                        // single node would reproduce it. Treat this poll as unsatisfied and let the loop
                        // retry; if the window is genuinely gone, the next poll's walk throws and
                        // propagates, which is this method's existing behaviour for a vanished window.
                        // (An earlier comment here claimed the catch stopped a completed wait becoming a
                        // throw. It never did: the satisfy block's SnapshotModelForWaitAsync is unguarded.)
                        stillThere = true;
                    }

                    if (stillThere)
                    {
                        satisfied = false;
                        // LATCH: this element is present-but-culled, so every later poll would repeat
                        // this same double walk. Switch to the unculled walk for the rest of the call —
                        // one walk per poll, not two, and only after the expensive case is proven.
                        latched = true;
                    }
                }
            }
            if (satisfied)
            {
                // The satisfy snapshot must use options CONSISTENT with how the decision was made, or a
                // wait can succeed and hand back a null ref. Two paths decide without the spatial cull:
                // `valueEquals`, whose evaluator (PerceptionManager.EvaluateSelectorValueAsync) filters
                // IsOffscreen but applies NO bounding-rect cull, and any latched call, which by then is
                // polling unculled. Re-culling here would drop the very element we just matched.
                var satisfyOptions = (until == "valueEquals" || latched) ? UnculledPollOptions : PollOptions;
                CountWalk();
                var (snapId, real) = await _perception.SnapshotModelForWaitAsync(handle, satisfyOptions);
                var realMatch = real.Nodes.FirstOrDefault(n => Matches(n, by, value));
                return new WaitForResult(true, realMatch?.Ref, (int)sw.ElapsedMilliseconds, snapId);
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) return new WaitForResult(false, null, (int)sw.ElapsedMilliseconds, null);
            await Task.Delay(pollIntervalMs);
        }
    }
}
