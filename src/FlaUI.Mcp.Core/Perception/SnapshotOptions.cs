namespace FlaUI.Mcp.Core.Perception;

public sealed record SnapshotOptions
{
    /// <summary>Optional ref (from a prior snapshot of the same window) to root the walk at.</summary>
    public string? RootRef { get; init; }
    public int MaxDepth { get; init; } = 40;
    /// <summary>Prune non-interactive container/decoration noise (Playwright-style). Default true.</summary>
    public bool InteractiveOnly { get; init; } = true;
    /// <summary>Append AutomationId/HelpText to each line. Default false.</summary>
    public bool FullProperties { get; init; } = false;
    /// <summary>Include elements UIA reports as off-screen (scrolled/virtualized out of view).
    /// Default false: off-screen subtrees are culled for privacy and token economy — the agent
    /// should perceive what the user can see. Opt in to reach scrolled-off-but-real elements.</summary>
    public bool IncludeOffscreen { get; init; } = false;
}
