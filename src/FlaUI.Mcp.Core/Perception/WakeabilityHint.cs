using System;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Pure predicate for the desktop_snapshot 'wakeable' hint (Phase 9 §3). True iff the window is a
/// Chromium/Electron host (by Win32 ClassName) AND its accessibility tree is collapsed/empty (opaque) — i.e. it
/// WOULD benefit from desktop_wake_accessibility. A Chromium window that already exposes a rich tree (a screen
/// reader is active, or it launched --force-renderer-accessibility) is NOT flagged: there is nothing to wake.</summary>
public static class WakeabilityHint
{
    // §4 spike: opaque Chromium collapses to ~14-15 nodes (window frame + Min/Restore/Close + empty Panes);
    // hydrated is 200+. A generous boundary well below the hydrated count and above the opaque baseline.
    // CALIBRATION CAVEAT (whole-branch review): 20 was calibrated from desktop_snapshot_stats (unpruned:
    // opaque ~14, hydrated ~231). The live hint is computed against the CALLER's snapshot NodeCount, which
    // under desktop_snapshot's default interactiveOnly=true is PRUNED (smaller). A hydrated-but-sparse
    // Chromium window could therefore fall <=20 and spuriously read wakeable:true. Worst case is harmless:
    // a no-op wake suggestion on an already-accessible window (WakeRegistry tolerates duplicate wakes).
    // Fast-follow if precise: compute the predicate from a fixed InteractiveOnly=false walk.
    public const int CollapsedNodeThreshold = 20;

    public static bool IsWakeable(string? className, int nodeCount)
        => IsChromiumClass(className) && nodeCount <= CollapsedNodeThreshold;

    // Chromium/Electron top-level window class is "Chrome_WidgetWin_N" (N = 0/1). Case-insensitive prefix match.
    public static bool IsChromiumClass(string? className)
        => !string.IsNullOrEmpty(className)
           && className!.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);
}
