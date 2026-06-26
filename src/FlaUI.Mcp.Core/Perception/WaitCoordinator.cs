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
