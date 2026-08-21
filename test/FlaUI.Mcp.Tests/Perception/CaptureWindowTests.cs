using System;
using System.Drawing;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureWindowTests
{
    private static CaptureGeometry Geo(Rectangle capture, Rectangle window,
                                       params Rectangle[] masks)
        => new(capture, masks, false, false, null, Array.Empty<MaskEscalationEntry>(),
               window, new IntPtr(0x1234), false);

    // W2 is injected rather than read from the OS, so the whole seam is headless.
    private static CaptureOutcome Run(CaptureGeometry geo, Rectangle w2, CaptureScope scope,
                                      IWindowImageSource src,
                                      IReadOnlyList<CaptureWarning>? warnings = null)
        => ScreenCapture.CaptureWindow(geo, maxWidth: 0, scope, warnings ?? Array.Empty<CaptureWarning>(),
                                       src, timeoutMs: 1000, w2Probe: _ => w2, minimizedProbe: _ => false);

    [Fact]
    public void A_static_window_completes_and_reports_printWindow()
    {
        var w = new Rectangle(100, 100, 400, 300);
        var o = Run(Geo(w, w), w, CaptureScope.Window, FakeWindowImageSource.Solid(Color.White));
        Assert.Equal(CaptureOutcomeKind.Completed, o.Kind);
        Assert.Equal("printWindow", o.Result!.CaptureMethod);
        Assert.Equal(100, o.Result.X);
        Assert.Equal(400, o.Result.W);
    }

    // ORDERING: minimized is checked BEFORE the resize branch. A window that minimized mid-capture also
    // changed size, and if resize won it would be sent to retry-and-fallback -- scraping the rect it used
    // to occupy, which now shows whatever is behind it.
    [Fact]
    public void A_window_minimized_at_capture_time_refuses_and_does_not_report_a_resize()
    {
        var w1 = new Rectangle(100, 100, 400, 300);
        var w2 = new Rectangle(-32000, -32000, 160, 28);   // the real placeholder rect, from F6
        var ex = Assert.Throws<ToolException>(() =>
            ScreenCapture.CaptureWindow(Geo(w1, w1), 0, CaptureScope.Window, Array.Empty<CaptureWarning>(),
                                        FakeWindowImageSource.Solid(Color.White), 1000,
                                        w2Probe: _ => w2, minimizedProbe: _ => true));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
    }

    // F6 measured a minimized window's placeholder as 160x28 -- POSITIVE extents. An extents check alone
    // would pass it and yield a 160x28 image that is not the window's content at all.
    // ⚠ REPORTED, NOT THROWN. A degenerate W2 is the SAME physical condition as a degenerate W1, which
    // the design treats as a retryable transient because an animating or mid-open window reports one for
    // a frame. Throwing here would make that condition terminal or recoverable purely according to which
    // of two reads milliseconds apart happened to catch it -- and would defeat the retry loop for exactly
    // the animating window it exists for.
    [Fact]
    public void A_degenerate_W2_reports_a_retryable_transient_rather_than_refusing()
    {
        var w1 = new Rectangle(100, 100, 400, 300);
        var o = Run(Geo(w1, w1), new Rectangle(100, 100, 0, 300), CaptureScope.Window,
                    FakeWindowImageSource.Solid(Color.White));
        Assert.Equal(CaptureOutcomeKind.TargetTransient, o.Kind);
        Assert.Null(o.Result);
    }

    [Fact]
    public void A_failed_GetWindowRect_refuses_rather_than_trusting_a_zeroed_struct()
    {
        var w1 = new Rectangle(100, 100, 400, 300);
        var ex = Assert.Throws<ToolException>(() =>
            ScreenCapture.CaptureWindow(Geo(w1, w1), 0, CaptureScope.Window, Array.Empty<CaptureWarning>(),
                                        FakeWindowImageSource.Solid(Color.White), 1000,
                                        w2Probe: _ => null, minimizedProbe: _ => false));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
    }

    // WINDOW SCOPE, MASKS PRESENT, RESIZED -> Resized signal. The seam REPORTS; the caller retries and
    // decides the terminal outcome. Since the 2026-08-21 ratification this is no longer an immediate
    // refusal, and the seam is the component that must not make that decision.
    [Fact]
    public void Window_scope_with_masks_reports_a_resize_rather_than_refusing()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 900, 600);
        var o = Run(Geo(w1, w1, new Rectangle(10, 10, 50, 20)), w2, CaptureScope.Window,
                    FakeWindowImageSource.Solid(Color.White));
        Assert.Equal(CaptureOutcomeKind.Resized, o.Kind);
        Assert.Null(o.Result);
    }

    // ⚠ WINDOW SCOPE, NO MASKS, RESIZED -> STILL REPORTS. An empty mask set means "nothing sensitive
    // was found in the T1 layout", NOT "nothing in this window is sensitive" -- a reflow can move content
    // into the cropped region or CREATE it, and no mask was ever computed for that content. This
    // continued-and-warned until panel round 11.
    [Fact]
    public void Window_scope_without_masks_still_reports_a_resize_rather_than_continuing()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 1000, 700);
        var o = Run(Geo(w1, w1), w2, CaptureScope.Window, FakeWindowImageSource.Solid(Color.White));
        Assert.Equal(CaptureOutcomeKind.Resized, o.Kind);
        Assert.Null(o.Result);
    }

    [Fact]
    public void Element_scope_reports_a_resize_for_the_callers_retry_loop()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(100, 100, 200, 150);
        var o = Run(Geo(e, w1), new Rectangle(0, 0, 700, 600), CaptureScope.Element,
                    FakeWindowImageSource.Solid(Color.White));
        Assert.Equal(CaptureOutcomeKind.Resized, o.Kind);
    }

    [Fact]
    public void A_timeout_becomes_the_TimedOut_outcome_not_an_exception()
    {
        var w = new Rectangle(0, 0, 400, 300);
        var o = Run(Geo(w, w), w, CaptureScope.Window, FakeWindowImageSource.TimesOut());
        Assert.Equal(CaptureOutcomeKind.TimedOut, o.Kind);
    }

    [Fact]
    public void A_null_GDI_handle_becomes_CaptureUnavailable()
    {
        var w = new Rectangle(0, 0, 400, 300);
        var ex = Assert.Throws<ToolException>(() =>
            Run(Geo(w, w), w, CaptureScope.Window, FakeWindowImageSource.FailsWithCaptureUnavailable()));
        Assert.Equal(ToolErrorCode.CaptureUnavailable, ex.Code);
    }

    // Sizes AGREE and the element still falls outside the window's own bitmap -- a provider reporting an
    // element outside its own window. Pathological rather than impossible; this repo's mask-escalation
    // machinery exists because UIA does report inconsistent rectangles.
    // ⚠ RETRYABLE, not an immediate refusal. Reachable only when the sizes AGREE, so it means UIA
    // reported an element outside its own window -- the same class of transient as a momentarily
    // degenerate window rect, which the design already absorbs. The reason travels with the signal so the
    // terminal message is not the false "no renderable area".
    [Fact]
    public void An_empty_element_crop_at_matching_sizes_is_a_retryable_transient()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(900, 900, 100, 50);
        var o = Run(Geo(e, w1), w1, CaptureScope.Element, FakeWindowImageSource.Solid(Color.White));
        Assert.Equal(CaptureOutcomeKind.TargetTransient, o.Kind);
        Assert.Contains("not inside the pixels", o.TransientReason);
    }

    // THE DETECTORS RUN WHERE THEIR OPERANDS EXIST. uniformCanvas sees the FULL window bitmap, before the
    // crop -- §3 requires it, and after the crop that bitmap no longer exists.
    [Fact]
    public void A_uniform_window_bitmap_warns_uniformCanvas()
    {
        var w = new Rectangle(0, 0, 400, 300);
        // Solid() paints a magenta marker, so use a genuinely flat source for this one.
        var flat = new FakeWindowImageSource(size =>
        {
            var b = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(b);
            using var br = new SolidBrush(Color.Black);
            g.FillRectangle(br, 0, 0, b.Width, b.Height);
            return b;
        });
        var o = Run(Geo(w, w), w, CaptureScope.Window, flat);
        Assert.Contains(o.Result!.CaptureWarnings, x => x.Code == "uniformCanvas");
    }

    // ⚠ THE HOLE THIS CLOSES. An ELEMENT-scope capture of a window that failed to render entirely used to
    // emit NOTHING: uniformCanvas was scoped window-only, and elementCanvasUniform fires only when the
    // crop is uniform AND the window bitmap was NOT. The agent got a black crop, silently -- in breach of
    // the design's own success criterion. uniformCanvas now fires on BOTH scopes.
    [Fact]
    public void An_element_capture_of_a_blank_window_still_warns_uniformCanvas()
    {
        var w1 = new Rectangle(0, 0, 400, 300);
        var e  = new Rectangle(100, 100, 80, 60);
        var flat = new FakeWindowImageSource(size =>
        {
            var b = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(b);
            using var br = new SolidBrush(Color.Black);
            g.FillRectangle(br, 0, 0, b.Width, b.Height);
            return b;
        });
        var o = Run(Geo(e, w1), w1, CaptureScope.Element, flat);
        Assert.Contains(o.Result!.CaptureWarnings, x => x.Code == "uniformCanvas");
        // NOT elementCanvasUniform: the crop is not uniform *while the window was not* -- the window was.
        Assert.DoesNotContain(o.Result.CaptureWarnings, x => x.Code == "elementCanvasUniform");
    }

    // elementCanvasUniform fires ONLY when the crop is uniform and the full window bitmap was NOT. A
    // uniformly-coloured crop inside a uniformly-coloured window is just a solid window, already covered
    // by uniformCanvas -- which, since the fix above, is now TRUE on element scope as well.
    [Fact]
    public void A_uniform_crop_inside_a_varied_window_warns_elementCanvasUniform()
    {
        var w1 = new Rectangle(0, 0, 400, 300);
        var e  = new Rectangle(200, 200, 100, 80);   // lands in the flat right-hand region
        var varied = new FakeWindowImageSource(size =>
        {
            var b = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(b);
            using var black = new SolidBrush(Color.Black);
            g.FillRectangle(black, 0, 0, b.Width, b.Height);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 0, 0, 60, 60);     // contrast, but far from the element
            return b;
        });
        var o = Run(Geo(e, w1), w1, CaptureScope.Element, varied);
        Assert.Contains(o.Result!.CaptureWarnings, x => x.Code == "elementCanvasUniform");
        Assert.DoesNotContain(o.Result.CaptureWarnings, x => x.Code == "uniformCanvas");
    }

    // Warnings travel INWARD. The caller's geometry-time findings must reach Encode through the seam --
    // the alternative, unpacking the returned CaptureResult to insert one, is exactly the reconstruction
    // the append-only rule exists to prevent.
    [Fact]
    public void The_callers_accumulated_warnings_reach_the_result()
    {
        var w = new Rectangle(0, 0, 400, 300);
        var carried = new[] { CaptureWarnings.For(CaptureWarnings.PopupsNotRendered) };
        var o = Run(Geo(w, w), w, CaptureScope.Window, FakeWindowImageSource.Solid(Color.White), carried);
        Assert.Contains(o.Result!.CaptureWarnings, x => x.Code == "popupsNotRendered");
    }
}
