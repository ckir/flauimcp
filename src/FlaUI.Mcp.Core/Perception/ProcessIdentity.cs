using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>THE one way to answer "which process owns this element". Two consumers depend on the answer —
/// operator redaction rules and the shipped credential denylist — and BOTH now treat a null as fail-closed,
/// so this is a security-relevant primitive rather than a convenience.
///
/// ⚠⚠ IT EXISTS BECAUSE THERE WERE TWO OF IT. PerceptionManager and WatchPump each carried a private
/// SafeProcessName, the second one commented "mirrors PerceptionManager.SafeProcessName". They then drifted
/// exactly as duplicated decisions always do: a capstone fix taught one of them to recover a pid of 0 via
/// the window handle, the copy kept rejecting only pid &lt; 0, and the watch path started dropping events for
/// windows the snapshot path served happily. This is the same defect class that let an earlier fix land in
/// ElementContent.Classify while missing SnapshotEngine.Build for a whole review round.
/// Do not re-introduce a local copy. If a caller needs different behaviour, change it HERE, once.
///
/// ⚠ Deliberately NOT built on OpenProcess/QueryFullProcessImageName or a Toolhelp32 snapshot. MEASURED
/// from a NON-elevated caller: Process.GetProcessById(pid).ProcessName already resolves wininit, services,
/// csrss, lsass and Defender's MsMpEng, so elevated and protected processes are ALREADY covered and that
/// pipeline would be hundreds of lines of P/Invoke buying nothing. Nor does it pierce ApplicationFrameHost
/// to name the hosted app: that silently breaks an operator rule written against "ApplicationFrameHost"
/// and fails OPEN, which is strictly worse than the status quo.</summary>
internal static class ProcessIdentity
{
    /// <summary>The owning process's base name, or null when it genuinely cannot be determined.
    ///
    /// ⚠ A null answer FAILS CLOSED downstream (PerceptionPolicy.IsDenied denies it; a process-scoped
    /// redaction rule withholds rather than silently not-matching). Keeping the null rate low is therefore
    /// what stops that from over-redacting — which is the entire job of the handle fallback below.</summary>
    public static string? OfElement(AutomationElement el)
    {
        int pid;
        try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { pid = -1; }

        // pid 0 is the Idle process and never owns a window, so treat it as "not answered" rather than
        // looking it up. A UIA provider that reports 0 used to reach GetProcessById(0), throw, and yield
        // null — an unidentified window for a perfectly healthy one.
        if (pid <= 0)
        {
            try
            {
                nint hwnd = el.Properties.NativeWindowHandle.ValueOrDefault;
                if (hwnd != 0 &&
                    FlaUI.Mcp.Core.Interaction.Win32Interop.GetWindowThreadProcessId(hwnd, out uint upid) != 0 &&
                    upid != 0)
                    pid = (int)upid;
            }
            catch { /* no usable handle either: fall through to null, which fails CLOSED */ }
        }
        return OfPid(pid);
    }

    /// <summary>The owning process's base name for a PID we already have, or null when it cannot be
    /// determined. Null NEVER means "safe" — every policy consumer fails closed on it.
    ///
    /// ⚠ EXISTS BECAUSE I CLAIMED IT COULD NOT. Reviewing round 7, I argued that
    /// WindowManager.SafeProcessName(int) had to stay a separate resolver "by necessity, since one has an
    /// element and one only has a pid". The peer refuted it in one line: differing INPUTS do not require
    /// duplicating the RESOLUTION — expose the pid-based half and let the element-based half call it. The
    /// necessity was manufactured and the duplication voluntary, which is exactly the excuse that let two
    /// copies of this logic drift far enough to start dropping watch events.
    ///
    /// ⚠⚠ pid &lt;= 0 IS REJECTED, AND THIS IS A REAL BEHAVIOUR CHANGE — not merely a guard against a throw.
    /// MEASURED: `Process.GetProcessById(0)` does NOT throw. It returns the System Idle Process, whose
    /// ProcessName is "Idle". So before this guard existed, a window reporting pid 0 was listed with the
    /// process name "Idle", and `PerceptionPolicy.IsDenied("Idle")` is FALSE — it was served. It is now
    /// null, and null is DENIED.
    ///
    /// That is the correct answer: pid 0 is the Idle process, which owns no windows, so "Idle" was never a
    /// true attribution — it was a plausible-looking string standing in for "this pid is not real", which
    /// is the same shape as the "unknown" sentinel this type exists to have removed. But it IS a
    /// behaviour change, and the commit that introduced it described itself as only removing duplication.
    /// It was caught by a review seat asking whether the delegation altered the listing, and by MEASURING
    /// the answer rather than accepting that GetProcessById(0) throws — which both I and the reviewing
    /// peer had assumed, and which is false.
    ///
    /// A negative pid is our own "could not read" marker and never reaches a lookup.</summary>
    public static string? OfPid(int pid)
    {
        if (pid <= 0) return null;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }
}
