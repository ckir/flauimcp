using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

public sealed record WaitForResult(bool Satisfied, string? Ref, int ElapsedMs, string? SnapshotId);
public sealed record WaitStableResult(bool Stable, int ElapsedMs, string? SnapshotId);

/// <summary>Polling read-only wait conditions. Each poll issues ONE short query-STA Build with a
/// THROWAWAY RefRegistry (no durable-registry growth) and Task.Delays off-STA. Offscreen subtrees
/// are culled (IncludeOffscreen=false) to bound per-poll cost. (Wait methods added in later tasks.)</summary>
public sealed class WaitCoordinator
{
    private readonly PerceptionManager _perception;
    public WaitCoordinator(PerceptionManager perception) => _perception = perception;

    internal static bool Matches(SnapshotNode n, string by, string value) => by switch
    {
        "automationId" => string.Equals(n.AutomationId, value, System.StringComparison.Ordinal),
        "name" => string.Equals(n.Name, value, System.StringComparison.Ordinal),
        "controlType" => string.Equals(n.ControlType.ToString(), value, System.StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    internal static SnapshotOptions PollOptions => new() { InteractiveOnly = false, IncludeOffscreen = false };

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
            var (_, model) = await _perception.BuildModelAsync(handle, PollOptions, new RefRegistry());
            var sub = Subtree(model, by, value);
            if (scopeRequested && sub.Count == 0)
                throw new ToolException(ToolErrorCode.SelectorNoMatch, $"No element matched {by}={value} to scope stability.", "widen or correct the selector");
            var sig = Signature(sub, includeText);
            stableCount = sig == last ? stableCount + 1 : 0; last = sig;
            if (stableCount >= needed)
            {
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
                var (_, model) = await _perception.BuildModelAsync(handle, PollOptions, new RefRegistry());
                var match = model.Nodes.FirstOrDefault(n => Matches(n, by, value));
                satisfied = until switch
                {
                    "exists" => match is not null, "gone" => match is null, "enabled" => match is { Enabled: true }, _ => match is not null
                };
            }
            if (satisfied)
            {
                var (snapId, real) = await _perception.SnapshotModelForWaitAsync(handle, PollOptions);
                var realMatch = real.Nodes.FirstOrDefault(n => Matches(n, by, value));
                return new WaitForResult(true, realMatch?.Ref, (int)sw.ElapsedMilliseconds, snapId);
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) return new WaitForResult(false, null, (int)sw.ElapsedMilliseconds, null);
            await Task.Delay(pollIntervalMs);
        }
    }
}
