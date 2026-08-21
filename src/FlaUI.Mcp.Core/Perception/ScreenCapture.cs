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

/// <summary>Screen-region capture (no occlusion handling — callers focus-first; no UIA element reads).
/// Captures by absolute screen rectangle so it can run OFF the query STA (spec §8). Paints black
/// redaction rects (live bounds passed in), clamps width to a hard ceiling, PNG-encodes. Headless/
/// disconnected sessions are detected before capture so we never hand back a black frame.</summary>
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
                var rel = new Rectangle(
                    (int)System.Math.Round((clip.X - absolute.X) * scale), (int)System.Math.Round((clip.Y - absolute.Y) * scale),
                    (int)System.Math.Round(clip.Width * scale), (int)System.Math.Round(clip.Height * scale));
                if (rel.Width <= 0 || rel.Height <= 0) continue;
                g.FillRectangle(black, rel); painted++;
            }
            using var ms = new MemoryStream();
            outBmp.Save(ms, ImageFormat.Png);
            return new CaptureResult(ms.ToArray(), reported.X, reported.Y, reported.Width, reported.Height,
                                     scale, painted, method, warnings);
        }
    }
}
