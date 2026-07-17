namespace FlaUI.Mcp.Core.Perception;

public sealed record NodeState(string Name, bool Enabled, bool Focused, bool Selected);
public sealed record DiffDescriptor(string Ref, string ControlType, string AutomationId, string Name);
public sealed record ChangedEntry(string Ref, NodeState Was, NodeState Now);
public sealed record SnapshotDiffResult(string BaselineSnapshotId, string CurrentSnapshotId,
    IReadOnlyList<DiffDescriptor> Added, IReadOnlyList<DiffDescriptor> Removed, IReadOnlyList<ChangedEntry> Changed);

public static class SnapshotDiff
{
    /// <summary>The single composite-identity contract used by BOTH the diff (node identity) and the
    /// scoped-diff subtree slice. RuntimeId when present (stable across a re-walk), else Name.</summary>
    internal static string IdentityKey(FlaUI.Core.Definitions.ControlType ct, string automationId,
        IReadOnlyList<int> runtimeId, string name)
        => runtimeId.Count > 0
            ? $"{ct}|{automationId}|rid:{string.Join(",", runtimeId)}"
            : $"{ct}|{automationId}|name:{name}";

    private static string Identity(SnapshotNode n) => IdentityKey(n.ControlType, n.AutomationId, n.RuntimeId, n.Name);

    /// <summary>The Name as it may appear on the wire (added/removed/changed output): "[REDACTED]" for an
    /// IsPassword element, matching the snapshot render (SnapshotEngine.cs:131) so a diff never becomes a
    /// name-oracle that plain snapshot already redacts (INV-5). Identity keying keeps the RAW name (internal,
    /// never serialized) so a password node still matches itself across baseline/current.</summary>
    private static string ShownName(SnapshotNode n) => n.IsPassword ? "[REDACTED]" : n.Name;
    private static DiffDescriptor Desc(SnapshotNode n) => new(n.Ref, n.ControlType.ToString(), n.AutomationId, ShownName(n));
    private static NodeState State(SnapshotNode n) => new(ShownName(n), n.Enabled, n.Focused, n.Selected);

    /// <summary>Slice a cached model down to the subtree rooted at the node matching the scope
    /// descriptor's identity (same IdentityKey as the diff), by pre-order depth walk: the matched
    /// node plus all following SnapshotNodes with a greater Depth, stopping at the first node whose
    /// Depth is &lt;= the scope's (a sibling/ancestor) or a non-node marker. Missing scope => empty
    /// (its whole current-side subtree then reads as "added"). Depth-agnostic Compute means the
    /// slice's original depths are fine to keep.</summary>
    public static SnapshotModel Subtree(SnapshotModel model, ElementDescriptor scope)
    {
        string wantId = IdentityKey(scope.ControlType, scope.AutomationId, scope.RuntimeId, scope.Name);

        var items = model.Items;
        int start = -1, scopeDepth = 0;
        for (int i = 0; i < items.Count; i++)
            if (items[i] is SnapshotNode n && Identity(n) == wantId) { start = i; scopeDepth = n.Depth; break; }
        if (start < 0) return new SnapshotModel(System.Array.Empty<SnapshotItem>());

        var slice = new List<SnapshotItem> { items[start] };
        for (int i = start + 1; i < items.Count; i++)
        {
            if (items[i] is not SnapshotNode n) break;      // marker (overlays/depth-limit) ends the subtree
            if (n.Depth <= scopeDepth) break;               // sibling or ancestor - subtree done
            slice.Add(n);
        }
        return new SnapshotModel(slice);
    }

    public static SnapshotDiffResult Compute(string baselineId, SnapshotModel baseline, string currentId, SnapshotModel current)
    {
        var baseById = new Dictionary<string, SnapshotNode>();
        foreach (var n in baseline.Nodes) baseById[Identity(n)] = n;
        var curById = new Dictionary<string, SnapshotNode>();
        foreach (var n in current.Nodes) curById[Identity(n)] = n;
        var added = current.Nodes.Where(n => !baseById.ContainsKey(Identity(n))).Select(Desc).ToList();
        var removed = baseline.Nodes.Where(n => !curById.ContainsKey(Identity(n))).Select(Desc).ToList();
        var changed = new List<ChangedEntry>();
        foreach (var n in current.Nodes)
        {
            if (!baseById.TryGetValue(Identity(n), out var b)) continue;
            var was = State(b); var now = State(n);
            if (was != now) changed.Add(new ChangedEntry(n.Ref, was, now));
        }
        return new SnapshotDiffResult(baselineId, currentId, added, removed, changed);
    }
}
