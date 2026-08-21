> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

## Phase 1 — The contract types

Everything here is headless, has no dependency on the measurements, and is what every later phase compiles against. Build it first so the decomposition is pinned in real signatures rather than in prose — that is the layer thirty panel rounds could not settle.

### Task 4: `CaptureScope` — the enum both seams branch on

Two seams must branch on scope and neither can infer it. `CaptureWindow` cannot infer window-vs-element from `E == W1`, because an element that exactly covers its window would take the wrong branch. `CaptureRectangle` cannot infer full-desktop from anything it receives.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/CaptureScope.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureScopeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureScopeTests
{
    // The scrape seam runs the desktopCanvasUniform detector for FullDesktop and for NOTHING else.
    // OcrRegion and a fallback scrape must both come back false, and they are different callers, so
    // this is pinned per-value rather than as "not FullDesktop".
    [Theory]
    [InlineData(CaptureScope.FullDesktop, true)]
    [InlineData(CaptureScope.Window, false)]
    [InlineData(CaptureScope.Element, false)]
    [InlineData(CaptureScope.OcrRegion, false)]
    public void Only_full_desktop_runs_the_desktop_detector(CaptureScope scope, bool expected)
        => Assert.Equal(expected, scope.RunsDesktopUniformDetector());

    // The two-stage uniform detector needs a full-window bitmap distinct from the crop, which only the
    // printWindow backend produces. Every scrape path -- including a fallback -- must be false.
    [Theory]
    [InlineData(CaptureScope.Window, true)]
    [InlineData(CaptureScope.Element, true)]
    [InlineData(CaptureScope.FullDesktop, false)]
    [InlineData(CaptureScope.OcrRegion, false)]
    public void Only_window_and_element_acquire_per_window(CaptureScope scope, bool expected)
        => Assert.Equal(expected, scope.AcquiresPerWindow());

    [Fact]
    public void OcrRegion_exists_because_FindTextTools_is_a_third_caller()
        => Assert.Equal(4, System.Enum.GetValues<CaptureScope>().Length);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureScopeTests"`
Expected: FAIL — `CaptureScope` does not exist (CS0246).

- [ ] **Step 3: Write the implementation**

Create `src/FlaUI.Mcp.Core/Perception/CaptureScope.cs`:

```csharp
namespace FlaUI.Mcp.Core.Perception;

/// <summary>Which caller a capture seam is serving. Neither seam can infer this from its other
/// arguments, and both must branch on it, so it crosses the boundary explicitly (spec §1, data flow).
///
/// ⚠ CaptureWindow must NOT infer window-vs-element from `E == W1`: an element that exactly covers its
/// window would take the resize branch meant for the other scope.
///
/// ⚠ OcrRegion is a REAL third caller, not a placeholder. FindTextTools.cs:62 and :110 back
/// desktop_find_text and desktop_wait_for_text, reach the mask walk through
/// ResolveTextCaptureGeometryAsync, and would otherwise inherit every default this feature adds. The
/// spec's risk 6 said CaptureRectangle had "one other caller" until this was measured.</summary>
public enum CaptureScope
{
    /// <summary>desktop_screenshot with a window handle and no ref. Acquires via PrintWindow.</summary>
    Window,
    /// <summary>desktop_screenshot with a window handle AND a ref. Acquires via PrintWindow, then crops.</summary>
    Element,
    /// <summary>desktop_screenshot with no window. Scrapes the virtual screen; the ONLY scope that runs
    /// the desktopCanvasUniform detector.</summary>
    FullDesktop,
    /// <summary>desktop_find_text / desktop_wait_for_text. Scrapes a window or a sub-region of one.
    /// Runs NO detector: it is neither full-desktop nor a PrintWindow capture, so it has no second
    /// operand for the two-stage comparison and no whole-desktop claim to make.</summary>
    OcrRegion,
}

public static class CaptureScopeExtensions
{
    /// <summary>TRUE only for FullDesktop. A fallback scrape and the OCR path both get NO detector:
    /// the two-stage comparison has no second operand on a scrape, and neither is a whole desktop.</summary>
    public static bool RunsDesktopUniformDetector(this CaptureScope scope) => scope == CaptureScope.FullDesktop;

    /// <summary>TRUE for the two scopes that acquire through PrintWindow. Note this describes the scope's
    /// INTENDED backend, not the backend a given attempt actually used -- a Window-scope capture that fell
    /// back to the scrape still answers true here, which is why the uniform detectors are additionally
    /// gated on the method that produced the image.</summary>
    public static bool AcquiresPerWindow(this CaptureScope scope)
        => scope is CaptureScope.Window or CaptureScope.Element;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureScopeTests"`
Expected: PASS — 9 passed.

- [ ] **Step 5: Prove the gate is non-vacuous with a logic mutant**

Temporarily change `RunsDesktopUniformDetector` to `=> scope != CaptureScope.Window;`. Re-run. Expected: `Only_full_desktop_runs_the_desktop_detector` FAILS on the `Element` and `OcrRegion` rows. **Revert the mutant.**

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/CaptureScope.cs test/FlaUI.Mcp.Tests/Perception/CaptureScopeTests.cs
git commit -m "feat(capture): CaptureScope - the enum both seams branch on"
```

### Task 5: `CaptureWarning` and the six codes

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/CaptureWarning.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureWarningTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureWarningTests
{
    // The wire contract. These strings are read by every consuming agent, so they are pinned here
    // rather than left to drift. camelCase, matching every other field in the response.
    [Theory]
    [InlineData("uniformCanvas")]
    [InlineData("desktopCanvasUniform")]
    [InlineData("elementCanvasUniform")]
    [InlineData("popupsNotRendered")]
    [InlineData("scrapeFallbackTargetUnresponsive")]
    [InlineData("scrapeFallbackTargetChanging")]
    public void Every_shipped_code_has_a_recourse(string code)
    {
        var w = CaptureWarnings.For(code);
        Assert.Equal(code, w.Code);
        Assert.False(string.IsNullOrWhiteSpace(w.Recourse));
    }

    // ⚠ SIX. `windowResized` was retired at panel round 11 when the path that emitted it was found to
    // be a leak and removed. A code that cannot fire teaches a consumer that its condition never happens.
    [Fact]
    public void Exactly_six_codes_ship()
        => Assert.Equal(6, CaptureWarnings.AllCodes.Count);

    // Every shipped code must have at least one emission site in production. This is the other half of
    // the retirement: it is what would have caught `windowResized` going dead on its own.
    [Fact]
    public void No_shipped_code_is_unreachable()
    {
        var root = RepoRoot();
        var src = string.Join("\n", System.IO.Directory.EnumerateFiles(
            System.IO.Path.Combine(root, "src"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Select(f => StripComments(System.IO.File.ReadAllText(f))));
        foreach (var c in CaptureWarnings.AllCodes)
        {
            var member = char.ToUpperInvariant(c[0]) + c.Substring(1);
            Assert.True(src.Contains("CaptureWarnings." + member),
                $"'{c}' is documented but never emitted - retire it or emit it");
        }
    }

    /// <summary>⚠ COMMENTS STRIPPED FIRST, and this is the SECOND test in this plan to need it. Without
    /// it, a code named only in a comment — and this plan's comments name these codes constantly, to
    /// explain the paths that emit them — satisfies the gate while nothing emits it. That is the same
    /// defect item 12 shipped and the same one the metadata sweep had, arriving a third time in the test
    /// written to prevent a code going dead. MEASURED: the substring check matches a commented line.</summary>
    private static string StripComments(string source)
    {
        var noBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join("\n", noBlocks.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));
    }

    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "FlaUI.Mcp.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // §5: `code` is what a caller branches on, so it must be a stable identifier -- never prose.
    [Fact]
    public void Codes_are_camelCase_identifiers_not_sentences()
        => Assert.All(CaptureWarnings.AllCodes, c =>
        {
            Assert.DoesNotContain(' ', c);
            Assert.True(char.IsLower(c[0]), $"'{c}' must start lowercase");
        });

    [Fact]
    public void An_unknown_code_throws_rather_than_inventing_a_recourse()
        => Assert.Throws<System.ArgumentOutOfRangeException>(() => CaptureWarnings.For("notACode"));
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureWarningTests"`
Expected: FAIL — `CaptureWarnings` does not exist (CS0103).

- [ ] **Step 3: Write the implementation**

Create `src/FlaUI.Mcp.Core/Perception/CaptureWarning.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One condition that may make a returned image unusable. `Code` is a stable identifier a
/// caller branches on; `Recourse` is the sentence telling an agent what to do instead.
///
/// ⚠ Deliberately an OBJECT, not a bare sentence. The analogy this field is built on is
/// unmaskedProcesses -- an always-present list, empty when nothing is wrong -- and that analogy holds
/// only on the axis where the entries are programmatic identifiers. A list of English prose would force
/// a consumer to regex wording that will drift, which is a worse contract than the boolean it replaced.</summary>
public sealed record CaptureWarning(string Code, string Recourse);

/// <summary>The six codes this feature ships, with their recourse text. Centralised so the wire
/// contract has exactly one definition -- §5 settles these strings, not the implementation.</summary>
public static class CaptureWarnings
{
    public const string UniformCanvas = "uniformCanvas";
    public const string DesktopCanvasUniform = "desktopCanvasUniform";
    public const string ElementCanvasUniform = "elementCanvasUniform";
    public const string PopupsNotRendered = "popupsNotRendered";
    public const string ScrapeFallbackTargetUnresponsive = "scrapeFallbackTargetUnresponsive";
    public const string ScrapeFallbackTargetChanging = "scrapeFallbackTargetChanging";

    private static readonly Dictionary<string, string> Recourses = new(StringComparer.Ordinal)
    {
        // ⚠ THE TEXT MUST BE TRUE ON BOTH BACKENDS, and an earlier version said "rendered" -- which is
        // false of a scrape, where nothing rendered and the screen was photographed. The peer argued from
        // that wording that the code should not fire on a scrape at all. Rejected: a uniform scrape of a
        // window's rect is very often a solid OCCLUDER, which is precisely the hazard the fallback
        // carries, and this design's standing rule is that an incorrect warning is cheap while an
        // incorrect refusal is not. The wording was the defect, not the firing.
        // *(AGY-AFTER panel over this plan, round 9, Adversary of the Reviewer.)*
        [UniformCanvas] =
            "The captured region came back as a single colour, so this image may not be usable. Under " +
            "captureMethod 'printWindow' that means the window did not render; under 'screenScrape' it " +
            "may be a solid window covering the target, or a genuinely blank region. Either way, read " +
            "the UIA tree with desktop_snapshot rather than trusting these pixels.",
        [DesktopCanvasUniform] =
            "The whole desktop came back a single colour. The usual causes are a secure desktop (a UAC " +
            "prompt), DRM-protected content, or a session in transition. UIA is normally blocked in " +
            "those states too, so desktop_snapshot will not help - wait for the condition to clear and " +
            "re-capture.",
        [ElementCanvasUniform] =
            "This element's pixels may have failed to render even though the window as a whole did - a " +
            "hardware-accelerated child viewport is the usual cause. Verify through the UIA tree before " +
            "concluding the control is blank.",
        [PopupsNotRendered] =
            "An open menu, dropdown or tooltip belonging to this window is a separate top-level window " +
            "and may be missing from this image - structurally absent under printWindow, and cropped off " +
            "under screenScrape wherever it extends beyond the window's rect. Its absence is not evidence " +
            "it failed to open - read the UIA tree to see it.",
        [ScrapeFallbackTargetUnresponsive] =
            "This image is a screen scrape, so anything overlapping the window is in it - treat occlusion " +
            "as possible. The target is not pumping messages: it will not respond to input either, so do " +
            "not queue clicks against it.",
        [ScrapeFallbackTargetChanging] =
            "This image is a screen scrape, so treat occlusion as possible. The target is changing " +
            "continuously - it is alive and busy, not stuck; waiting and re-capturing may succeed.",
    };

    // ⚠ SIX, not seven. `windowResized` was retired at panel round 11 and this is not an omission.
    // It existed for one path: a window-scope capture that resized with an EMPTY mask set, which
    // continued to the crop and warned. That path was a LEAK -- an empty mask set means "nothing
    // sensitive was found in the T1 layout", not "nothing in this window is sensitive", and a reflow can
    // move or create content the walk never masked. A resize now always retries, so no image is ever
    // returned FOR a window that resized, and a code describing one would never fire.
    // **A documented code that cannot fire is worse than no code**: a consumer writes a handler for it
    // and concludes, from its absence, that no window ever resizes.
    public static IReadOnlyList<string> AllCodes { get; } = new[]
    {
        UniformCanvas, DesktopCanvasUniform, ElementCanvasUniform,
        PopupsNotRendered, ScrapeFallbackTargetUnresponsive, ScrapeFallbackTargetChanging,
    };

    /// <summary>Build the warning for a code. Throws on an unknown code rather than inventing a
    /// recourse: a warning whose instruction is empty is the failure mode §3 exists to prevent.</summary>
    public static CaptureWarning For(string code)
        => Recourses.TryGetValue(code, out var r)
            ? new CaptureWarning(code, r)
            : throw new ArgumentOutOfRangeException(nameof(code), code, "not a shipped capture-warning code");
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureWarningTests"`
Expected: PASS — 11 passed.

- [ ] **Step 5: Prove the gate is non-vacuous with a logic mutant**

Temporarily set `[PopupsNotRendered]` to `""`. Re-run. Expected: `Every_shipped_code_has_a_recourse` FAILS on the `popupsNotRendered` row. **Revert.**

Then TWO mutants for the reachability gate, and the second matters more than the first:

1. Temporarily add `"windowResized"` back to `AllCodes` without adding an emission site.
   Expected: `No_shipped_code_is_unreachable` FAILS naming it, and `Exactly_six_codes_ship` FAILS at 7.
   **That is the gate that would have caught this code going dead on its own, so see it go red once.**
2. Do the same, but ALSO add `// CaptureWarnings.WindowResized` as a COMMENT in any production file.
   Expected: **it still FAILS.** If it passes, `StripComments` is not working and the gate is defeated by
   typing two slashes — the third time that defect would have shipped in this repo, after item 12's
   property sweep and this plan's own metadata sweep.

**Revert both.**

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/CaptureWarning.cs test/FlaUI.Mcp.Tests/Perception/CaptureWarningTests.cs
git commit -m "feat(capture): CaptureWarning and the seven wire codes with their recourse text"
```

### Task 6: `CaptureResult` gains its two fields — appended, never inserted

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs:9`
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs:69` (the one construction site)
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureResultShapeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureResultShapeTests
{
    // CaptureResult is a POSITIONAL record constructed positionally. A field added mid-list silently
    // rebinds arguments wherever the types happen to line up -- int X, int Y, int W, int H are four
    // interchangeable ints. This pins the ORDER, so an insertion fails a test instead of shipping.
    [Fact]
    public void The_positional_order_is_append_only()
    {
        var ctor = typeof(CaptureResult).GetConstructors().Single();
        var names = ctor.GetParameters().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "Png", "X", "Y", "W", "H", "ScaleApplied", "Redactions",
            "CaptureMethod", "CaptureWarnings",
        }, names);
    }

    [Fact]
    public void CaptureWarnings_is_never_null_when_constructed_empty()
    {
        var r = new CaptureResult(System.Array.Empty<byte>(), 0, 0, 1, 1, 1.0, 0,
                                  "printWindow", System.Array.Empty<CaptureWarning>());
        Assert.NotNull(r.CaptureWarnings);
        Assert.Empty(r.CaptureWarnings);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureResultShapeTests"`
Expected: FAIL — the constructor has 7 parameters, not 9.

- [ ] **Step 3: Change the record**

In `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs`, replace line 9 with:

```csharp
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
```

- [ ] **Step 4: Fix the one construction site so the project compiles**

`Encode` is rewritten fully in Task 9. For now, make line 69 compile by passing the scrape's values:

```csharp
            return new CaptureResult(ms.ToArray(), captureBounds.X, captureBounds.Y, captureBounds.Width,
                                     captureBounds.Height, scale, painted,
                                     "screenScrape", System.Array.Empty<CaptureWarning>());
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureResultShapeTests"`
Expected: PASS — 2 passed.

- [ ] **Step 6: Run the whole headless suite — this record has many readers**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed. Build must be 0 warnings / 0 errors.

- [ ] **Step 7: Prove the gate is non-vacuous with a logic mutant**

Temporarily move `CaptureMethod` to sit before `Redactions` in the record. Re-run `CaptureResultShapeTests`. Expected: `The_positional_order_is_append_only` FAILS. **Revert.**

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs test/FlaUI.Mcp.Tests/Perception/CaptureResultShapeTests.cs
git commit -m "feat(capture): CaptureResult carries captureMethod and captureWarnings (appended)"
```

### Task 7: `CaptureOutcome` — the three-case seam return

The seam must be able to say "no image; the window resized" and "no image; the call timed out". Neither an exception nor a null/sentinel `CaptureResult` can express that: exceptions are wrong because this is ordinary expected control flow that collides with the surrounding `ToolException` conversions, and a sentinel cannot be told apart from any other empty outcome.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/CaptureOutcome.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureOutcomeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureOutcomeTests
{
    [Fact]
    public void A_completed_outcome_carries_the_result()
    {
        var r = new CaptureResult(System.Array.Empty<byte>(), 1, 2, 3, 4, 1.0, 0,
                                  "printWindow", System.Array.Empty<CaptureWarning>());
        var o = CaptureOutcome.Completed(r);
        Assert.Equal(CaptureOutcomeKind.Completed, o.Kind);
        Assert.Same(r, o.Result);
    }

    // The signals carry NO payload, deliberately. An earlier design had "resized" carry the observed W2
    // "for the next attempt" -- but retrying means a fresh UIA walk, which discovers the new geometry
    // itself and has no input for a rectangle the previous attempt measured. A contract requiring data
    // its only consumer discards is a contract that will drift.
    [Fact]
    public void The_resized_signal_carries_no_payload()
    {
        Assert.Equal(CaptureOutcomeKind.Resized, CaptureOutcome.Resized.Kind);
        Assert.Null(CaptureOutcome.Resized.Result);
    }

    [Fact]
    public void The_timed_out_signal_exists_and_carries_no_payload()
    {
        Assert.Equal(CaptureOutcomeKind.TimedOut, CaptureOutcome.TimedOut.Kind);
        Assert.Null(CaptureOutcome.TimedOut.Result);
    }

    // Without a TimedOut case the risk-2 scrape fallback is UNREACHABLE: the caller owns the fallback and
    // can only act on what the seam tells it.
    //
    // TargetTransient exists for the same structural reason: a degenerate W2 is RETRYABLE, and without a
    // case for it the seam could only throw -- making the identical condition terminal at W2 while it is
    // recoverable at W1.
    [Fact]
    public void There_are_exactly_four_cases()
        => Assert.Equal(4, System.Enum.GetValues<CaptureOutcomeKind>().Length);

    // Carries no RESULT, but does carry the reason its terminal message needs -- a payload with exactly
    // one consumer, unlike the W2 rectangle the Resized signal was correctly denied.
    [Fact]
    public void The_transient_signal_carries_a_reason_but_no_result()
    {
        var t = CaptureOutcome.Transient("the window reported no renderable area");
        Assert.Equal(CaptureOutcomeKind.TargetTransient, t.Kind);
        Assert.Null(t.Result);
        Assert.Equal("the window reported no renderable area", t.TransientReason);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureOutcomeTests"`
Expected: FAIL — `CaptureOutcome` does not exist (CS0103).

- [ ] **Step 3: Write the implementation**

Create `src/FlaUI.Mcp.Core/Perception/CaptureOutcome.cs`:

```csharp
namespace FlaUI.Mcp.Core.Perception;

public enum CaptureOutcomeKind
{
    /// <summary>An image was produced. Result is non-null.</summary>
    Completed,
    /// <summary>W1.Size != W2.Size. No image. The CALLER decides what happens next, and the answer
    /// differs by scope.</summary>
    Resized,
    /// <summary>PrintWindow did not return within the bound. No image. The caller falls back to the
    /// scrape with scrapeFallbackTargetUnresponsive.</summary>
    TimedOut,
    /// <summary>W2 had zero or negative extents — the window has no renderable area RIGHT NOW. No image.
    /// RETRYABLE, and that is the whole reason this case exists separately from a refusal.
    ///
    /// ⚠ It exists because the design already treats the IDENTICAL condition at W1 as a retryable
    /// transient: "a window caught mid-open or mid-animation can report a degenerate rect for a frame —
    /// exactly the transient the retry loop exists to absorb". Throwing here while returning a soft
    /// signal there would make the same physical condition terminal or recoverable purely according to
    /// which of two reads a few milliseconds apart happened to see it, and would defeat the retry loop
    /// for precisely the animating window it was built for.
    /// *(AGY-AFTER panel over this plan, round 3, Axiom Breaker.)*
    ///
    /// Surfaces to the AGENT as ElementNotActionable once retries exhaust — the same code the W1 case
    /// surfaces as. The agent-facing code is not the internal signal.</summary>
    TargetTransient,
}

/// <summary>What CaptureWindow returns. It WRAPS a CaptureResult rather than being one, because the seam
/// has three outcomes and a CaptureResult can express only the first.
///
/// ⚠ Not an exception: this is ordinary, expected control flow on a path the design does not refuse, and
/// throwing here would collide with the ToolException conversions surrounding this code.
/// ⚠ Not a null or sentinel CaptureResult: the caller must distinguish "resized" from "timed out" from
/// any other empty outcome, and a sentinel collapses them.
/// ⚠ The two signals carry NO payload. Retrying means a fresh UIA walk, which discovers the new geometry
/// itself; nothing consumes a rectangle the failed attempt observed.</summary>
public sealed record CaptureOutcome(CaptureOutcomeKind Kind, CaptureResult? Result,
                                    string? TransientReason = null)
{
    public static CaptureOutcome Completed(CaptureResult result) => new(CaptureOutcomeKind.Completed, result);
    public static readonly CaptureOutcome Resized = new(CaptureOutcomeKind.Resized, null);
    public static readonly CaptureOutcome TimedOut = new(CaptureOutcomeKind.TimedOut, null);

    /// <summary>A retryable transient, carrying the sentence the caller uses IF the budget runs out.
    ///
    /// ⚠ This payload does NOT violate the no-payload rule the Resized signal obeys. That rule killed a
    /// payload NOBODY CONSUMED -- "a contract requiring data its only consumer discards". This one has
    /// exactly one consumer and is consumed on every terminal path: without it the coordinator would
    /// report "no renderable area" for an empty element crop, which is a false diagnosis.</summary>
    public static CaptureOutcome Transient(string reason)
        => new(CaptureOutcomeKind.TargetTransient, null, reason);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureOutcomeTests"`
Expected: PASS — 5 passed.

- [ ] **Step 5: Prove the gate is non-vacuous with a logic mutant**

Temporarily delete the `TimedOut` enum member and its factory. Re-run. Expected: `There_are_exactly_three_cases` FAILS (and the file no longer compiles, which is the structural half — the count assertion is the behavioural half). **Revert.**

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/CaptureOutcome.cs test/FlaUI.Mcp.Tests/Perception/CaptureOutcomeTests.cs
git commit -m "feat(capture): CaptureOutcome - completed, resized, timed out"
```

---
