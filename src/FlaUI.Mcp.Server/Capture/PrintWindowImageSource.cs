using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Server.Capture;

/// <summary>The real PrintWindow acquisition. The ONLY production file in this feature that touches
/// Win32, which is what keeps the crop, the guards and the detectors headless-testable.
///
/// ⚠ PW_RENDERFULLCONTENT (flag 2) is MANDATORY -- but read the next paragraph before repeating why.
/// The spec's evidence row F1 records that flag 0 returns blank for four of five window classes probed
/// (DirectX/Atlas, XAML/UWP, WPF and the shell). That is INHERITED evidence, gathered elsewhere.
///
/// ⚠ F1 DOES NOT REPRODUCE ON THE REFERENCE MACHINE, and the doc comment used to assert it as though it
/// did. MEASURED 2026-08-21 against the WPF TestApp, same acquisition code, flag as the only variable:
///   flag 0 -> 100 distinct colours, mean luminance 233.3, NOT uniform
///   flag 2 -> 110 distinct colours, mean luminance 236.4, NOT uniform
///   the two differ on 10% of sampled pixels
/// Flag 0 rendered the CLIENT AREA correctly -- every control, every label, all text. The 10% is entirely
/// the NON-CLIENT chrome: flag 0 draws the legacy title bar, flag 2 the modern DWM-composed one.
///
/// ⚠ AND THE CLIENT AREA IS NOT SHIFTED, which matters because a shift would be a LEAK. A per-row diff
/// puts 100% of the difference in rows 0-30 (the title bar) and 1.6% in the rows below (the side
/// borders); a client-area landmark sits at y=77 in BOTH captures, delta 0. So masks translated by the
/// window origin land identically under either flag on this build. A review seat argued the opposite --
/// that thicker legacy chrome would shift the client area 2-10px and misalign every mask -- and that was
/// checked and REFUTED by the row profile above.
///
/// The flag stays: it is the correct constant for hardware-accelerated targets this machine cannot
/// exercise, and it renders the chrome the way the screen actually shows it. What changed is that this
/// comment no longer claims a local measurement it does not have.
///
/// ⚠⚠ CONSEQUENCE FOR THE TEST BELOW: the flag is NOT PINNED BY ANY TEST. Its only mutant -- flag 2 to 0
/// -- leaves both captures non-uniform, so the "not a single colour" assertion cannot fail. Tracked as
/// coverage debt A5. What that test DOES prove is still worth having: the P/Invoke signatures are right,
/// the GDI handle lifecycle neither leaks nor crashes, and the unmanaged-to-managed copy produces a
/// correctly-sized, non-blank bitmap from a real window.
///
/// ⚠ THE BOOL RETURN IS WORTHLESS AS A SUCCESS SIGNAL. MEASURED: ret=True on every call, including every
/// all-black one. No failure handling here may be driven by it.
///
/// ⚠ GDI DOES NOT THROW. CreateCompatibleDC, CreateCompatibleBitmap and SelectObject return null/zero
/// handles on failure, so the scrape path's COMException/ExternalException catch never fires on this path
/// and nothing else would take its place. Every handle is checked.</summary>
public sealed class PrintWindowImageSource : IWindowImageSource
{
    private const uint PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    public Bitmap? Acquire(IntPtr hwnd, Size size, int timeoutMs)
    {
        if (hwnd == IntPtr.Zero)
            throw new ToolException(ToolErrorCode.CaptureUnavailable,
                "The target window has no native handle to capture.",
                "re-list windows and retry against a live handle");
        if (size.Width <= 0 || size.Height <= 0)
            throw new ToolException(ToolErrorCode.CaptureUnavailable,
                "The target window has no renderable area to allocate.",
                "restore or resize the window, then retry");

        Bitmap? result = null;
        // ⚠⚠ Exception, NOT ToolException. See the catch below — narrowing this type is what made a
        // GDI+ fault lethal to the whole process.
        Exception? failure = null;
        // ⚠ THE ABANDONED THREAD MUST DISPOSE ITS OWN BITMAP. If the hung target eventually processes
        // WM_PRINT, the abandoned thread unblocks, finishes Render(), and allocates a managed Bitmap
        // wrapping GDI+ resources -- for a caller that returned long ago and will never dispose it. Those
        // accumulate against the process's 10,000-handle GDI ceiling and are reclaimed only whenever the
        // GC gets round to finalizing them, which is not a schedule this server can rely on.
        //
        // This does NOT make the leak go away: the thread, the HDC and the GDI bitmap held INSIDE a call
        // that is still blocked are unreachable either way. What it removes is the ONE resource that
        // becomes reclaimable after the fact and was being dropped anyway.
        // *(AGY-AFTER panel over this plan, round 2, Resource Vampire.)*
        var handoff = new object();
        var abandoned = false;

        // ⚠ A DEDICATED BACKGROUND THREAD, NOT Task.Run. PrintWindow renders by sending WM_PRINT to the
        // TARGET synchronously, so a target whose message loop is blocked blocks this call with no
        // cancellation. On a threadpool thread that consumes a bounded CLR slot and a hung target could
        // degrade every OTHER tool in the server. IsBackground keeps a leaked thread from blocking exit.
        //
        // ⚠ THIS CONTAINS; IT DOES NOT RECLAIM. A blocked call still holds its thread, its HDC, its GDI
        // bitmap and its managed bitmap FOREVER -- a blocked Win32 call cannot be cancelled. Only killing
        // a separate process reclaims those, and that is filed as ROADMAP debt rather than built here.
        // The per-HWND circuit breaker in WindowCaptureCoordinator is what bounds the cumulative cost.
        var t = new Thread(() =>
        {
            Bitmap? produced = null;
            // ⛔⛔ CATCH EVERYTHING, ON THIS THREAD, OR A GDI+ FAULT KILLS THE SERVER. This read
            // `catch (ToolException ex)`, and an escaping exception from a background thread delegate
            // TERMINATES the .NET process — there is no outer handler to reach.
            //
            // `Render`'s raw GDI calls genuinely do not throw (they return null handles, which is why
            // every one is checked), and that is what the summary above says. But the last two lines of
            // `Render` are **GDI+**, not GDI: `Image.FromHbitmap` throws `ExternalException` on failure
            // and `new Bitmap(shared)` throws `OutOfMemoryException` — GDI+ reports many allocation
            // failures that way — for a window large enough that its bitmap will not fit. Neither is a
            // `ToolException`, so both escaped.
            //
            // Worse, the failure would have looked like a TIMEOUT: the thread dies, `Join` returns true
            // because the thread finished, `failure` is null, `result` is null, and the caller reports a
            // timed-out acquisition — while the process is already tearing down underneath it.
            //
            // ⚠ THIS DELIBERATELY CATCHES `OutOfMemoryException`, against the repo idiom
            // (`catch (Exception ex) when (ex is not OutOfMemoryException ...)`, e.g. CaptureAuditSignal).
            // That idiom is right when the alternative is swallowing a real OOM; here the alternative is
            // KILLING THE PROCESS. A GDI+ OOM for one oversized bitmap is a local, recoverable condition,
            // and surfacing it as a refusal on the caller's thread is strictly better than dying.
            // *(AGY-CAPSTONE round 1, finding 3.)*
            try { produced = Render(hwnd, size); }
            catch (Exception ex) { failure = ex; }
            lock (handoff)
            {
                // The caller already gave up: nobody will ever dispose this, so dispose it here.
                if (abandoned) produced?.Dispose();
                else result = produced;
            }
        }) { IsBackground = true };
        t.Start();

        if (!t.Join(timeoutMs))
        {
            // TIMED OUT. The thread is abandoned and whatever the blocked call holds is unreclaimable --
            // but anything it produces AFTER this point is now its own to release.
            lock (handoff)
            {
                abandoned = true;
                // ⚠⚠ AND WHATEVER IT PUBLISHED IN THE GAP IS OURS. Join expiring and this lock being
                // taken are not one atomic step: the thread can finish in between, see `abandoned` still
                // false, and publish into `result` -- for a caller that is about to return null and will
                // never look at it again. Setting the flag alone NARROWS that window without closing it,
                // which is what an earlier version of this fix did. Both orderings are now covered: either
                // the thread publishes first and we dispose here, or we set the flag first and it disposes
                // there.
                result?.Dispose();
                result = null;
            }
            return null;
        }
        // Thread.Join establishes happens-before, so `result` and `failure` are visible here without
        // further synchronisation.
        // A ToolException already carries a code and a recourse, so it is rethrown unchanged. Anything
        // else is wrapped rather than rethrown raw: the tool layer's contract is a code plus a recourse,
        // and `ToolException` has no inner-exception constructor, so the original type and message are
        // folded into the text where an operator can still read them.
        if (failure is ToolException toolFailure) throw toolFailure;
        if (failure is not null)
            throw new ToolException(ToolErrorCode.CaptureUnavailable,
                // ⚠ `GetType()`, not `GetType().Name`. RedactionSurfaceInventoryTests forbids a bare
                // `.Name` read outside its listed egress members - it cannot tell a TYPE name from a UIA
                // element's Name, and a sweep that guards redaction is right to be conservative. Do not
                // "tidy" this back; weakening the sweep to fit one message is the wrong trade.
                $"The capture failed inside the rendering thread ({failure.GetType()}: {failure.Message}).",
                "retry; if it repeats, capture a specific element rather than the whole window");
        return result;
    }

    private static Bitmap Render(IntPtr hwnd, Size size)
    {
        IntPtr windowDc = IntPtr.Zero, memDc = IntPtr.Zero, hbm = IntPtr.Zero, prev = IntPtr.Zero;
        try
        {
            windowDc = GetWindowDC(hwnd);
            if (windowDc == IntPtr.Zero) throw Gdi();
            memDc = CreateCompatibleDC(windowDc);
            if (memDc == IntPtr.Zero) throw Gdi();
            hbm = CreateCompatibleBitmap(windowDc, size.Width, size.Height);
            if (hbm == IntPtr.Zero) throw Gdi();
            prev = SelectObject(memDc, hbm);
            if (prev == IntPtr.Zero) throw Gdi();

            // The BOOL is deliberately ignored: MEASURED as True on every call including every blank one.
            _ = PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);

            // Copy out before the GDI objects are released.
            using var shared = Image.FromHbitmap(hbm);
            return new Bitmap(shared);
        }
        finally
        {
            if (prev != IntPtr.Zero) SelectObject(memDc, prev);
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (windowDc != IntPtr.Zero) ReleaseDC(hwnd, windowDc);
        }
    }

    private static ToolException Gdi() => new(ToolErrorCode.CaptureUnavailable,
        "GDI could not allocate the resources for this capture (handle exhaustion is the usual cause).",
        "close some windows to free GDI handles, then retry");
}
