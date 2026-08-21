> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

## Phase 4 — The acquisition seam

### Task 14: `IWindowImageSource` — the injectable acquisition

This interface is what makes everything except the interop headless-testable. Without it the crop tests, the resize tests and the detector-placement tests cannot exist, and the spec's Testing section would be mandating tests against an architecture that forbids them.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/IWindowImageSource.cs`
- Create: `test/FlaUI.Mcp.Tests/Perception/FakeWindowImageSource.cs`

- [ ] **Step 1: Write the interface**

Create `src/FlaUI.Mcp.Core/Perception/IWindowImageSource.cs`:

```csharp
using System;
using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Acquire a bitmap of one window's own content. The ONLY part of this feature that touches
/// Win32, which is what keeps every other part headless-testable.</summary>
public interface IWindowImageSource
{
    /// <summary>Render the window into a new bitmap of exactly <paramref name="size"/>.
    ///
    /// Returns NULL when the call did not complete within <paramref name="timeoutMs"/>. The caller turns
    /// that into CaptureOutcome.TimedOut and falls back to the scrape.
    ///
    /// THROWS ToolException(CaptureUnavailable) when GDI hands back a null or zero handle. GDI does not
    /// throw -- CreateCompatibleDC, CreateCompatibleBitmap and SelectObject return null/zero on failure --
    /// so the scrape path's COMException/ExternalException catch never fires here and nothing else would
    /// take its place. Given ROADMAP item 13 (108 bare catches) the realistic outcome of skipping the
    /// null checks is an NRE surfacing as something unhelpful, or a garbage bitmap returned as a capture.
    ///
    /// The returned bitmap is the CALLER's to dispose.</summary>
    Bitmap? Acquire(IntPtr hwnd, Size size, int timeoutMs);
}
```

- [ ] **Step 2: Write the fake**

Create `test/FlaUI.Mcp.Tests/Perception/FakeWindowImageSource.cs`:

```csharp
using System;
using System.Drawing;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>A synthetic acquisition for headless tests. Every knob a test needs to stage one of the
/// seam's outcomes without a real window.</summary>
public sealed class FakeWindowImageSource : IWindowImageSource
{
    private readonly Func<Size, Bitmap?> _make;
    public int Calls { get; private set; }
    public Size LastRequestedSize { get; private set; }
    public IntPtr LastHwnd { get; private set; }

    public FakeWindowImageSource(Func<Size, Bitmap?> make) => _make = make;

    /// <summary>A bitmap of the requested size filled with one colour, with a distinguishing 4x4 marker
    /// at (1,1) so a test can prove the crop moved the origin rather than merely resizing.</summary>
    public static FakeWindowImageSource Solid(Color c) => new(size =>
    {
        var b = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using var g = Graphics.FromImage(b);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 0, 0, b.Width, b.Height);
        using var marker = new SolidBrush(Color.Magenta);
        g.FillRectangle(marker, 1, 1, 4, 4);
        return b;
    });

    /// <summary>Stages the timeout path.</summary>
    public static FakeWindowImageSource TimesOut() => new(_ => null);

    /// <summary>Stages the null-GDI-handle path.</summary>
    public static FakeWindowImageSource FailsWithCaptureUnavailable() => new(_ =>
        throw new FlaUI.Mcp.Core.Errors.ToolException(
            FlaUI.Mcp.Core.Errors.ToolErrorCode.CaptureUnavailable,
            "GDI resources are exhausted; the capture could not be allocated.",
            "close some windows and retry"));

    public Bitmap? Acquire(IntPtr hwnd, Size size, int timeoutMs)
    {
        Calls++;
        LastHwnd = hwnd;
        LastRequestedSize = size;
        return _make(size);
    }
}
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build FlaUI.Mcp.slnx`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/IWindowImageSource.cs test/FlaUI.Mcp.Tests/Perception/FakeWindowImageSource.cs
git commit -m "feat(capture): the injectable acquisition seam and its headless fake"
```

### Task 15: `CaptureWindow` — the seam that composes the guards, the acquire, the crop and the detectors

**"Owns" means COMPOSES.** The crop geometry is the pure function from Task 8; the extraction is one bitmap operation; acquisition is the injectable seam. `CaptureWindow` puts them together and adds the `W2`-side guards.

**Order is load-bearing.** Terminal target-state guards run BEFORE the resize check, because a window that minimizes mid-capture also changes size — and if the resize check ran first, a minimized window would be sent into the retry-and-fallback path, scraping the rect it used to occupy, which now shows whatever is behind it.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` (add `CaptureWindow` + the P/Invokes it needs)
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureWindowTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureWindowTests"`
Expected: FAIL — `CaptureWindow` does not exist (CS0117).

- [ ] **Step 3: Write `CaptureWindow`**

Append to `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` inside the class:

```csharp
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

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static Rectangle? DefaultW2Probe(IntPtr hwnd)
        => GetWindowRect(hwnd, out var r)
            ? new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : null;

    private static bool DefaultMinimizedProbe(IntPtr hwnd) => IsIconic(hwnd);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureWindowTests"`
Expected: PASS — 14 passed.

- [ ] **Step 5: Prove the gates are non-vacuous with FOUR logic mutants**

⚠ **This heading said "three" while listing four** — the same count-fossil family as Task 5's "11 passed", Task 7's "three-case", and Task 12's test name. All four below ARE logic mutants (expression and ordering changes), so each should build cleanly and turn a NAMED test red; if any produces a BUILD ERROR instead, the test never ran and the mutant proved nothing — say so and stop.

1. Move the `probeMin` check to AFTER the resize branch.
   Expected: `A_window_minimized_at_capture_time_refuses_and_does_not_report_a_resize` FAILS with a `Resized` outcome instead of the refusal — the exact ordering defect the canonical list was written to prevent.
2. Change `if (w1.Size != w2.Size) return CaptureOutcome.Resized;` to
   `if (w1.Size != w2.Size && geo.MaskRects.Count > 0) return CaptureOutcome.Resized;` — i.e. restore the
   empty-mask-set shortcut panel round 11 removed.
   Expected: `Window_scope_without_masks_still_reports_a_resize_rather_than_continuing` FAILS with
   `Completed`. **That failure is the leak**: an image returned for a window whose layout reflowed, with a
   mask set computed before the reflow.
3. Change `if (windowUniform)` back to `if (windowUniform && scope == CaptureScope.Window)` — i.e. restore the hole this design shipped with until 2026-08-21.
   Expected: `An_element_capture_of_a_blank_window_still_warns_uniformCanvas` FAILS with an EMPTY warning list. That empty list is the defect: a black crop returned to an agent with nothing saying so.
4. Drop `&& !windowUniform` from the `elementCanvasUniform` condition.
   Expected: `An_element_capture_of_a_blank_window_still_warns_uniformCanvas` FAILS on its second assertion — both codes now fire, and `elementCanvasUniform` says the element failed while the window rendered, which is false.

**Revert every mutant.**

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs test/FlaUI.Mcp.Tests/Perception/CaptureWindowTests.cs
git commit -m "feat(capture): the CaptureWindow seam - guards, resize detection, crop, detectors"
```

### Task 15b: The OCR path's RESIZE guard — MOVED HERE FROM TASK 13 STEP 6b-2

⛔ **Run this immediately after Task 15, before Task 16.** It was written as Task 13 Step 6b-2 and could
not compile there: it calls `ScreenCapture.WindowSizeChanged`, whose body needs `DefaultW2Probe`, and
both that and the `GetWindowRect` P/Invoke it rests on are created by **Task 15** (`:495`, `:499`).
MEASURED at Task 13 time: `grep -rn "DefaultW2Probe" src/` returned nothing.

*(AGY-FIRST consult 2026-08-21, option A, peer and driver ALIGNED. Giving Task 13 its own `GetWindowRect`
was rejected — Task 15 declares that exact P/Invoke in that exact class, so they would collide as
CS0111.)*

⚠⚠ **OPEN QUESTION BEFORE YOU IMPLEMENT THIS — the guard as written is a PRE-CHECK, and a pre-check
cannot close the race it is aimed at.** It runs `WindowSizeChanged` *immediately before* `Task.Run`, so
a reflow between that check and the capture is undetected: the pixels come from the new layout while the
masks describe the old one, and **this path OCRs the result and returns it as a string**. Checking
*after* the pixels are acquired is what actually closes it, for one extra `GetWindowRect`.
*(Raised by the AGY-FIRST consult's fourth answer.)*

**This is NOT what ROADMAP 19 already tracks, and the difference is the cost.** Item 19 defers an
ELEMENT-level bookend because it is "a second full geometry walk" and `desktop_wait_for_text` re-resolves
geometry every 750 ms, so a walk per poll would roughly double the polling cost. A window-level
*post-capture* `GetWindowRect` is not a walk and costs microseconds. **Item 19's stated cost objection
does not apply to it.**

⚠ **The operator has been asked whether to make this a bookend (check after, or before AND after) rather
than a pre-check. Do not implement it as a pre-check-only until that is answered.**

**Files:** `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (the `TextCaptureGeometry` record and both
`return` sites), `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs` (both capture sites),
`src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` (the `WindowSizeChanged` helper),
`test/FlaUI.Mcp.Tests/Perception/` as needed.

⚠ **Task 13's Files list and its Step 9 `git add` named NEITHER `FindTextTools.cs` NOR `ScreenCapture.cs`,
while this step modifies both** — so as originally written Task 13 would have committed a tree that does
not compile. Staging is listed above rather than inherited.

- [ ] **Step 1: The guard itself**

⚠⚠ The OCR path takes its mask rects from the walk and its pixels from `CaptureRectangle` some time later, **with nothing in between comparing the window's geometry.** A reflow in that gap misplaces every mask, and unlike the screenshot path the consequence is not a wrong-looking picture: **`FindAsync` OCRs the unmasked region and returns the redacted text as a string in the response.** `FindTextTools.cs:104-108` already states this hazard in its own words for a different case.

**PRE-EXISTING** — this is today's behaviour, unchanged by item 8. It is fixed here for the same reason the degenerate guard above was: item 8 built a resize guard for the screenshot path, and a guard that stops at the adjacent caller is this review's most-repeated defect. It is cheap: one `GetWindowRect`.

`TextCaptureGeometry` needs the handle to compare against. Extend it with an appended field, mirroring `CaptureGeometry`:

```csharp
// Appended, never inserted - same positional-record rule as CaptureResult and CaptureGeometry.
public sealed record TextCaptureGeometry(bool Denied, string? DeniedProcess, bool Minimized,
    System.Drawing.Rectangle CaptureBounds, IReadOnlyList<System.Drawing.Rectangle> MaskRects,
    int WindowLeft, int WindowTop, int WindowWidth, int WindowHeight,
    System.IntPtr NativeWindowHandle);
```

Populate it from the geometry the wrapper already holds (`geo.NativeWindowHandle`) at both `return` sites in `ResolveTextCaptureGeometryAsync`.

Then guard at **both** OCR capture sites, `FindTextTools.cs:62` and `:110`, immediately before the `Task.Run`:

```csharp
            // ⚠ A RESIZE BETWEEN THE WALK AND THE CAPTURE MISPLACES EVERY MASK, AND THIS PATH READS THE
            // RESULT ALOUD. On the screenshot path a misplaced mask returns a wrong-looking image; here
            // the OCR engine reads the unmasked pixels and returns the redacted text as a STRING. Cheap
            // to check - one GetWindowRect - and the two consumers both do the right thing with the
            // refusal: DesktopFindText propagates it, and DesktopWaitForText's catch at :108 degrades it
            // to "not found" and keeps polling, which is correct for a window that is still settling.
            if (ScreenCapture.WindowSizeChanged(geo.NativeWindowHandle,
                                                new System.Drawing.Size(geo.WindowWidth, geo.WindowHeight)))
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "The window changed size between reading its redacted regions and capturing it, so " +
                    "those regions can no longer be located.",
                    "wait for the window to settle, then retry");
```

Add the helper beside `GuardTargetState` in `ScreenCapture`, reusing the P/Invoke already declared there:

```csharp
    /// <summary>TRUE when the window's CURRENT size differs from <paramref name="asWalked"/>. Used by the
    /// OCR path, which has no W1/W2 pair of its own. A destroyed window reports changed: it is not safe
    /// to photograph either.</summary>
    public static bool WindowSizeChanged(IntPtr hwnd, Size asWalked)
    {
        var now = DefaultW2Probe(hwnd);
        return now is null || now.Value.Size != asWalked;
    }
```

### Task 16: `PrintWindowImageSource` — the real interop, and the only Desktop-category production file

**Build the timeout and the dedicated thread ONLY if Task 1 measured a block.** If it did not, implement `Acquire` synchronously and note in the file's doc comment that the timeout parameter is honoured but unreachable, citing the measurement.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Capture/PrintWindowImageSource.cs`
- Test: `test/FlaUI.Mcp.Tests/Capture/PrintWindowImageSourceTests.cs`

- [ ] **Step 1: Write the Desktop-category test**

```csharp
using System;
using System.Drawing;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

[Trait("Category", "Desktop")]
public class PrintWindowImageSourceTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PrintWindowImageSourceTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task It_renders_the_test_app_window_as_something_other_than_one_colour()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();
        using var bmp = src.Acquire(hwnd, new Size(rect.Width, rect.Height), timeoutMs: 5000);

        Assert.NotNull(bmp);
        Assert.Equal(rect.Width, bmp!.Width);
        Assert.Equal(rect.Height, bmp.Height);
        // F1: PW_RENDERFULLCONTENT is mandatory -- flag 0 returns blank for four of five window classes.
        // If this asserts, check the flag before anything else.
        Assert.False(FlaUI.Mcp.Core.Perception.UniformCanvasDetector.IsUniform(bmp),
            "the window rendered as a single colour; check PW_RENDERFULLCONTENT (flag 2) is being passed");
    }

    // A zero handle must refuse rather than calling into Win32 with it.
    [Fact]
    public void A_zero_handle_throws_CaptureUnavailable()
    {
        var src = new PrintWindowImageSource();
        var ex = Assert.Throws<FlaUI.Mcp.Core.Errors.ToolException>(() =>
            src.Acquire(IntPtr.Zero, new Size(100, 100), 1000));
        Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.CaptureUnavailable, ex.Code);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PrintWindowImageSourceTests"`
Expected: FAIL — `PrintWindowImageSource` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/FlaUI.Mcp.Server/Capture/PrintWindowImageSource.cs`:

```csharp
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
/// ⚠ PW_RENDERFULLCONTENT (flag 2) is MANDATORY. MEASURED: flag 0 returns blank for four of the five
/// window classes probed -- DirectX/Atlas, XAML/UWP, WPF and the shell all come back empty.
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
        ToolException? failure = null;
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
            try { produced = Render(hwnd, size); }
            catch (ToolException ex) { failure = ex; }
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
        if (failure is not null) throw failure;
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
```

- [ ] **Step 4: Run the Desktop test**

Run on a **physical console** (not RDP), on a quiet machine, and do not co-run anything else:

`dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PrintWindowImageSourceTests"`
Expected: PASS — 2 passed.

- [ ] **Step 5: Prove the gate is non-vacuous with a logic mutant**

Change `PW_RENDERFULLCONTENT` to `0`.
Expected: `It_renders_the_test_app_window_as_something_other_than_one_colour` FAILS with the "check PW_RENDERFULLCONTENT" message — which is evidence row F1 reproducing on this machine. **Revert.**

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Capture/PrintWindowImageSource.cs test/FlaUI.Mcp.Tests/Capture/PrintWindowImageSourceTests.cs
git commit -m "feat(capture): PrintWindow acquisition on a dedicated thread, all GDI handles checked"
```

---
