namespace FlaUI.Mcp.Core.Perception;

public sealed record NodeState(string Name, bool Enabled, bool Focused);
public sealed record DiffDescriptor(string Ref, string ControlType, string AutomationId, string Name);
public sealed record ChangedEntry(string Ref, NodeState Was, NodeState Now);
public sealed record SnapshotDiffResult(string BaselineSnapshotId, string CurrentSnapshotId,
    IReadOnlyList<DiffDescriptor> Added, IReadOnlyList<DiffDescriptor> Removed, IReadOnlyList<ChangedEntry> Changed);

public static class SnapshotDiff
{
    private static string Identity(SnapshotNode n)
        => n.RuntimeId.Count > 0
            ? $"{n.ControlType}|{n.AutomationId}|rid:{string.Join(",", n.RuntimeId)}"
            : $"{n.ControlType}|{n.AutomationId}|name:{n.Name}";
    private static DiffDescriptor Desc(SnapshotNode n) => new(n.Ref, n.ControlType.ToString(), n.AutomationId, n.Name);
    private static NodeState State(SnapshotNode n) => new(n.Name, n.Enabled, n.Focused);

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
