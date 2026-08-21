> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

## Phase 2 — The crop and the encode path

### Task 8: `WindowCropGeometry` — the pure function the whole design turns on

This is the single most reviewed block in the spec: six panel rounds each corrected it, and twice the correction landed *below* the text it was correcting. **Implement exactly the algorithm below and do not re-derive it.**

Three observations, and which one you use is the whole game:

| | |
|---|---|
| `W1` | the WINDOW rect observed during the UIA walk |
| `E` | the ELEMENT rect from that SAME walk (`= W1` for window scope) |
| `W2` | the `GetWindowRect` taken at capture time, which sized the bitmap |

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/WindowCropGeometry.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/WindowCropGeometryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class WindowCropGeometryTests
{
    // Window scope: E == W1, nothing moved, nothing resized. The crop is the whole bitmap and both
    // translations land back on the window's own origin.
    [Fact]
    public void Window_scope_static_window_is_the_whole_bitmap()
    {
        var w1 = new Rectangle(100, 200, 800, 600);
        var g = WindowCropGeometry.Compute(new Size(800, 600), e: w1, w1: w1, w2: w1);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(0, 0, 800, 600), g!.Value.Effective);
        Assert.Equal(new Rectangle(100, 200, 800, 600), g.Value.Absolute);
        Assert.Equal(new Rectangle(100, 200, 800, 600), g.Value.Reported);
    }

    // THE MOVE CASE. This is the numeric trace panel round 7 verified by hand and it is the reason W1
    // anchors the mask side: window (-8,-8,1936,1036) moves to (92,92,...), element at (100,200,300,50).
    // Using W2 on the way back would give absolute=(200,300) against masks sampled at (100,200) -- every
    // mask 100px off.
    [Fact]
    public void A_pure_move_keeps_masks_on_W1_and_reports_on_W2()
    {
        var w1 = new Rectangle(-8, -8, 1936, 1036);
        var w2 = new Rectangle(92, 92, 1936, 1036);
        var e  = new Rectangle(100, 200, 300, 50);
        var g = WindowCropGeometry.Compute(new Size(1936, 1036), e, w1, w2);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(108, 208, 300, 50), g!.Value.Effective);
        Assert.Equal(new Rectangle(100, 200, 300, 50), g.Value.Absolute);   // masks land here
        Assert.Equal(new Rectangle(200, 300, 300, 50), g.Value.Reported);   // pixels are actually here
    }

    // A window that GREW exposes area the UIA walk never inspected -- pixels no mask was computed for.
    // Intersect discards exactly that region because `relative` is W1-sized. Without this the design
    // returns unscanned pixels and calls it a success.
    [Fact]
    public void A_grown_window_is_cropped_back_to_the_region_that_was_scanned()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 1000, 700);
        var g = WindowCropGeometry.Compute(new Size(1000, 700), e: w1, w1: w1, w2: w2);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(0, 0, 800, 600), g!.Value.Effective);
        Assert.Equal(800, g.Value.Absolute.Width);
        Assert.Equal(600, g.Value.Absolute.Height);
    }

    [Fact]
    public void A_shrunk_window_clamps_so_nothing_reads_past_the_bitmap()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 500, 400);
        var g = WindowCropGeometry.Compute(new Size(500, 400), e: w1, w1: w1, w2: w2);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(0, 0, 500, 400), g!.Value.Effective);
    }

    // The invariant the whole subsection exists to protect, asserted on every case above at once.
    [Theory]
    [InlineData(0, 0, 800, 600, 0, 0, 800, 600)]
    [InlineData(-8, -8, 1936, 1036, 92, 92, 1936, 1036)]
    [InlineData(0, 0, 800, 600, 0, 0, 1000, 700)]
    [InlineData(0, 0, 800, 600, 0, 0, 500, 400)]
    public void Src_absolute_and_reported_always_share_one_size(
        int x1, int y1, int cx1, int cy1, int x2, int y2, int cx2, int cy2)
    {
        var w1 = new Rectangle(x1, y1, cx1, cy1);
        var w2 = new Rectangle(x2, y2, cx2, cy2);
        var g = WindowCropGeometry.Compute(new Size(cx2, cy2), e: w1, w1: w1, w2: w2);
        Assert.NotNull(g);
        Assert.Equal(g!.Value.Effective.Size, g.Value.Absolute.Size);
        Assert.Equal(g.Value.Effective.Size, g.Value.Reported.Size);
    }

    // An element that fell entirely outside the new bitmap. Returns null; the caller refuses. Unguarded,
    // Bitmap.Clone on this throws ArgumentException, which ScreenCapture.cs:40's COMException/
    // ExternalException filter does NOT catch, so it escapes as a raw unmapped exception.
    [Fact]
    public void An_element_entirely_outside_the_bitmap_returns_null()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 200, 150);
        var e  = new Rectangle(500, 400, 100, 40);
        Assert.Null(WindowCropGeometry.Compute(new Size(200, 150), e, w1, w2));
    }

    // GUARD ON EXTENTS, NOT IsEmpty. Rectangle.Intersect yields a ZERO-EXTENT rect at NON-ZERO
    // coordinates for rects that merely TOUCH along an edge, and IsEmpty is false there. This repo has
    // already shipped the wrong form of this exact guard and documents the resulting leak at
    // PerceptionManager.cs:942-946. An element touching the window's right edge is that case.
    [Fact]
    public void A_zero_width_touching_intersection_is_rejected_even_though_IsEmpty_is_false()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(800, 100, 50, 30);   // starts exactly at the right edge
        var g = WindowCropGeometry.Compute(new Size(800, 600), e, w1, w2: w1);
        Assert.Null(g);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~WindowCropGeometryTests"`
Expected: FAIL — `WindowCropGeometry` does not exist (CS0103).

- [ ] **Step 3: Write the implementation**

Create `src/FlaUI.Mcp.Core/Perception/WindowCropGeometry.cs`:

```csharp
using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>The three rectangles a crop produces. Sizes are equal by construction.</summary>
public readonly record struct CropGeometry(Rectangle Effective, Rectangle Absolute, Rectangle Reported);

/// <summary>The crop GEOMETRY -- a PURE function of (bitmap size, E, W1, W2). Rectangles in, rectangles
/// out: no OS handle, no pixels, no allocation. This is what makes the element-crop invariant and the
/// clamp path HEADLESS-testable, which the spec's Testing section requires; if this logic lived inside
/// the component that calls GetWindowRect and PrintWindow, those tests could not exist.
///
/// Crop EXTRACTION -- `src = bitmap cropped to Effective` -- is a separate, decision-free bitmap
/// operation that lives in the seam. Every rectangle it uses was already computed here.</summary>
public static class WindowCropGeometry
{
    /// <summary>Compute the crop. Returns null when the intersection has no AREA, which the caller turns
    /// into a defined refusal.</summary>
    /// <param name="bitmap">The PrintWindow bitmap's size. Its (0,0) corresponds to W2's top-left.</param>
    /// <param name="e">The element rect from the UIA walk, absolute screen coords. EQUALS w1 for window
    /// scope -- there is no window-scope special case, and adding one caused three separate defects.</param>
    /// <param name="w1">The window rect from that SAME walk. Anchors BOTH mask-side translations.</param>
    /// <param name="w2">The GetWindowRect taken at capture time. Anchors ONLY the reported origin.</param>
    public static CropGeometry? Compute(Size bitmap, Rectangle e, Rectangle w1, Rectangle w2)
    {
        // W1 on the way IN. Using W2 here would misalign the crop by the movement delta on a pure move.
        var relative = new Rectangle(e.X - w1.X, e.Y - w1.Y, e.Width, e.Height);

        var effective = Rectangle.Intersect(relative, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

        // ⚠ EXTENTS, not IsEmpty. Rectangle.Intersect compares with >= and so yields a zero-extent rect
        // at NON-ZERO coordinates -- (100,50,0,30) -- for two rects that merely touch along an edge, and
        // IsEmpty is FALSE there. MEASURED on this runtime. The existing yardstick guard at
        // PerceptionManager.cs:947 tests exactly this way, for exactly this reason.
        if (effective.Width <= 0 || effective.Height <= 0) return null;

        // Clamp ONCE, then derive BOTH operands from that one rectangle, so they cannot drift. An earlier
        // draft clamped the crop while handing Encode the UNCLAMPED bounds: the scale factor then came off
        // the clamped src.Width while masks translated against the unclamped origin -- the very
        // misalignment this guard exists to prevent, reintroduced by its own fix.
        return new CropGeometry(
            effective,
            // W1 on the way BACK for the MASK rectangle. Encode's arithmetic is absolute and the masks
            // were sampled alongside E, so they must meet at the same origin.
            new Rectangle(effective.X + w1.X, effective.Y + w1.Y, effective.Width, effective.Height),
            // W2 for the REPORTED rectangle -- where the pixels actually are. These two origins differ by
            // exactly the movement delta, and conflating them makes the response lie about a moved window.
            new Rectangle(effective.X + w2.X, effective.Y + w2.Y, effective.Width, effective.Height));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~WindowCropGeometryTests"`
Expected: PASS — **10 passed** (6 `[Fact]` + 1 `[Theory]` × 4 `[InlineData]` rows).

⚠ **This said "11 passed" until it was executed.** MEASURED: `Failed: 0, Passed: 10, Total: 10`. Same
count-fossil family as Task 5's "11 passed" and "seven wire codes" — a review changed the block and the
prose beside it was not updated.

- [ ] **Step 5: Prove the gates are non-vacuous with three logic mutants**

Each mutant must turn a *named* test red. Run them one at a time and revert each.

1. Change `effective.X + w1.X` to `effective.X + w2.X` in the `Absolute` line.
   Expected: `A_pure_move_keeps_masks_on_W1_and_reports_on_W2` FAILS with absolute `(200,300)` — the exact 100px misalignment from the spec's trace.
2. Change the guard to `if (effective.IsEmpty) return null;`.
   Expected: `A_zero_width_touching_intersection_is_rejected_even_though_IsEmpty_is_false` FAILS.
3. Change `relative` to use `e` unmodified (drop the `- w1` translation).
   Expected: **`A_pure_move_keeps_masks_on_W1_and_reports_on_W2` and `Window_scope_static_window_is_the_whole_bitmap`** both FAIL.

   ⚠ **This step used to name `A_grown_window_is_cropped_back_to_the_region_that_was_scanned` as the
   second failure. That is IMPOSSIBLE and it was measured.** In that test `w1 = (0, 0, 800, 600)` — the
   origin — so `e.X - w1.X` subtracts zero and dropping the translation is a **no-op** there; it stays
   green. The test that does go red is `Window_scope_static_window_is_the_whole_bitmap`, whose
   `w1 = (100, 200, 800, 600)` is off-origin. MEASURED: exactly those two red, the other eight green.

   **The general lesson, and it applies to every mutant in this plan:** a mutant that removes a
   translation can only be caught by a case whose translation is NON-ZERO. Half these fixtures sit at
   the origin, so they are structurally blind to it. When predicting which test a mutant kills, check
   the fixture's numbers — do not reason from the test's name.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCropGeometry.cs test/FlaUI.Mcp.Tests/Perception/WindowCropGeometryTests.cs
git commit -m "feat(capture): the pure crop geometry - effective, absolute, reported"
```

### Task 9: `Encode` takes the two rectangles, the method and the warnings

`Encode` builds the `CaptureResult`, so it must be HANDED the `W2`-anchored rectangle — it cannot derive one from the other. Both cross the call.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` — the `Encode` method (was cited as `:45-71`; Task 6 shifted it to `:55-83`, so find it by NAME)
- Test: `test/FlaUI.Mcp.Tests/Perception/EncodeContractTests.cs`

- [ ] **Step 1: Make `Encode` internal so it can be tested directly**

`Encode` is currently `private static`. **Change it to `internal static`. That is the ENTIRE step.**

~~and add to `src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj` inside the existing `<PropertyGroup>` if not already present:~~

```xml
  <!-- STRUCK OUT - DO NOT ADD THIS. See the warning immediately below. -->
  <ItemGroup>
    <InternalsVisibleTo Include="FlaUI.Mcp.Tests" />
  </ItemGroup>
```

⚠ **ADD NOTHING — the grant already exists, and the check written here would not have found it.** That
check greps ONE FILE (`FlaUI.Mcp.Core.csproj`) for a grant that lives somewhere else:
`src/FlaUI.Mcp.Core/Properties/AssemblyInfo.cs:2` declares
`[assembly: InternalsVisibleTo("FlaUI.Mcp.Tests")]`. So the narrow grep answers "not present", the
implementer adds the MSBuild item, and — since `GenerateAssemblyInfo` is not set anywhere and therefore
defaults ON — the SDK emits a SECOND attribute on top of the hand-written one. Redundant, and it changes
a build file for no reason.

**Grep the CONCEPT, not one spelling of it:**

```bash
grep -rn "InternalsVisibleTo" --include=*.cs --include=*.csproj --include=*.props src/FlaUI.Mcp.Core/
```

MEASURED: that returns `src/FlaUI.Mcp.Core/Properties/AssemblyInfo.cs:2`. **Core's internals are already
visible to `FlaUI.Mcp.Tests`; this step is a no-op and the `.csproj` must NOT be touched.** Drop it from
the Step 7 `git add` line too.

- [ ] **Step 2: Write the failing test**

```csharp
using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class EncodeContractTests
{
    private static Bitmap Solid(int w, int h, Color c)
    {
        var b = new Bitmap(w, h);
        using var g = Graphics.FromImage(b);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 0, 0, w, h);
        return b;
    }

    // THE AB-2 CASE, and it is leak-shaped. A WINDOW-sized bitmap cropped to an ELEMENT-sized rect: the
    // mask must land relative to the ELEMENT's origin, not the window's. Under the scrape this invariant
    // was structural; under PrintWindow it must be re-established by hand.
    [Fact]
    public void A_mask_lands_relative_to_the_absolute_rectangle_it_was_given()
    {
        // src is the already-cropped element region: 200x100 at absolute (300,400).
        using var src = Solid(200, 100, Color.White);
        var absolute = new Rectangle(300, 400, 200, 100);
        var mask = new Rectangle(350, 430, 50, 20);   // 50,30 inside the element

        var r = ScreenCapture.Encode(src, absolute, reported: absolute, new[] { mask }, maxWidth: 0,
                                     method: "printWindow", warnings: System.Array.Empty<CaptureWarning>());

        Assert.Equal(1, r.Redactions);
        using var ms = new System.IO.MemoryStream(r.Png);
        using var outBmp = new Bitmap(ms);
        Assert.Equal(Color.Black.ToArgb(), outBmp.GetPixel(60, 40).ToArgb());   // inside the mask
        Assert.Equal(Color.White.ToArgb(), outBmp.GetPixel(10, 10).ToArgb());   // outside it
    }

    // X/Y come from REPORTED (where the pixels are), never from ABSOLUTE (where the masks are). They
    // differ by exactly the movement delta whenever the window moved.
    [Fact]
    public void XY_report_the_W2_anchored_rectangle_not_the_mask_rectangle()
    {
        using var src = Solid(80, 60, Color.Gray);
        var absolute = new Rectangle(100, 200, 80, 60);
        var reported = new Rectangle(300, 500, 80, 60);

        var r = ScreenCapture.Encode(src, absolute, reported, System.Array.Empty<Rectangle>(), 0,
                                     "printWindow", System.Array.Empty<CaptureWarning>());

        Assert.Equal(300, r.X);
        Assert.Equal(500, r.Y);
        Assert.Equal(80, r.W);
        Assert.Equal(60, r.H);
    }

    [Fact]
    public void The_method_and_warnings_reach_the_result_unchanged()
    {
        using var src = Solid(10, 10, Color.Red);
        var warn = new[] { CaptureWarnings.For(CaptureWarnings.PopupsNotRendered) };
        var r = ScreenCapture.Encode(src, new Rectangle(0, 0, 10, 10), new Rectangle(0, 0, 10, 10),
                                     System.Array.Empty<Rectangle>(), 0, "printWindow", warn);
        Assert.Equal("printWindow", r.CaptureMethod);
        Assert.Single(r.CaptureWarnings);
        Assert.Equal("popupsNotRendered", r.CaptureWarnings[0].Code);
    }

    // THE CLAMP PATH. src is SMALLER than the element rect originally requested, because the crop clamped
    // it. Masks must still land correctly and nothing may throw. This is the regression test for the
    // defect panel round 2's own fix introduced.
    [Fact]
    public void A_clamped_src_still_places_masks_correctly()
    {
        using var src = Solid(120, 90, Color.White);            // clamped down from 200x100
        var absolute = new Rectangle(300, 400, 120, 90);        // derived FROM the clamped rect
        var mask = new Rectangle(320, 420, 30, 20);

        var r = ScreenCapture.Encode(src, absolute, absolute, new[] { mask }, 0, "printWindow",
                                     System.Array.Empty<CaptureWarning>());

        Assert.Equal(1, r.Redactions);
        using var ms = new System.IO.MemoryStream(r.Png);
        using var outBmp = new Bitmap(ms);
        Assert.Equal(Color.Black.ToArgb(), outBmp.GetPixel(25, 25).ToArgb());
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~EncodeContractTests"`
Expected: FAIL — `Encode` takes 4 parameters, not 7.

- [ ] **Step 4: Rewrite `Encode`**

Replace the **whole `Encode` method** in `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` — from its `private static CaptureResult Encode(` signature through its closing brace — with:

⚠ **Anchor on the SYMBOL, not the line numbers.** This step said "lines 45–71" until execution; Task 6 inserted a nine-line doc comment above `CaptureResult` and pushed `Encode` down to **55–83**. It will shift again as later tasks edit this file, so find the method by name rather than trusting any number written here.

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~EncodeContractTests"`
Expected: PASS — 4 passed.

- [ ] **Step 6: Prove the gates are non-vacuous with two logic mutants**

1. Change `reported.X, reported.Y` back to `absolute.X, absolute.Y`.
   Expected: `XY_report_the_W2_anchored_rectangle_not_the_mask_rectangle` FAILS with `(100,200)`.
2. Change `clip.X - absolute.X` to `clip.X - reported.X`.
   Expected: `A_mask_lands_relative_to_the_absolute_rectangle_it_was_given` still passes (the two are equal there) but `XY_report...` does not cover it — so **also** temporarily change that first test to pass `reported: new Rectangle(500, 600, 200, 100)` and confirm the mask assertion then FAILS. Revert both.

   *(This second mutant is the one that proves the two rectangles are genuinely doing different jobs. If it cannot be made to fail, the test set does not yet pin the distinction and the test must be strengthened before moving on.)*

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs test/FlaUI.Mcp.Tests/Perception/EncodeContractTests.cs
git commit -m "feat(capture): Encode takes absolute+reported, method and warnings"
```

### Task 10: `CaptureRectangle` learns its scope — and the third caller is wired explicitly

The scrape seam now has three callers with divergent requirements: full-desktop must evaluate `desktopCanvasUniform`, a fallback scrape must not and carries a `scrapeFallback*` code its caller decided, and the OCR path must not either.

⚠⚠ **THIS TASK HAD A BUILD-BREAKING OMISSION AND IT IS FIXED BELOW: there are FOUR call sites, not three.**
MEASURED — `grep -rn "ScreenCapture.CaptureRectangle(" src/` returns:

```
FindTextTools.cs:62      OCR
FindTextTools.cs:110     OCR
ScreenshotTools.cs:49    full desktop
ScreenshotTools.cs:58    window / element   <-- the plan never listed this one
```

Step 3 makes `scope` and `warningsSoFar` REQUIRED (no defaults, deliberately). Updating only three call
sites leaves `:58` calling a five-parameter method with three arguments — **CS7036, the project does not
build.** The plan's own index says `ScreenshotTools.cs:49,58` are both modified; only this task's Files
list and Step 4 dropped `:58`.

*(Why it was missed: the index describes `:58` as the caller being REPLACED by the coordinator in
Phase 6, so it was mentally filed as "handled later". But it still has to compile in the meantime.)*

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` — the `CaptureRectangle` method (find it by NAME; earlier line citations in this plan have already drifted twice)
- Modify: `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:49` **and `:58`**
- Modify: `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:62,110`
- Modify: `test/FlaUI.Mcp.Tests/Perception/ScreenCaptureTests.cs:28`
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureRectangleCallSiteTests.cs`

- [ ] **Step 1: Write the failing call-site sweep**

This is a **source sweep**, the idiom the repo already owns (`BuildPropertySweepTests`). It exists because no test of `CaptureRectangle`'s own logic can catch a CALLER that forgot the parameter.

```csharp
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureRectangleCallSiteTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // Every production call site must name its scope EXPLICITLY. There is no safe default: the scope
    // decides which detector runs, and a caller that inherits one silently gets the wrong answer. The
    // OCR path (FindTextTools) is the caller this sweep exists for -- it was invisible to the spec until
    // it was measured, and it is the one most likely to be forgotten again.
    [Fact]
    public void Every_production_CaptureRectangle_call_names_its_scope()
    {
        var root = RepoRoot();
        var sites = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .Where(x => x.Text.Contains("ScreenCapture.CaptureRectangle("))
            .ToList();

        // ⚠ FOUR CALL SITES, and the DECLARATION is not among them. The declaration reads
        // `public static CaptureResult CaptureRectangle(` -- no `ScreenCapture.` prefix -- so the
        // Contains() filter above never matches it. An earlier version of this test asserted
        // "1 declaration + 3 call sites" and then filtered for the declaration text that cannot be
        // present; it would have failed 4 != 3 even with every call site correctly updated.
        Assert.Equal(4, sites.Count);

        // Kept as a defensive filter in case the declaration is ever rewritten to self-qualify, but it
        // matches nothing today -- which is why the expected count is unchanged at 4.
        var calls = sites.Where(x => !x.Text.Contains("public static CaptureResult")).ToList();
        Assert.Equal(4, calls.Count);

        foreach (var c in calls)
            Assert.True(Regex.IsMatch(c.Text, @"CaptureScope\.\w+"),
                $"{Path.GetFileName(c.File)}:{c.Line} calls CaptureRectangle without naming a CaptureScope");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureRectangleCallSiteTests"`
Expected: FAIL — no call site names a scope.

- [ ] **Step 3: Change the signature**

Replace `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` lines 36–43 with:

```csharp
    /// <summary>Scrape a screen rectangle. Serves THREE callers with divergent requirements, and cannot
    /// tell them apart without being told, which is why `scope` has no default:
    ///   FullDesktop -- runs the desktopCanvasUniform detector.
    ///   OcrRegion   -- runs no detector; it is neither a whole desktop nor a PrintWindow capture.
    ///   Window/Element -- a FALLBACK scrape. Runs no detector, and its caller has already decided a
    ///     scrapeFallback* code, which arrives in `warningsSoFar`.
    ///
    /// ⚠ On this path `absolute` and `reported` are the SAME rectangle. The scrape captures exactly the
    /// region it was asked for, in one observation -- there is no W1/W2 pair, so the two cannot differ.
    /// Only the PrintWindow path derives them separately.</summary>
    public static CaptureResult CaptureRectangle(Rectangle absolute, IReadOnlyList<Rectangle> redactAbsolute,
                                                 int maxWidth, CaptureScope scope,
                                                 IReadOnlyList<CaptureWarning> warningsSoFar)
    {
        CaptureImage cap;
        try { cap = Capture.Rectangle(absolute, null); }
        catch (System.Exception ex) when (ex is COMException or System.Runtime.InteropServices.ExternalException)
        { throw new ToolException(ToolErrorCode.CaptureUnavailable, "Screen capture failed (session may be disconnected/locked).", "reconnect to restore rendering"); }
        using (cap)
        {
            var warnings = warningsSoFar;
            // The detector runs where the bitmap lives, so the seam cannot delegate this decision upward.
            if (scope.RunsDesktopUniformDetector() && UniformCanvasDetector.IsUniform(cap.Bitmap))
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
            // is the one gap this fold does not close.
            else if (scope == CaptureScope.Window && UniformCanvasDetector.IsUniform(cap.Bitmap))
                warnings = Append(warnings, CaptureWarnings.For(CaptureWarnings.UniformCanvas));
            return Encode(cap.Bitmap, absolute, absolute, redactAbsolute, maxWidth, "screenScrape", warnings);
        }
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
```

Add `using System.Collections.Generic;` to the file's using block if the analyzer flags it.

- [ ] **Step 4: Update all FOUR call sites**

`src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:49`:

```csharp
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(
                    vbounds, desk.Rects, maxWidth, CaptureScope.FullDesktop,
                    System.Array.Empty<CaptureWarning>()));
```

`src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:58` — **the site the plan originally omitted**:

```csharp
                // ⚠ SCOPE IS NOT CONSTANT HERE. This one call serves BOTH window and element capture,
                // and the two are not interchangeable: CaptureRectangle emits uniformCanvas for Window
                // and deliberately NOT for Element, because on an element the captured region is the
                // ELEMENT and claiming "the window rendered as one colour" would be a statement the tool
                // never measured. `@ref` is exactly the window-vs-element discriminator the enclosing
                // branch already used to resolve `geo`.
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(
                    geo.Bounds, geo.MaskRects, maxWidth,
                    @ref is null ? CaptureScope.Window : CaptureScope.Element,
                    System.Array.Empty<CaptureWarning>()));
```

⚠ **This does change behaviour at this task, and the change is intended.** A uniform WINDOW scrape can
now raise `uniformCanvas` where before it raised nothing. Nothing projects `CaptureWarnings` onto the
wire until Task 20, so the warning is computed and discarded for now — but if the headless suite goes
red here, read the failure before "fixing" it: it may be a test that asserted the old silence.

`src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:62`:

```csharp
            // ⚠ OcrRegion, not Window. This path SCRAPES and always has; it is neither a whole desktop
            // nor a PrintWindow capture, so it runs NO uniform detector. Item 8 did not change this
            // path's backend -- it only forced the scope to be named. See risk 6.
            var cap = await Task.Run(() => ScreenCapture.CaptureRectangle(
                geo.CaptureBounds, geo.MaskRects, maxWidth: 0, CaptureScope.OcrRegion,
                System.Array.Empty<CaptureWarning>())); // maxWidth:0 -> best OCR accuracy (still 1920-clamped)
```

`src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:110` — the same replacement, same comment omitted (the one above covers both; add `// scope: see :62`):

```csharp
                var cap = await Task.Run(() => ScreenCapture.CaptureRectangle(
                    geo.CaptureBounds, geo.MaskRects, maxWidth: 0, CaptureScope.OcrRegion,
                    System.Array.Empty<CaptureWarning>())); // scope: see the note at :62
```

- [ ] **Step 5: Update the existing Desktop test at `ScreenCaptureTests.cs:28`**

```csharp
        var result = ScreenCapture.CaptureRectangle(winRect, new[] { secretRect }, 1600,
                                                    CaptureScope.Window, System.Array.Empty<CaptureWarning>());
```

- [ ] **Step 6: Run the sweep and the headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed, build 0 warnings / 0 errors.

- [ ] **Step 7: Prove the sweep is non-vacuous with a logic mutant**

Temporarily revert `FindTextTools.cs:110` to omit `CaptureScope.OcrRegion` (pass the old four arguments and let it fail to compile — then instead pass `default` for scope, which compiles). Re-run the sweep. Expected: `Every_production_CaptureRectangle_call_names_its_scope` FAILS naming `FindTextTools.cs:110`. **Revert.**

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Server/Tools/FindTextTools.cs test/FlaUI.Mcp.Tests/
git commit -m "feat(capture): CaptureRectangle takes an explicit scope; wire the OCR third caller"
```

---
