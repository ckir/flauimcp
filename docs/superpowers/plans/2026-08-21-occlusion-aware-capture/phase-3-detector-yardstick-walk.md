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

    // ⚠ THE FALLBACK PATH. IsUniform takes a fast LockBits route only for 32bpp bitmaps and falls back
    // to GetPixel otherwise. Every bitmap the predicate receives in production is 32bpp, which means the
    // fallback would be entirely UNEXERCISED without these two -- dead code that still ships and still
    // has to be right. A 24bpp bitmap is the cheapest way to reach it through the public API.
    [Fact]
    public void A_non_32bpp_uniform_bitmap_is_detected_through_the_fallback()
    {
        using var bmp = new Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.FromArgb(18, 18, 18)))
            g.FillRectangle(brush, 0, 0, 400, 300);
        Assert.True(UniformCanvasDetector.IsUniform(bmp));
    }

    [Fact]
    public void A_non_32bpp_bitmap_with_content_is_not_uniform_through_the_fallback()
    {
        using var bmp = new Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using (var bg = new SolidBrush(Color.FromArgb(18, 18, 18))) g.FillRectangle(bg, 0, 0, 400, 300);
            using (var fg = new SolidBrush(Color.White)) g.FillRectangle(fg, 200, 150, 12, 12);
        }
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

⚠ **THE READ PATH CHANGED AFTER MEASUREMENT — OPERATOR DECISION, 2026-08-21.** An earlier version read
every sample with `Bitmap.GetPixel`, and it **failed criterion 3 on the reference machine**: 115–126 ms
across three runs against the <100 ms budget, consistently over. The sampling strategy was not the
problem. Benchmarked head-to-head, same sample points, after first confirming both paths return the
**same answer** on a uniform bitmap and on one with content:

| read path | 20 detections, 3840×2160 |
|---|---|
| `GetPixel` | **110 / 114 / 177 ms** |
| `LockBits` + `Marshal.ReadInt32` | **29 / 30 / 30 ms** |

`GridN` stays 64 and the 100 ms budget stays; the instrument changed, giving **3.3× headroom on the
oldest hardware in play**. *(AGY-FIRST consult: the peer recommended deleting the latency test instead,
on the grounds that an absolute wall-clock threshold is a weak instrument and the detector is a tiny
fraction of a 4K capture. Its first objection — that `LockBits` would force a 33 MB format conversion —
it retracted itself after tracing the sources. **Rejected** because nothing else enforces the O(1)
sampling property: swapping the sparse grid for a full-image scan is ~2000× more work and would ship
silently, and the peer's supporting figure for a 4K `PrintWindow` (">800 ms") is measured nowhere. Its
point about absolute thresholds is fair and is recorded below.)*

⚠ **WHAT THIS TEST DOES AND DOES NOT CATCH.** With 3.3× headroom it catches a *catastrophic* regression —
a full-image scan, a per-pixel allocation — and it will **not** catch a 2× drift. That is the right
sensitivity for a diagnostic, but do not read a green here as "the detector is still optimal".

```csharp
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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

        // ⚠ FORMAT GUARD, and it is not decoration. LockBits asked for a pixel format the bitmap does
        // NOT already have converts the WHOLE image into a temporary buffer -- at 4K that is a ~33 MB
        // allocation on the capture path, trading a few ms of CPU for a large transient. So we lock in
        // the bitmap's OWN format and only when that format is 32bpp, which makes the lock zero-copy and
        // makes a 4-byte read per sample correct. MEASURED: every bitmap this predicate actually
        // receives is 32bpp -- `new Bitmap(w, h)` defaults to Format32bppArgb, which covers the scrape
        // path, the PrintWindow path and the tests -- so the fast path is the one that runs. The
        // fallback exists so an unexpected format degrades in SPEED rather than in MEMORY.
        if (Image.GetPixelFormatSize(bmp.PixelFormat) == 32)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                    ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                // ⚠ A NEGATIVE stride means a BOTTOM-UP DIB: Scan0 points at the LAST scanline and the
                // offset arithmetic below would walk backwards out of the buffer. Rare, and not worth
                // special-casing, so we fall through to GetPixel -- but we must UNLOCK first, because
                // GetPixel on a locked bitmap throws InvalidOperationException.
                if (data.Stride > 0) return ScanLocked(data, bmp.Width, bmp.Height, stepX, stepY);
            }
            finally { bmp.UnlockBits(data); }
        }

        return ScanWithGetPixel(bmp, stepX, stepY);
    }

    /// <summary>The fast path: raw 4-byte reads at the same sample points, no per-pixel marshalling.</summary>
    private static bool ScanLocked(BitmapData data, int width, int height, int stepX, int stepY)
    {
        IntPtr scan0 = data.Scan0;
        int stride = data.Stride;
        int first = Marshal.ReadInt32(scan0, 0);

        for (int y = 0; y < height; y += stepY)
            for (int x = 0; x < width; x += stepX)
                if (Marshal.ReadInt32(scan0, y * stride + x * 4) != first) return false;

        // The far edges are sampled explicitly: a stride that does not divide the dimension would
        // otherwise never look at the last row/column, and a render that failed only at one edge is
        // exactly the shape a grid can miss.
        for (int y = 0; y < height; y += stepY)
            if (Marshal.ReadInt32(scan0, y * stride + (width - 1) * 4) != first) return false;
        for (int x = 0; x < width; x += stepX)
            if (Marshal.ReadInt32(scan0, (height - 1) * stride + x * 4) != first) return false;

        return true;
    }

    /// <summary>The fallback, for a non-32bpp or bottom-up bitmap. Identical sample points and identical
    /// answer -- only slower. Verified against the fast path before the fast path was adopted.</summary>
    private static bool ScanWithGetPixel(Bitmap bmp, int stepX, int stepY)
    {
        int first = bmp.GetPixel(0, 0).ToArgb();
        for (int y = 0; y < bmp.Height; y += stepY)
            for (int x = 0; x < bmp.Width; x += stepX)
                if (bmp.GetPixel(x, y).ToArgb() != first) return false;

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
Expected: PASS — **8 passed** (3 theory rows + 5 facts).

⚠ **THE `Measurement` TRAIT DOES NOT EXCUSE THIS TEST FROM THE HEADLESS GATE — an earlier version of this
line claimed it did, and that was wrong.** MEASURED: the headless baseline went 970 → **978** after this
task, i.e. **all eight** ran under
`--filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`. That filter excludes
`Desktop`, `SyntheticInput` and `KnownDefect` — a test tagged `Measurement` satisfies all three
inequalities and is therefore **included**. Only the *Desktop* gate carries `Category!=Measurement`
(`phase-6-wiring-and-gates.md`).

**Consequence, and it is the whole reason this correction matters:** had the timing test been left red,
it would have broken the **headless gate**, not merely a class-filtered run. "It's Measurement-traited,
so the gates stay green" was never true, and must not be used to justify leaving one red.

⚠ **This said 6 until the read path changed.** The two fallback tests were added with the format guard, because a guard whose fallback branch is never exercised is untested code that still ships.

⚠ **The timing test must now come in around 30 ms, not 115–126 ms.** If it is still over 100 ms, the LockBits path is NOT being taken — check the format guard before touching the threshold.

- [ ] **Step 5: Record the sampling measurement**

Append a `## Risk 5 — the detector's sampling` section to the measurements doc: the chosen strategy (64×64 grid plus the two far edges), the measured time for 20 detections on a 3840×2160 bitmap, and an explicit statement that criteria 1 and 2 are enforced by `A_uniform_bitmap_is_detected` and `The_F4_dark_themed_real_render_is_not_uniform` rather than by inspection.

- [ ] **Step 6: Prove the gate is non-vacuous with a logic mutant**

**Mutant 1 — the banned heuristic.** Replace the whole body of `IsUniform` with a darkness test:
`{ if (bmp.Width <= 0 || bmp.Height <= 0) return true; return AverageLuminance(bmp) < 0.10; }`, writing a small
`AverageLuminance` helper inline that samples the same grid.
Expected: `The_F4_dark_themed_real_render_is_not_uniform` FAILS — precisely the defect F4 was measured to
prevent, and worth seeing fail once. Some `A_uniform_bitmap_is_detected` rows also flip, since white and
mid-grey are not dark; report the full red set. **Revert.**

**Mutant 2 — the format guard.** Change `Image.GetPixelFormatSize(bmp.PixelFormat) == 32` to `!= 0`, so a
24bpp bitmap wrongly takes the 4-byte-read fast path.
Expected: at least one of the two `..._through_the_fallback` tests FAILS — a 3-byte-per-pixel buffer read
4 bytes at a time yields garbage comparisons. **This is the mutant that proves the guard is load-bearing
rather than decorative.** If BOTH fallback tests still pass, say so and stop: it would mean the guard is
not actually selecting the path it claims to. **Revert.**

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
using System.Collections.Generic;
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

    /// <summary>⚠ COMMENT-BLINDNESS IS THE DEFECT THIS REPO KEEPS SHIPPING, and both sweeps below need
    /// this. MEASURED: PerceptionManager.cs:1130 is a `///` doc comment reading "Wraps
    /// ResolveWindowCaptureGeometryAsync(handle," — the call-site sweep counted it as a fourth CALL,
    /// which is the entire reason an earlier draft of this plan called that test "known-red". It was not
    /// known-red; it was comment-blind. `///` starts with `//` so one prefix test covers both, and `*`
    /// covers the interior of a block comment.</summary>
    private static bool IsCode(string line)
    {
        var t = line.TrimStart();
        return !(t.StartsWith("//", System.StringComparison.Ordinal)
              || t.StartsWith("*", System.StringComparison.Ordinal));
    }

    private static List<(string File, int Line, string Text)> CodeLines(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .Where(x => IsCode(x.Text))
            .ToList();

    // The FULL-DESKTOP aggregator is the only caller that may clip. Every other call site takes the
    // unclipped default, and this pins that exactly one site names `clipToVirtualScreen: true`.
    //
    // ⚠ This sweep is the ONLY thing that catches a new caller silently inheriting the wrong yardstick.
    // A test of the yardstick logic itself cannot see a caller that never passed the parameter.
    [Fact]
    public void Exactly_one_production_call_site_clips_the_yardstick()
    {
        var clipping = CodeLines(RepoRoot())
            .Where(x => x.Text.Contains("clipToVirtualScreen: true")).ToList();
        Assert.Single(clipping);
        Assert.EndsWith("PerceptionManager.cs", clipping[0].File);
    }

    // Every call site of the geometry walk, counted. If this number changes, a new caller appeared and
    // somebody must decide its yardstick deliberately rather than inherit one.
    [Fact]
    public void The_geometry_walk_has_exactly_three_production_call_sites()
    {
        var calls = CodeLines(RepoRoot())
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

*(Both assertions go green in Step 6 of this task — and that WAS correct; Step 6's "known-red" caveat was the wrong half and has been removed. The count is **3** and stays 3 for the whole plan: the coordinator added in Task 17 takes the walk as a delegate and so adds no call site. An earlier draft predicted 4, which is also where the test's original NAME — `..._exactly_four_production_call_sites`, asserting 3 — came from. Renamed to `..._exactly_three_...`.)*

- [ ] **Step 3: Change the signature**

Replace the `ResolveWindowCaptureGeometryAsync` **signature and its doc-comment block** in `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — find it BY NAME (it was at `:848-853`, VERIFIED still correct at dispatch time, but every line citation in this plan has drifted at least once) — with:

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
Expected: **PASS, nothing red.** Build 0 warnings / 0 errors.

⚠ **This line used to say "PASS except the known-red `The_geometry_walk_has_exactly_four_production_call_sites`", which contradicted Step 2's own note that both assertions go green here.** Neither was right: the test was not known-red, it was **COMMENT-BLIND**. MEASURED — `PerceptionManager.cs:1130` is a `///` doc comment reading *"Wraps ResolveWindowCaptureGeometryAsync(handle,"*, and the sweep counted it as a fourth CALL, so `Assert.Equal(3, ...)` failed against 4. With comment lines filtered it counts the three real sites and passes. **A task in this repo never commits red** — and the headless gate does not exempt anything here.

- [ ] **Step 7: Prove the sweep is non-vacuous with a logic mutant**

**Mutant 1.** Temporarily change `PerceptionManager.cs:1181` to drop `clipToVirtualScreen: true`.
Expected: `Exactly_one_production_call_site_clips_the_yardstick` FAILS with zero matches. **Revert.**

**Mutant 2 — THE COMMENTED-OUT VARIANT, and it is mandatory here.** Do mutant 1 again, but ALSO paste `// clipToVirtualScreen: true` as a comment anywhere in `PerceptionManager.cs`.
Expected: **it still FAILS.** If it PASSES, `IsCode` is not filtering and the sweep is defeated by typing two slashes — which is the defect this repo has now shipped or nearly shipped FOUR times (item 12's property sweep, this plan's metadata sweep, its warning-code reachability gate, and this very test). **Revert both.**

**Mutant 3.** Add `_ = await ResolveWindowCaptureGeometryAsync(handle, null);` immediately after the existing call at `PerceptionManager.cs:1136` — inside `ResolveTextCaptureGeometryAsync`, where `handle` is already in scope and the method is already `async`.
Expected: `The_geometry_walk_has_exactly_three_production_call_sites` FAILS at 4. **Revert.**

⚠ **Use the DISCARD (`_ =`), not `var x = …`.** An unused local risks a build error under warnings-as-errors, and a mutant that fails the BUILD proves nothing — the test never runs. This plan has already specified two structural mutants that did exactly that (Tasks 6 and 7). If this one produces a build error anyway, say so and stop rather than tuning it.

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
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — the `CaptureGeometry` record declaration
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — **every `new CaptureGeometry(` site**
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureGeometryShapeTests.cs`

⚠ **EVERY LINE NUMBER BELOW IS ALREADY STALE BY THE TIME YOU READ IT — Task 12 runs first and inserts
~15 lines of doc comment above `:853`, shifting everything after it.** The citations were
`:1275-1276` for the record and `:854-863, 915-935, 963-971, 1110` for the construction sites; all were
**VERIFIED CORRECT immediately before Task 12 ran**, so they are accurate as a description and useless as
coordinates. **Find both by symbol.**

**MEASURED before Task 12 — there are exactly FOUR construction sites, all in `PerceptionManager.cs`,
and ZERO in `test/`:**

```
:858   denied         -> new CaptureGeometry(default, ..., false, true, procName, ...)
:863   minimized      -> new CaptureGeometry(default, ..., true, false, null, ...)
:964   no-overlap     -> new CaptureGeometry(captureBounds, ...)
:1110  the happy path -> new CaptureGeometry(captureBounds, pw, false, false, null, escalations)
```

`grep -rn "new CaptureGeometry(" src/` re-derives that list in one command. **Adding three fields to a
positional record means ALL FOUR must be updated or the project does not build** — the same shape as
Task 10's four call sites, where the plan listed three and the build broke.

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
Expected: **PASS, nothing red.** Build 0 warnings / 0 errors. *(Task 12's call-site count is no longer red — it was comment-blind, not known-red. See Task 12 Step 6.)*

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
