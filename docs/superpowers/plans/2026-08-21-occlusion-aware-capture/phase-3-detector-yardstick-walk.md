> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

## Phase 3 — The detector, the yardstick, and the geometry walk

### Task 11: `UniformCanvasDetector` — settle the sampling by measurement

> ⚠ **THIS TASK RUNS BEFORE TASK 10, ahead of the rest of Phase 3.** Task 10 calls
> `UniformCanvasDetector.IsUniform(...)` and cannot compile until this task creates it. This task has no
> dependency on Task 10 in return, so it simply moves earlier; its content is unchanged. Execution order
> is therefore **… 9 → 11 → 10 → 12 → 13 …**. See the banner on Task 10 for the full reasoning.

This is **risk 5**. The predicate's PURPOSE is settled by the spec and must not grow into a correctness gate: it tells an agent this image may not be usable and that the UIA tree is the fallback.

**Its acceptance criteria are fixed, so this is a test rather than an observation.** The predicate and its sampling strategy pass if and only if:
1. it classifies the F1 flag-0 blank renders as **true**, and
2. it classifies the F4 row — the 0.044 non-black, dark-themed, **pixel-perfect** capture — as **false**, and
3. it does not measurably change capture latency.

**F4 is why a darkness heuristic is banned:** that 0.044 PNG was dumped and inspected and is a flawless render of an occluded window — nav rail, toggles, body text. 0.044 is a dark theme. A darkness heuristic would have rejected a perfect capture.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/UniformCanvasDetector.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/UniformCanvasDetectorTests.cs`
- Modify: `docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md`

- [ ] **Step 1: Write the failing tests, including the two acceptance criteria as tests**

```csharp
using System.Diagnostics;
using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class UniformCanvasDetectorTests
{
    private static Bitmap Solid(int w, int h, Color c)
    {
        var b = new Bitmap(w, h);
        using var g = Graphics.FromImage(b);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 0, 0, w, h);
        return b;
    }

    // Criterion 1: a failed render is one colour. Black is the common case; white and a mid-grey are
    // included because "uniform" is the property, not "dark" -- F4 proved a darkness heuristic unsound.
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(128, 128, 128)]
    public void A_uniform_bitmap_is_detected(int r, int g, int b)
    {
        using var bmp = Solid(400, 300, Color.FromArgb(r, g, b));
        Assert.True(UniformCanvasDetector.IsUniform(bmp));
    }

    // Criterion 2: THE F4 CASE. A dark-themed but real render -- mostly near-black with sparse light
    // content, ~4% non-black. This MUST come back false. It is the single case that rules out every
    // darkness-based predicate.
    [Fact]
    public void The_F4_dark_themed_real_render_is_not_uniform()
    {
        using var bmp = Solid(400, 300, Color.FromArgb(18, 18, 18));   // a dark theme's background
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.FromArgb(230, 230, 230)))
        {
            // ~4% of the area as light content, scattered the way real UI chrome is.
            for (int i = 0; i < 12; i++) g.FillRectangle(brush, 20 + i * 30, 20 + (i % 5) * 50, 24, 8);
        }
        Assert.False(UniformCanvasDetector.IsUniform(bmp));
    }

    // A single differing pixel is still not uniform. This pins that the predicate is about COLOUR COUNT
    // and not about a proportion threshold that could be tuned into swallowing real content.
    [Fact]
    public void One_differing_region_is_enough_to_be_non_uniform()
    {
        using var bmp = Solid(400, 300, Color.Black);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.White))
            g.FillRectangle(brush, 200, 150, 12, 12);
        Assert.False(UniformCanvasDetector.IsUniform(bmp));
    }

    // Criterion 3: it runs over a bitmap that is already allocated and already being encoded. A detector
    // that costs real time has chosen the wrong sampling strategy. 4K-wide is the worst realistic case.
    [Fact]
    [Trait("Category", "Measurement")]
    public void It_costs_no_measurable_time_on_a_4K_bitmap()
    {
        using var bmp = Solid(3840, 2160, Color.FromArgb(18, 18, 18));
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) UniformCanvasDetector.IsUniform(bmp);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"20 detections took {sw.ElapsedMilliseconds}ms; the sampling strategy is too expensive");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~UniformCanvasDetectorTests"`
Expected: FAIL — `UniformCanvasDetector` does not exist (CS0103).

- [ ] **Step 3: Write the implementation**

Create `src/FlaUI.Mcp.Core/Perception/UniformCanvasDetector.cs`:

```csharp
using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Is this bitmap effectively a single colour? A DIAGNOSTIC, never a gate (§3).
///
/// ⚠ NOT a darkness heuristic, and this is proven rather than argued. Evidence row F4 is an occluded
/// XAML window captured pixel-perfectly -- nav rail, toggles, body text, a warning banner -- whose
/// non-black fraction is 0.044 because it is a DARK THEME. A darkness predicate rejects a flawless
/// capture. The property is UNIFORMITY, not luminance.
///
/// ⚠ It must never grow into a correctness gate. §3's whole error budget is calibrated on the
/// consequence of a false positive being a spurious sentence; reusing this signal to SELECT A BACKEND
/// would re-price every error in it, and a false positive would then silently return a scrape -- for an
/// occluded window, a photograph of the occluder, the exact defect this feature exists to remove.</summary>
public static class UniformCanvasDetector
{
    // A sparse grid, not the full bitmap. Cost is O(GridN^2) regardless of resolution, which is what
    // satisfies criterion 3 on a 4K window. 64x64 = 4096 samples: dense enough that a real UI cannot hide
    // all of its contrast between the sample points, cheap enough to be free next to a PNG encode.
    private const int GridN = 64;

    /// <summary>TRUE when every sampled pixel has the same ARGB value.</summary>
    public static bool IsUniform(Bitmap bmp)
    {
        if (bmp.Width <= 0 || bmp.Height <= 0) return true;   // nothing to disagree

        int stepX = System.Math.Max(1, bmp.Width / GridN);
        int stepY = System.Math.Max(1, bmp.Height / GridN);

        int first = bmp.GetPixel(0, 0).ToArgb();
        for (int y = 0; y < bmp.Height; y += stepY)
            for (int x = 0; x < bmp.Width; x += stepX)
                if (bmp.GetPixel(x, y).ToArgb() != first) return false;

        // The far edges are sampled explicitly: a stride that does not divide the dimension would
        // otherwise never look at the last row/column, and a render that failed only at one edge is
        // exactly the shape a grid can miss.
        for (int y = 0; y < bmp.Height; y += stepY)
            if (bmp.GetPixel(bmp.Width - 1, y).ToArgb() != first) return false;
        for (int x = 0; x < bmp.Width; x += stepX)
            if (bmp.GetPixel(x, bmp.Height - 1).ToArgb() != first) return false;

        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~UniformCanvasDetectorTests"`
Expected: PASS — 6 passed. The `Measurement`-trait test runs here because the filter names the class directly; the headless gate excludes it.

- [ ] **Step 5: Record the sampling measurement**

Append a `## Risk 5 — the detector's sampling` section to the measurements doc: the chosen strategy (64×64 grid plus the two far edges), the measured time for 20 detections on a 3840×2160 bitmap, and an explicit statement that criteria 1 and 2 are enforced by `A_uniform_bitmap_is_detected` and `The_F4_dark_themed_real_render_is_not_uniform` rather than by inspection.

- [ ] **Step 6: Prove the gate is non-vacuous with a logic mutant**

Change `IsUniform` to `=> AverageLuminance(bmp) < 0.10;` (a darkness heuristic, written inline).
Expected: `The_F4_dark_themed_real_render_is_not_uniform` FAILS — which is precisely the defect F4 was measured to prevent, and it is worth seeing fail once. **Revert.**

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/UniformCanvasDetector.cs test/FlaUI.Mcp.Tests/Perception/UniformCanvasDetectorTests.cs docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md
git commit -m "feat(capture): uniform-canvas detector, sampling settled by measurement"
```

### Task 12: The yardstick switch, defaulting to the mask-preserving direction

**F5 falsifies the premise the existing early return rests on.** `PrintWindow` returns pixels that are on no monitor, so a window with no renderable overlap CAN contribute pixels, and dropping its mask set is a leak of exactly the class SP4 existed to close.

**The default MUST be unclipped.** This is inherited from the precedent beside it: `skipIfNoRenderableOverlap` already defaults to `false` because the window-scoped value is the safe one, so a forgotten call site fails safe.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:852-853` (signature)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:935` (the yardstick line)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:1181-1182` (full-desktop passes true)
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureGeometryCallSiteTests.cs`

- [ ] **Step 1: Write the failing call-site sweep**

No test of the yardstick's own logic can catch a CALLER omission, and §2 makes that default the thing standing between this feature and a mask-dropping leak. This is failure mode 1 of the three the spec names as having no test that would go red.

```csharp
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureGeometryCallSiteTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // The FULL-DESKTOP aggregator is the only caller that may clip. Every other call site takes the
    // unclipped default, and this pins that exactly one site names `clipToVirtualScreen: true`.
    //
    // ⚠ This sweep is the ONLY thing that catches a new caller silently inheriting the wrong yardstick.
    // A test of the yardstick logic itself cannot see a caller that never passed the parameter.
    [Fact]
    public void Exactly_one_production_call_site_clips_the_yardstick()
    {
        var root = RepoRoot();
        var lines = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .ToList();

        var clipping = lines.Where(x => x.Text.Contains("clipToVirtualScreen: true")).ToList();
        Assert.Single(clipping);
        Assert.EndsWith("PerceptionManager.cs", clipping[0].File);
    }

    // Every call site of the geometry walk, counted. If this number changes, a new caller appeared and
    // somebody must decide its yardstick deliberately rather than inherit one.
    [Fact]
    public void The_geometry_walk_has_exactly_four_production_call_sites()
    {
        var root = RepoRoot();
        var calls = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .Where(x => Regex.IsMatch(x.Text, @"ResolveWindowCaptureGeometryAsync\s*\("))
            .Where(x => !x.Text.Contains("public Task<CaptureGeometry>"))
            .ToList();

        // ScreenshotTools:53 (window/element), PerceptionManager:1136 (OCR),
        // PerceptionManager:1181 (full-desktop).
        //
        // ⚠ THREE, and it stays three after Task 17. The coordinator takes the walk as a DELEGATE rather
        // than calling this method directly, which is what keeps it headless-testable — so it adds no
        // call site here. If this number ever goes UP, a new caller appeared and somebody must choose its
        // yardstick deliberately instead of inheriting one.
        Assert.Equal(3, calls.Count);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureGeometryCallSiteTests"`
Expected: FAIL — no site names `clipToVirtualScreen: true`; the count test fails at 3.

*(Both assertions go green in Step 6 of this task. The count is **3** and stays 3 for the whole plan — the coordinator added in Task 17 takes the walk as a delegate and so adds no call site. An earlier draft of this plan predicted 4 and was wrong.)*

- [ ] **Step 3: Change the signature**

Replace `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` lines 848–853 with:

```csharp
    /// <param name="skipIfNoRenderableOverlap">TRUE only for the full-desktop mask sweep, where a window with no
    /// renderable overlap contributes no pixels and may be skipped. FALSE for a caller that NAMED this
    /// window and will photograph its rect regardless — suppressing that window's masks would hand back an
    /// unmasked image of the named target, which is the one thing this feature must not do.</param>
    /// <param name="clipToVirtualScreen">TRUE only for the full-desktop scrape. Selects the YARDSTICK the
    /// mask walk judges against.
    ///
    /// ⚠ DEFAULTS TO FALSE — the mask-preserving direction — and that is not a style choice. Evidence F5
    /// measured a 1920x1020 window captured in full while the physical display was 1366x768: PrintWindow
    /// returns pixels that are on NO monitor. So a window with no renderable overlap CAN contribute pixels,
    /// and clipping its yardstick drops the whole mask set for a window whose pixels are in the image.
    /// The default follows skipIfNoRenderableOverlap's precedent for the same reason given at :958 — the
    /// window-scoped value is the safe one, so a forgotten call site fails safe.
    ///
    /// ⚠ It follows the SCOPE, never the BACKEND. Window and element scope take the default INCLUDING when
    /// they fall back to the scrape: clipping exists to stop a maximized window's invisible resize-border
    /// bleed from defeating the full-desktop blacks-out check, which is a property of that CALLER.
    ///
    /// ⚠ The OCR path (FindTextTools, via ResolveTextCaptureGeometryAsync at :1136) also takes the default
    /// and so changes behaviour for a PARTIALLY off-screen window: today it always clips. That is a
    /// deliberate, tested consequence — see risk 6 — not an oversight.</param>
    public Task<CaptureGeometry> ResolveWindowCaptureGeometryAsync(WindowHandle handle, string? @ref,
                                                                   bool skipIfNoRenderableOverlap = false,
                                                                   bool clipToVirtualScreen = false) =>
```

- [ ] **Step 4: Make the yardstick conditional**

Replace `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` line 935 with:

```csharp
            var yardstick = clipToVirtualScreen
                ? System.Drawing.Rectangle.Intersect(captureBounds, ScreenCapture.VirtualScreenBounds())
                : captureBounds;
```

- [ ] **Step 5: Make the full-desktop aggregator clip explicitly**

Replace `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` lines 1181–1182 with:

```csharp
                var geo = await ResolveWindowCaptureGeometryAsync(new WindowHandle(w.Handle), null,
                                                                  skipIfNoRenderableOverlap: true,
                                                                  clipToVirtualScreen: true);
```

- [ ] **Step 6: Run the headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS except the known-red `The_geometry_walk_has_exactly_four_production_call_sites` (see Step 2's note). Build 0 warnings / 0 errors.

- [ ] **Step 7: Prove the sweep is non-vacuous with a logic mutant**

Temporarily change `PerceptionManager.cs:1181` to drop `clipToVirtualScreen: true`.
Expected: `Exactly_one_production_call_site_clips_the_yardstick` FAILS with zero matches. **Revert.**

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/CaptureGeometryCallSiteTests.cs
git commit -m "feat(capture): clipToVirtualScreen on the geometry walk, defaulting mask-preserving"
```

### Task 13: `CaptureGeometry` carries `W1`, the native `HWND`, and the retryable degenerate signal

Three things the seam needs that nothing currently carries there.

**The `HWND` is not optional:** `PrintWindow(hwnd, hdc, flags)` takes an OS window handle, and the `WindowHandle` the tool layer holds is this server's own `wN` identifier — verified, `src/FlaUI.Mcp.Core/Windows/WindowHandle.cs:4` is `public readonly record struct WindowHandle(string Id)`.

**`W1` is not optional either:** `CaptureGeometry.Bounds` is the ELEMENT rect for element scope (`PerceptionManager.cs:883` resolves `target` to the element, `:915` reads `target.BoundingRectangle`), so the window rect is genuinely absent.

**The degenerate-`W1` guard must be RETRYABLE and distinguishable from already-minimized.** A window caught mid-open can report a degenerate rect for one frame — exactly the transient the retry loop absorbs. If the walk simply throws, the throw escapes the loop and the capture fails terminally on the first bad frame. And the caller cannot recover by catching, because an already-minimized window raises the identical `ElementNotActionable` and must NOT be retried. So the walk signals the two differently even though both surface to the agent as `ElementNotActionable` once retries are exhausted.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:1275-1276` (the record)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:854-863, 915-935, 963-971, 1110` (every construction site)
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureGeometryShapeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureGeometryShapeTests
{
    // Positional record, same append-only rule as CaptureResult and for the same reason.
    [Fact]
    public void The_positional_order_is_append_only()
    {
        var ctor = typeof(CaptureGeometry).GetConstructors().Single();
        Assert.Equal(new[]
        {
            "Bounds", "MaskRects", "Minimized", "Denied", "DeniedProcess", "Escalations",
            "WindowBounds", "NativeWindowHandle", "DegenerateWindow",
        }, ctor.GetParameters().Select(p => p.Name).ToArray());
    }

    // THE DISTINCTION THAT MAKES THE RETRY LOOP WORK. Both surface to the agent as
    // ElementNotActionable, but one is a transient worth retrying and the other never will be.
    [Fact]
    public void Degenerate_and_minimized_are_separate_flags()
    {
        var degenerate = new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(),
            Minimized: false, Denied: false, null, System.Array.Empty<MaskEscalationEntry>(),
            default, System.IntPtr.Zero, DegenerateWindow: true);
        var minimized = new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(),
            Minimized: true, Denied: false, null, System.Array.Empty<MaskEscalationEntry>(),
            default, System.IntPtr.Zero, DegenerateWindow: false);

        Assert.True(degenerate.DegenerateWindow);
        Assert.False(degenerate.Minimized);
        Assert.True(minimized.Minimized);
        Assert.False(minimized.DegenerateWindow);
    }

    // A headless test can construct one with ANY handle value and a fake acquisition, and every crop,
    // yardstick and mask assertion still runs -- nothing between the walk and the seam dereferences it.
    // That is what keeps the HWND from breaking headless testability.
    [Fact]
    public void A_synthetic_handle_is_carried_through_untouched()
    {
        var geo = new CaptureGeometry(new System.Drawing.Rectangle(10, 20, 30, 40),
            System.Array.Empty<System.Drawing.Rectangle>(), false, false, null,
            System.Array.Empty<MaskEscalationEntry>(),
            new System.Drawing.Rectangle(0, 0, 100, 200), new System.IntPtr(0xDEAD), false);
        Assert.Equal(new System.IntPtr(0xDEAD), geo.NativeWindowHandle);
        Assert.Equal(new System.Drawing.Rectangle(0, 0, 100, 200), geo.WindowBounds);
        Assert.Equal(new System.Drawing.Rectangle(10, 20, 30, 40), geo.Bounds);   // the ELEMENT rect
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureGeometryShapeTests"`
Expected: FAIL — the constructor has 6 parameters, not 9.

- [ ] **Step 3: Extend the record**

Replace `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` lines 1275–1276 with:

```csharp
/// <summary>What the geometry walk produces for one window.
///
/// ⚠ POSITIONAL RECORD. APPEND ONLY, NEVER INSERT — the same rule and the same reason as CaptureResult.
///
/// <paramref name="Bounds"/> is the CAPTURE rect: the ELEMENT's rect when a @ref was given, the window's
/// otherwise. <paramref name="WindowBounds"/> is ALWAYS the window's own rect (W1) — for window scope the
/// two are equal, and for element scope they are not, which is why both must cross.
///
/// <paramref name="NativeWindowHandle"/> is the OS HWND. It is NOT the WindowHandle the tool layer holds:
/// that is this server's own wN identifier (WindowHandle.cs:4 is `record struct WindowHandle(string Id)`),
/// and PrintWindow needs the real handle. Carrying it here does NOT break headless testability — nothing
/// between the walk and the acquisition seam dereferences it, so a headless test constructs a geometry
/// with any handle value and a fake acquisition and every crop and mask assertion still runs.
///
/// <paramref name="DegenerateWindow"/> — W1 had zero or negative extents when the walk read it. A SEPARATE
/// flag from Minimized on purpose: a window caught mid-open or mid-animation reports a degenerate rect for
/// a frame and is exactly the transient the retry loop absorbs, while an already-minimized window never
/// stops being minimized and retrying it just burns the budget. Both surface to the AGENT as
/// ElementNotActionable once retries exhaust; the internal signal is what differs, and this is the one
/// place in the design where the agent-facing code and the internal signal deliberately part company.</summary>
public sealed record CaptureGeometry(System.Drawing.Rectangle Bounds, IReadOnlyList<System.Drawing.Rectangle> MaskRects, bool Minimized, bool Denied, string? DeniedProcess,
    IReadOnlyList<MaskEscalationEntry> Escalations,
    System.Drawing.Rectangle WindowBounds, System.IntPtr NativeWindowHandle, bool DegenerateWindow);
```

- [ ] **Step 4: Add the degenerate guard between the bounds read and the yardstick**

The ordering is the whole defence. Insert immediately after the `captureBounds` read block ends at `PerceptionManager.cs:925` and **before** the yardstick comment at `:927`:

```csharp
            // ⚠ W1 DEGENERACY, GUARDED BEFORE THE YARDSTICK. LOAD-BEARING, and an earlier design called
            // it redundant on a claim that stopped being true when element scope gained a scrape fallback.
            // Without it: a degenerate W1 makes the yardstick degenerate, `:947`'s window-scoped branch
            // falls back to `yardstick = captureBounds` which is still degenerate, every mask is judged
            // against a rect nothing intersects, THE WHOLE SET IS DROPPED -- and the repo's own comment at
            // :942-946 names the outcome: "every mask dropped, capture returned unmasked. A guard producing
            // a leak." Element scope then sees W1.Size != W2.Size, retries, exhausts, falls back to the
            // scrape, and returns an UNMASKED image as a success.
            //
            // ⚠ RETURNED, NOT THROWN. A throw escapes the caller's retry loop, so a window caught mid-open
            // would fail terminally on its first bad frame. The caller cannot recover by catching either,
            // because an already-minimized window raises the identical ElementNotActionable and must NOT
            // be retried.
            //
            // ⚠ The guard sits HERE and not in the caller because there is no point between for a caller
            // to stand: the walk produces the mask rects, producing them REQUIRES the yardstick, so by the
            // time a caller holds W1 the yardstick has already run and the mask set may already be gone.
            var windowBounds = string.IsNullOrEmpty(@ref) ? captureBounds : SafeWindowRect(win);
            if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
                return new CaptureGeometry(captureBounds, System.Array.Empty<System.Drawing.Rectangle>(),
                    false, false, null, System.Array.Empty<MaskEscalationEntry>(),
                    windowBounds, NativeHandleOf(win), DegenerateWindow: true);
```

- [ ] **Step 5: Add the two helpers**

Add as private static members of `PerceptionManager`:

```csharp
    /// <summary>W1 — the WINDOW's own rect, needed even when `target` is an element. Guarded because it
    /// is a UIA read on a possibly-dying window and this region must never raise a raw exception.
    ///
    /// ⚠⚠ ON FAILURE IT RETURNS `default` — AN EMPTY RECT — NOT THE ELEMENT'S RECT, and an earlier
    /// version of this plan returned the element's. That looked harmless and POISONED A DOWNSTREAM GUARD:
    /// the resize check compares `geo.WindowBounds.Size` against a `GetWindowRect` read of the WINDOW, so
    /// substituting the ELEMENT's size guarantees a mismatch on every element-scope capture whose window
    /// rect read happened to fail. Every one of them would burn the whole retry budget and then refuse or
    /// fall back — a total outage for that window, caused by the fallback that was meant to soften a
    /// failure. *(AGY-AFTER panel over this plan, round 6, Guard-Consistency Auditor.)*
    ///
    /// An empty rect flows into the degeneracy check immediately below and becomes
    /// `DegenerateWindow: true` — a RETRYABLE transient, which is the honest answer: we could not read
    /// the window's geometry this frame. Signalling failure beats substituting a plausible wrong value.</summary>
    private static System.Drawing.Rectangle SafeWindowRect(AutomationElement win)
    {
        try { return win.BoundingRectangle; }
        catch (System.Exception ex) when (ex is not System.OutOfMemoryException
                                          and not System.OperationCanceledException)
        { _ = ex; return default; }
    }

    /// <summary>The native HWND. IntPtr.Zero when unavailable; the acquisition seam treats zero as an
    /// unusable target and refuses rather than calling PrintWindow with it.</summary>
    private static System.IntPtr NativeHandleOf(AutomationElement win)
    {
        try { return win.Properties.NativeWindowHandle.ValueOrDefault; }
        catch (System.Exception ex) when (ex is not System.OutOfMemoryException
                                          and not System.OperationCanceledException)
        { _ = ex; return System.IntPtr.Zero; }
    }
```

- [ ] **Step 6: Update every remaining construction site**

There are four, and all must be found — an omission here is a compile error, which is the good case, but the VALUES matter. Run `grep -n "new CaptureGeometry(" src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` and update each:

- `:858` (denied) — append `default, System.IntPtr.Zero, false`
- `:863` (minimized) — append `default, System.IntPtr.Zero, false`
- `:964` (no renderable overlap, full-desktop) — append `windowBounds, NativeHandleOf(win), false`
- `:1110` (the success return) — append `windowBounds, NativeHandleOf(win), false`

- [ ] **Step 6b: Close the SAME hole on the OCR path — it is a leak, and the guard above does not reach it**

⚠⚠ `ResolveTextCaptureGeometryAsync` (`PerceptionManager.cs:1134-1144`) is the **third caller** of the geometry walk, and it tests only `geo.Denied || geo.Minimized`. It does **not** test `DegenerateWindow` — so the guard you just added returns an EMPTY mask set and the OCR path uses it happily.

**Why that is a leak and not merely untidy:** the degenerate-window race is the same one the screenshot path guards. The window is degenerate at walk time (mask set empty), valid again by capture time, and `Capture.Rectangle` then photographs a perfectly good window **with no masks at all**. On this path those pixels are fed to OCR, so the redacted text comes back as *plaintext in the response*. `FindTextTools.cs:104-108` already states this hazard in its own words: *"this path captures the same pixels the screenshot path refused to show and OCRs them, so swallowing the refusal would read back in plaintext exactly what the mask was there to withhold."*

Insert after the `Denied || Minimized` early return at `:1137-1139`:

```csharp
        // ⚠ A DEGENERATE WINDOW MUST REFUSE HERE TOO. The walk returns an EMPTY mask set for one, and this
        // path OCRs what it captures -- so proceeding would read redacted text back as plaintext once the
        // window returns to a valid size before the capture. THROWN rather than returned as a flag,
        // because TextCaptureGeometry has no field for it and the two consumers both do the right thing:
        // DesktopFindText propagates it, and DesktopWaitForText's catch at FindTextTools.cs:108 degrades
        // it to "not found" and keeps polling, which is correct for a window that is still opening.
        if (geo.DegenerateWindow)
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "The window reported no renderable area, so its redacted regions cannot be located.",
                "wait for the window to finish opening, then retry");
```

- [ ] **Step 6b-2: The OCR path also needs a RESIZE guard, and this one leaks PLAINTEXT**

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

- [ ] **Step 6c: Write the headless test for it**

Add to `test/FlaUI.Mcp.Tests/Perception/CaptureGeometryShapeTests.cs`:

```csharp
    // ⚠ The OCR path is the THIRD caller of the geometry walk and had no degenerate guard, so a window
    // that was degenerate at walk time and valid at capture time was OCR'd with an EMPTY mask set --
    // returning redacted text as plaintext. This pins that the flag exists for that caller to act on.
    [Fact]
    public void A_degenerate_geometry_is_distinguishable_by_a_caller_that_must_refuse()
    {
        var degenerate = new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(),
            Minimized: false, Denied: false, null, System.Array.Empty<MaskEscalationEntry>(),
            default, System.IntPtr.Zero, DegenerateWindow: true);

        // The exact test the OCR wrapper performs. Denied/Minimized alone would let this through.
        Assert.False(degenerate.Denied || degenerate.Minimized);
        Assert.True(degenerate.DegenerateWindow);
        Assert.Empty(degenerate.MaskRects);   // ...and this is what would have been OCR'd in the clear
    }
```

- [ ] **Step 7: Run the headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS except the known-red call-site count from Task 12. Build 0 warnings / 0 errors.

- [ ] **Step 8: Prove the gate is non-vacuous with a logic mutant**

Temporarily change the guard to set `DegenerateWindow: false`.
Expected: `Degenerate_and_minimized_are_separate_flags` still passes (it constructs records directly), so this mutant proves the *record* is fine and the *guard* is not covered here — the guard's behavioural test arrives in Task 18, where the coordinator can observe the retry. **Note this explicitly in the commit message rather than pretending the coverage exists now.** **Revert.**

- [ ] **Step 9: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/CaptureGeometryShapeTests.cs
git commit -m "feat(capture): CaptureGeometry carries W1, the HWND and a retryable degenerate signal

The guard's BEHAVIOUR (that a degenerate W1 causes a retry rather than a
terminal failure) is not yet covered - the coordinator that observes it
does not exist until Task 18. Only the record shape is pinned here."
```

---
