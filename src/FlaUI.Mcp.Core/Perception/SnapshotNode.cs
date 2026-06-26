using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One rendered item: an element node or a structural marker ([Active Overlays] /
/// depth-limit). Render(...) reproduces byte-for-byte legacy text; Stats/Diff/stability consume
/// only the element nodes. Value is intentionally absent — reading ValuePattern.Value on every
/// node freezes the STA on virtualized grids; diff compares Name/Enabled/Focused.</summary>
public abstract record SnapshotItem;

public sealed record SnapshotNode(
    string Ref,
    int Depth,
    string Indent,
    ControlType ControlType,
    string AutomationId,
    string Name,
    System.Drawing.Rectangle Bounds,
    bool Enabled,
    bool Focusable,
    bool Focused,
    bool IsPassword,
    bool IsOffscreen,
    IReadOnlyList<int> RuntimeId,
    IReadOnlyList<string> Patterns,
    string HelpText) : SnapshotItem;

public sealed record OverlaysHeaderItem : SnapshotItem;
public sealed record DepthLimitItem(string Indent, int MoreCount, int MaxDepth) : SnapshotItem;

public sealed record SnapshotModel(IReadOnlyList<SnapshotItem> Items)
{
    public IEnumerable<SnapshotNode> Nodes => Items.OfType<SnapshotNode>();
    public int NodeCount => Items.Count(i => i is SnapshotNode);
}
