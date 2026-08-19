namespace FlaUI.Mcp.Core.Perception;

/// <summary>Cheap orientation data returned by desktop_snapshot_stats: control counts and
/// a per-ControlType histogram, derived from a full (non-pruned) tree walk or a cached model.
///
/// ⚠ <see cref="OsPasswordCount"/> and <see cref="RedactedCount"/> are DIFFERENT numbers and always were.
/// OsPasswordCount counts nodes the OS flagged IsPassword; RedactedCount counts every withheld node, from
/// either source. They were once called `Redacted` and `RedactedCount` — two same-rooted names for two
/// different quantities, which is a footgun by construction, so the ambiguous one was renamed before the
/// v1.0.0 contract froze it.</summary>
public sealed record SnapshotStats(
    string SnapshotId,
    int Total,
    int Interactive,
    int Offscreen,
    int OsPasswordCount,
    int RedactedCount,
    IReadOnlyDictionary<string, int> ByControlType);
