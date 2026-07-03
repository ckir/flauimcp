namespace FlaUI.Mcp.Core.Perception;

/// <summary>A serialized snapshot. SnapshotId is stable wire surface for desktop_snapshot_diff. Wakeable (Phase 9
/// §3) is true only for an opaque Chromium/Electron window that would benefit from desktop_wake_accessibility.</summary>
public sealed record SnapshotResult(string SnapshotId, string Tree, int NodeCount, bool Wakeable = false);
