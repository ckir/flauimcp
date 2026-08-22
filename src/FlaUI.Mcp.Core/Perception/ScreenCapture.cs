using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>A captured, masked, encoded image plus the metadata the tool layer projects.
///
/// ⚠ POSITIONAL RECORD, constructed positionally at the single site in Encode. APPEND ONLY, NEVER
/// INSERT: X/Y/W/H are four interchangeable ints, so a field added mid-list rebinds arguments silently
/// wherever the types line up. CaptureResultShapeTests pins the order for exactly that reason.
///
/// CaptureMethod is "printWindow" or "screenScrape". CaptureWarnings is ALWAYS present and is empty in
/// the normal case -- never null. A diagnostic that appears only on failure teaches consumers to ignore
/// its absence (AB-9).</summary>
public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions,
                                   string CaptureMethod, IReadOnlyList<CaptureWarning> CaptureWarnings);

/// <summary>Screen-region and per-window capture. Captures by absolute screen rectangle so it can run
/// OFF the query STA (spec §8). Paints black redaction rects (live bounds passed in), clamps width to a
/// hard ceiling, PNG-encodes.
///
/// TWO BACKENDS. CaptureRectangle SCRAPES a screen rectangle — used by full-desktop, by the OCR path,
/// and as the fallback when PrintWindow cannot deliver. CaptureWindow renders a window's OWN content
/// via PrintWindow, so it is unaffected by occlusion; window and element scope use it.
///
/// ⚠ The old promise that "headless/disconnected sessions are detected before capture so we never hand
/// back a black frame" NO LONGER HOLDS in general. The session guard still runs (ScreenshotTools.cs
/// refuses a non-renderable desktop), but PrintWindow can return a blank render for reasons no guard
/// catches — MEASURED: its BOOL return is True on every call, including every all-black one. That is
/// what the uniformCanvas / elementCanvasUniform / desktopCanvasUniform warnings exist to report.
///
/// ⚠ Callers no longer focus-first, and must not: the whole point is capturing without touching
/// focus.</summary>
public static class ScreenCapture
{
    private const int MaxCaptureWidth = 1920; // hard ceiling — bounds base64 payload even if maxWidth<=0

    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static bool IsDesktopRenderable()
    {
        var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return false;
        CloseDesktop(h); return true;
    }

    public static Rectangle VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Scrape a screen rectangle. Serves THREE KINDS of caller with divergent requirements
    /// (across FOUR call sites), and cannot tell them apart without being told, which is why `scope`
    /// has no default:
    ///   FullDesktop -- runs the desktopCanvasUniform detector.
    ///   OcrRegion   -- runs no detector; it is neither a whole desktop nor a PrintWindow capture.
    ///   Window/Element -- a FALLBACK scrape. Runs no detector, and its caller has already decided a
    ///     scrapeFallback* code, which arrives in `warningsSoFar`.
    ///
    /// ⚠ On this path `absolute` and `reported` are the SAME rectangle. The scrape captures exactly the
    /// region it was asked for, in one observation -- there is no W1/W2 pair, so the two cannot differ.
    /// Only the PrintWindow path derives them separately.</summary>
    /// <param name="source">TEST SEAM. Null means grab the real screen, which is what every production
    /// caller does. A non-null source lets a headless test reach the warning-emission logic below --
    /// see IScreenImageSource. Both paths funnel into the SAME AssembleScrape, so an injected run and a
    /// real run cannot diverge in the logic under test.</param>
    public static CaptureResult CaptureRectangle(Rectangle absolute, IReadOnlyList<Rectangle> redactAbsolute,
                                                 int maxWidth, CaptureScope scope,
                                                 IReadOnlyList<CaptureWarning> warningsSoFar,
                                                 IScreenImageSource? source = null)
    {
        if (source is not null)
        {
            using var injected = source.Acquire(absolute);
            return AssembleScrape(injected, absolute, redactAbsolute, maxWidth, scope, warningsSoFar);
        }

        CaptureImage cap;
        try { cap = Capture.Rectangle(absolute, null); }
        catch (System.Exception ex) when (ex is COMException or System.Runtime.InteropServices.ExternalException)
        { throw new ToolException(ToolErrorCode.CaptureUnavailable, "Screen capture failed (session may be disconnected/locked).", "reconnect to restore rendering"); }
        using (cap)
            return AssembleScrape(cap.Bitmap, absolute, redactAbsolute, maxWidth, scope, warningsSoFar);
    }

    /// <summary>Decide the scrape's warnings and encode. Split out of CaptureRectangle so the decision is
    /// reachable without a screen -- this is the logic ScrapeWarningEmissionTests exercises.
    ///
    /// ⚠ Does NOT dispose the bitmap. Ownership stays with whoever acquired it: the CaptureImage `using`
    /// on the real path, the `using var injected` on the seam path.</summary>
    internal static CaptureResult AssembleScrape(Bitmap bmp, Rectangle absolute,
                                                 IReadOnlyList<Rectangle> redactAbsolute, int maxWidth,
                                                 CaptureScope scope, IReadOnlyList<CaptureWarning> warningsSoFar)
    {
        var warnings = warningsSoFar;
        // The detector runs where the bitmap lives, so the seam cannot delegate this decision upward.
        if (scope.RunsDesktopUniformDetector() && UniformCanvasDetector.IsUniform(bmp))
            warnings = Append(warnings, CaptureWarnings.For(CaptureWarnings.DesktopCanvasUniform));
        // ⚠⚠ A WINDOW-SCOPE FALLBACK SCRAPE GETS THE WHOLE-IMAGE CHECK TOO, and without it this path
        // silently returned a black image -- recreating the exact defect the uniformCanvas widening
        // was folded to close. The TWO-STAGE detector genuinely cannot run on a scrape (there is no
        // full-window bitmap distinct from the captured region), but the FIRST stage alone needs no
        // second operand, and "this image is one colour" is as true and as useful here as it is for a
        // full desktop. *(AGY-AFTER panel over this plan, round 4, Protocol Pedant.)*
        //
        // ⚠ WINDOW SCOPE ONLY. On an ELEMENT-scope fallback the captured region is the ELEMENT, so
        // emitting uniformCanvas would state that the whole WINDOW rendered as one colour -- a claim
        // the tool cannot support and did not measure. That case stays uncovered, deliberately, and it
        // is the one gap this fold does not close. `An_element_scope_uniform_scrape_stays_silent` pins
        // that silence so it reads as a decision rather than an omission.
        else if (scope == CaptureScope.Window && UniformCanvasDetector.IsUniform(bmp))
            warnings = Append(warnings, CaptureWarnings.For(CaptureWarnings.UniformCanvas));
        return Encode(bmp, absolute, absolute, redactAbsolute, maxWidth, "screenScrape", warnings);
    }

    /// <summary>Append one warning. Never mutates the caller's list -- warnings travel INWARD only, and
    /// nobody unpacks a returned CaptureResult to add one.</summary>
    internal static IReadOnlyList<CaptureWarning> Append(IReadOnlyList<CaptureWarning> list, CaptureWarning w)
    {
        var next = new List<CaptureWarning>(list.Count + 1);
        next.AddRange(list);
        next.Add(w);
        return next;
    }

    /// <summary>Mask, downscale, PNG-encode, and assemble the CaptureResult.
    ///
    /// ⚠ TWO rectangles cross this boundary and they are NOT interchangeable.
    ///   `absolute` -- anchored to W1 -- drives the MASK arithmetic, because the mask rects arrive as
    ///     absolute screen coords sampled alongside the element during the same walk.
    ///   `reported` -- anchored to W2 -- becomes CaptureResult.X/Y, because that is where the pixels
    ///     actually are. They differ by exactly the movement delta whenever the window moved, and
    ///     conflating them makes the response a false statement about a moved window.
    /// Sizes are equal by construction (see WindowCropGeometry), so W/H are the same either way.
    ///
    /// ⚠ Encode ASSEMBLES; it does not DETECT. The uniform-canvas detectors run where their operands
    /// exist -- uniformCanvas on the full window bitmap before the crop, elementCanvasUniform on the
    /// cropped src after it, desktopCanvasUniform in the scrape seam. Encode never sees the uncropped
    /// bitmap and cannot evaluate the two-stage comparison.
    ///
    /// `clip` below is an INTERNAL LOCAL, recomputed per mask rect. It does not cross this boundary.</summary>
    internal static CaptureResult Encode(Bitmap src, Rectangle absolute, Rectangle reported,
                                         IReadOnlyList<Rectangle> redactAbsolute, int maxWidth,
                                         string method, IReadOnlyList<CaptureWarning> warnings)
    {
        int cap = maxWidth <= 0 ? MaxCaptureWidth : System.Math.Min(maxWidth, MaxCaptureWidth);
        double scale = src.Width > cap ? (double)cap / src.Width : 1.0;
        int outW = System.Math.Max(1, (int)System.Math.Round(src.Width * scale));
        int outH = System.Math.Max(1, (int)System.Math.Round(src.Height * scale));
        using var outBmp = new Bitmap(outW, outH);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.DrawImage(src, new Rectangle(0, 0, outW, outH));
            int painted = 0;
            using var black = new SolidBrush(Color.Black);
            foreach (var r in redactAbsolute)
            {
                if (!r.IntersectsWith(absolute)) continue; // off-crop field — don't count/paint
                var clip = Rectangle.Intersect(r, absolute);  // clip to the captured region
                // ⛔⛔ FLOOR THE ORIGIN, CEIL THE FAR EDGE -- never Round both independently. This read
                // `Round(x*scale)` for the origin and `Round(width*scale)` for the extent, and rounding
                // the two independently UNDER-COVERS: the painted rect can start inside the element's
                // left edge, or stop short of its right edge, leaving a sliver of a redacted field in
                // the clear.
                //
                // MEASURED over 11,700 (scale, x, width) combinations at scales 0.5-0.9: **75.6% of
                // cases under-covered**, by up to a full pixel. Example from that sweep: scale 0.5,
                // x=1, width=1 -- the element occupies [0.5, 1.0] after scaling, and Round gives
                // origin 0 with extent 0, painting nothing at all.
                //
                // Deriving both operands from floor(left) and ceil(right) makes under-coverage
                // impossible: MEASURED 0 of 14,040 cases under-cover, at a cost of up to 1.8px of
                // OVER-masking. That is the direction this codebase already chooses everywhere else --
                // over-masking is a cosmetic thickening, under-masking is a leak.
                // *(AGY-CAPSTONE round 2, finding 3.)*
                int relLeft = (int)System.Math.Floor((clip.X - absolute.X) * scale);
                int relTop = (int)System.Math.Floor((clip.Y - absolute.Y) * scale);
                var rel = new Rectangle(
                    relLeft, relTop,
                    (int)System.Math.Ceiling((clip.X - absolute.X + clip.Width) * scale) - relLeft,
                    (int)System.Math.Ceiling((clip.Y - absolute.Y + clip.Height) * scale) - relTop);
                if (rel.Width <= 0 || rel.Height <= 0) continue;
                g.FillRectangle(black, rel); painted++;
            }
            using var ms = new MemoryStream();
            outBmp.Save(ms, ImageFormat.Png);
            return new CaptureResult(ms.ToArray(), reported.X, reported.Y, reported.Width, reported.Height,
                                     scale, painted, method, warnings);
        }
    }

    /// <summary>Acquire one window's own pixels via the injected source, guard the capture-time target
    /// state, detect a resize, crop, and encode. Canonical steps 4-8.
    ///
    /// ⚠ It does NOT own the retry loop or the scrape fallback. Retrying means re-walking the UIA tree
    /// for a fresh W1/E/mask set, and that walk lives in PerceptionManager -- a seam handed a finished
    /// CaptureGeometry has no way to perform it. This method REPORTS a mismatch; it does not resolve one.
    ///
    /// ⚠ ORDER IS LOAD-BEARING. The terminal target-state guards run BEFORE the resize branch, because a
    /// window that minimizes mid-capture ALSO changes size: if resize won, a minimized window would be
    /// routed to retry-and-fallback and the scrape would photograph the rect it used to occupy.
    ///
    /// w2Probe and minimizedProbe are injected so the whole method is headless-testable; production
    /// passes the real Win32 reads.</summary>
    public static CaptureOutcome CaptureWindow(
        CaptureGeometry geo, int maxWidth, CaptureScope scope,
        IReadOnlyList<CaptureWarning> warningsSoFar, IWindowImageSource source, int timeoutMs,
        System.Func<System.IntPtr, Rectangle?>? w2Probe = null,
        System.Func<System.IntPtr, bool>? minimizedProbe = null)
    {
        // Canonical steps 4-5. Extracted so EVERY path about to photograph a named window runs them --
        // including the coordinator's circuit-breaker path, which skips this method entirely.
        var w2 = GuardTargetState(geo.NativeWindowHandle, w2Probe, minimizedProbe);

        // A degenerate W2 is REPORTED, not thrown: the window has no renderable area right now, which an
        // animating window can be true of for a single frame. Same treatment as a degenerate W1.
        if (w2.Width <= 0 || w2.Height <= 0)
            return CaptureOutcome.Transient("The target window reported no renderable area.");

        var w1 = geo.WindowBounds;
        var warnings = warningsSoFar;

        // Step 6, the resize check. Sizes, not rectangles: a pure move changes only the origin and the
        // crop already makes it harmless, so flagging it would be a false positive.
        // ⚠⚠ A RESIZE ALWAYS REPORTS, ON EVERY SCOPE, REGARDLESS OF HOW MANY MASKS THE WALK FOUND.
        //
        // An earlier version let window scope CONTINUE when `geo.MaskRects.Count == 0`, reasoning that
        // "nothing was going to be redacted, so no misalignment is possible". The misalignment half was
        // true and the conclusion was a LEAK, because **an empty mask set does not mean "nothing in this
        // window is sensitive" — it means "nothing sensitive was found in the T1 LAYOUT".** A resize
        // reflows: content can move into the cropped region, or be CREATED by the resize itself (a dialog
        // that expands and reveals a credential field). Those pixels are in the image and no mask was ever
        // computed for them. The crop only discards the area a GROWN window ADDED; it does nothing about
        // content that reflowed INTO the region that was already being captured.
        //
        // Reporting instead of continuing costs a retry on a benign case and settles it on attempt 2 with
        // a FRESH walk whose mask set includes anything the reflow produced.
        // *(AGY-AFTER panel over this plan, round 11, Leak Hunter.)*
        if (w1.Size != w2.Size) return CaptureOutcome.Resized;

        // Step 6b. Allocate at W2's size and render.
        using var bitmap = source.Acquire(geo.NativeWindowHandle, w2.Size, timeoutMs);
        if (bitmap is null) return CaptureOutcome.TimedOut;

        // uniformCanvas: evaluated on the FULL window bitmap, between 6b and 7. §3 requires it to see the
        // uncropped image, which does not exist after step 7.
        // ⚠ BOTH SCOPES, not window only. uniformCanvas is a statement about the WINDOW BITMAP, and that
        // bitmap exists on element scope too. Scoping it to window scope left a HOLE: an element-scope
        // capture of a window that failed to render emitted NOTHING -- uniformCanvas excluded by scope,
        // and elementCanvasUniform fires only when the crop is uniform AND the window bitmap was not. A
        // black crop, silently, in breach of the design's own success criterion. See §5's note.
        bool windowUniform = UniformCanvasDetector.IsUniform(bitmap);
        if (windowUniform)
            warnings = Append(warnings, CaptureWarnings.For(CaptureWarnings.UniformCanvas));

        // Step 7, the crop. Its empty case is reachable only when the sizes AGREE, because the resize
        // check above already left the sequence otherwise.
        var crop = WindowCropGeometry.Compute(new Size(bitmap.Width, bitmap.Height), geo.Bounds, w1, w2);
        if (crop is null)
            // ⚠ RETRYABLE, and an earlier version of this plan refused here on the FIRST occurrence.
            // Reachable only when W1.Size == W2.Size, so it means a provider reported an element outside
            // its own window -- and the design's own justification for calling that "pathological rather
            // than impossible" is that "this repo's mask-escalation machinery exists because UIA does
            // report inconsistent rectangles". A momentarily bad ELEMENT rect is therefore the same class
            // of transient as a momentarily degenerate WINDOW rect, which the design already absorbs.
            // Two guards over one condition class must not disagree.
            // *(Driver's solo Guard-Consistency pass, round 4.)*
            return CaptureOutcome.Transient(
                "The requested element was not inside the pixels that were captured.");
        var c = crop.Value;

        using var src = bitmap.Clone(c.Effective, bitmap.PixelFormat);

        // elementCanvasUniform: evaluated on `src` AFTER the crop, and ONLY when the full window bitmap
        // was NOT uniform -- a uniform crop inside a uniform window is just a solid window, already
        // covered by uniformCanvas.
        if (scope == CaptureScope.Element && !windowUniform && UniformCanvasDetector.IsUniform(src))
            warnings = Append(warnings, CaptureWarnings.For(CaptureWarnings.ElementCanvasUniform));

        // Step 8. Encode assembles; it does not detect.
        return CaptureOutcome.Completed(
            Encode(src, c.Absolute, c.Reported, geo.MaskRects, maxWidth, "printWindow", warnings));
    }

    /// <summary>Canonical steps 4-5 — the TERMINAL target-state guards — and the W2 they produce.
    ///
    /// PUBLIC and extracted because more than one path is about to photograph a named window, and these
    /// guards are about the TARGET's state rather than about the backend. The coordinator's
    /// circuit-breaker path skips CaptureWindow entirely and still owes them: without that, a hung window
    /// that trips the breaker and then minimizes is scraped at the rectangle it used to occupy, returning
    /// a photograph of whatever is now behind it — the exact failure this feature exists to remove.
    ///
    /// ⚠ ORDER IS LOAD-BEARING and these run before ANYTHING conditional. A window that minimizes
    /// mid-capture also changes size; if the resize check ran first it would route a minimized window
    /// into retry-and-fallback rather than refusing it.</summary>
    public static Rectangle GuardTargetState(IntPtr hwnd,
                                             System.Func<IntPtr, Rectangle?>? w2Probe = null,
                                             System.Func<IntPtr, bool>? minimizedProbe = null)
    {
        // Step 4. A FALSE return means the window is gone. GetWindowRect does not reliably zero the
        // struct on failure, so relying on the degenerate guard to catch a zeroed rect is relying on a
        // coincidence.
        var w2n = (w2Probe ?? DefaultW2Probe)(hwnd);
        if (w2n is null)
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "The target window was destroyed between the UIA walk and the capture.",
                "re-list windows and retry against a live handle");
        var w2 = w2n.Value;

        // ⚠ DEGENERATE EXTENTS ARE **NOT** CHECKED HERE, AND THAT IS DELIBERATE. This method raises only
        // the TERMINAL conditions -- destroyed and minimized -- because a degenerate rect is a RETRYABLE
        // transient (an animating or mid-open window reports one for a frame), and throwing it from a
        // shared guard would make it terminal for every caller. Each caller checks extents itself and
        // routes the case into the retry loop. See CaptureOutcomeKind.TargetTransient.
        //
        // ⚠ NOT covered by any extents check anyway. F6 MEASURED a minimized window's placeholder rect as
        // -32000,-32000 with extents 160x28 -- POSITIVE. It would pass, and yield a 160x28 image that is
        // not the window's content at all.
        if ((minimizedProbe ?? DefaultMinimizedProbe)(hwnd))
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "Window is minimized; restore it first.",
                "desktop_window_transform restore, then retry");

        return w2;
    }

    /// <summary>TRUE when the window's CURRENT size differs from <paramref name="asWalked"/>. Used by the
    /// OCR path, which has no W1/W2 pair of its own. A destroyed window reports changed: it is not safe
    /// to photograph either.</summary>
    public static bool WindowRectChanged(IntPtr hwnd, Rectangle asWalked)
    {
        var now = DefaultW2Probe(hwnd);
        return now is null || now.Value != asWalked;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static Rectangle? DefaultW2Probe(IntPtr hwnd)
        => GetWindowRect(hwnd, out var r)
            ? new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : null;

    private static bool DefaultMinimizedProbe(IntPtr hwnd) => IsIconic(hwnd);
}
