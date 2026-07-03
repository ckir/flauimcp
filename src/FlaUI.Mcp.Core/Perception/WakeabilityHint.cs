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
    public const int CollapsedNodeThreshold = 20;

    public static bool IsWakeable(string? className, int nodeCount)
        => IsChromiumClass(className) && nodeCount <= CollapsedNodeThreshold;

    // Chromium/Electron top-level window class is "Chrome_WidgetWin_N" (N = 0/1). Case-insensitive prefix match.
    public static bool IsChromiumClass(string? className)
        => !string.IsNullOrEmpty(className)
           && className!.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);
}
