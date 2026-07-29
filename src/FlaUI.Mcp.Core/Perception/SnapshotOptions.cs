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
    /// <summary>Cull elements whose bounding rectangle does not intersect the WALK ROOT's rectangle
    /// (the window for a full-window walk; each popup's own rect for a grafted popup subtree).
    /// Default true = today's behaviour. Independent of IncludeOffscreen, which gates the separate
    /// UIA IsOffscreen property filter. Set false to keep the IsOffscreen filter while dropping the
    /// spatial cull — the wait paths do exactly that. Only consulted when IncludeOffscreen is false,
    /// because IncludeOffscreen=true already disables BOTH filters.</summary>
    public bool CullToWindowBounds { get; init; } = true;
}
