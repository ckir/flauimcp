namespace FlaUI.Mcp.Core.Perception;

/// <summary>Cheap orientation data returned by desktop_snapshot_stats: control counts and
/// a per-ControlType histogram, derived from a full (non-pruned) tree walk or a cached model.
///
/// ⚠ <see cref="Redacted"/> keeps its original, OS-password-only meaning for back-compat with an
/// existing consumer already keyed off it. <see cref="RedactedCount"/> is the true total across BOTH
/// redaction sources (OS + rule).</summary>
public sealed record SnapshotStats(
    string SnapshotId,
    int Total,
    int Interactive,
    int Offscreen,
    int Redacted,
    int RedactedCount,
    IReadOnlyDictionary<string, int> ByControlType);
