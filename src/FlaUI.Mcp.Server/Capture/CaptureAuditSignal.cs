using System;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Capture;

/// <summary>§2.6. Tells the person at the console that a capture read a window they cannot see.
///
/// WHY IT EXISTS: the process denylist protects named APPLICATIONS. What item 8 actually widens is the
/// operator's spatial assumption that what is not on the screen is not being read, and a denylist has
/// nothing to say about that. `captureMethod` records every occlusion-bypassing capture -- but it records
/// it to the AGENT, so it audits nothing for the human.
///
/// ⚠ THE TRIGGER OVER-SIGNALS, DELIBERATELY. A non-foreground window can be perfectly visible
/// side-by-side, so this fires on captures that revealed nothing hidden. It never MISSES a covered
/// window, which is the direction that matters. Computing true occlusion needs a hit-test this server
/// does not do, and it was rejected as disproportionate.
///
/// ⚠ WHICH IS WHY IT IS OFF BY DEFAULT. Agents screenshot non-foreground windows constantly -- that is
/// the tool's normal use -- so a signal on by default would be continuous noise, and a notification
/// everyone learns to ignore is worse than none (the same AB-9 reasoning §3 turns on).
///
/// ⚠ BEST-EFFORT, NEVER THROWS. IAttentionSignal's own contract requires it: a failed signal must not
/// turn a tool result into an error. CompositeAttentionSignal already swallows a faulting child; this
/// catches anyway, because the seam it is handed may not be a Composite.</summary>
public sealed class CaptureAuditSignal
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly IAttentionSignal _signal;
    private readonly bool _enabled;
    private readonly Func<IntPtr> _foregroundProbe;

    public CaptureAuditSignal(IAttentionSignal signal, bool enabled, Func<IntPtr>? foregroundProbe = null)
    { _signal = signal; _enabled = enabled; _foregroundProbe = foregroundProbe ?? GetForegroundWindow; }

    /// <summary>Canonical step 10. Call AFTER the result is final -- never before, because a signal
    /// raised earlier can appear in the captured pixels on the scrape path, and GdiActionOverlay in
    /// particular draws a real top-most window on the screen.</summary>
    public void SignalIfOcclusionBypassed(WindowHandle handle, IntPtr hwnd, string captureMethod)
    {
        if (!_enabled) return;
        // A scrape read only what was already on the screen. Nothing was bypassed, so nothing to audit.
        if (!string.Equals(captureMethod, "printWindow", StringComparison.Ordinal)) return;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (_foregroundProbe() == hwnd) return;   // visible to the operator by definition
            if (_signal.Enabled) _signal.Signal(handle);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        { _ = ex; /* best-effort: never throw from the signal path */ }
    }
}
