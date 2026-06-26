namespace FlaUI.Mcp.Core.Perception;

/// <summary>A serialized snapshot. SnapshotId is stable wire surface for the future
/// desktop_snapshot_diff (Phase 5); nothing consumes it yet.</summary>
public sealed record SnapshotResult(string SnapshotId, string Tree, int NodeCount);
