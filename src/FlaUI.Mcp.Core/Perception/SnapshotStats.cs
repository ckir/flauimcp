namespace FlaUI.Mcp.Core.Perception;

/// <summary>Cheap orientation data returned by desktop_snapshot_stats: control counts and
/// a per-ControlType histogram, derived from a full (non-pruned) tree walk or a cached model.</summary>
public sealed record SnapshotStats(
    string SnapshotId,
    int Total,
    int Interactive,
    int Offscreen,
    int Redacted,
    IReadOnlyDictionary<string, int> ByControlType);
