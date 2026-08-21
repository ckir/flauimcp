> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

## Phase 6 — Wiring, the wire contract, the audit signal, and the gates

### Task 20: Wire `ScreenshotTools` to the coordinator, and pin the metadata projection

**Failure mode 3 of the three the spec names as having no test that would go red:** nothing asserts the metadata projection's shape, so a field silently dropped from the anonymous object reaches an agent as an absent field it has no way to notice. The precedent to follow is `test/FlaUI.Mcp.Tests/Perception/ListWindowsProjectionShapeTests.cs`.

⛔ **RUN TASK 21 BEFORE THIS TASK. Not "Task 21's Steps 4-5" — the whole of Task 21.**

This task needs `CaptureAuditSignal` in **two** places: Step 4's capture call and Step 7's constructor.
MEASURED: the type does not exist until Task 21 creates it, and the dependency is strictly ONE-WAY —
Task 21 references neither `WindowCaptureCoordinator` nor `ScreenshotTools` (0 occurrences of each), so
nothing in it needs this task first. Running 21 → 20 is the only ordering that compiles at every step.

⚠⚠ **AND DO NOT TAKE THE "STUB THE FIELD AND COME BACK" OPTION Step 7 used to offer.** A stubbed signal
compiles and silently signals nothing, so Step 4's call site looks wired while the audit path is dead —
and every test still passes. That is the same trap the circuit breaker presented at Task 17, where the
whole class was moved rather than stubbed for exactly this reason. Step 7's own next sentence already
says it: *"a signal wired in later is a signal nobody notices is missing."*

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs` — the window/element branch, the metadata
  projection, the constructor, **and the tool description's field list (Step 5b)**. ⚠ The Files list
  used to cite `:30-84`; the file has grown since. MEASURED now: fields/constructor at **14-15**, the
  `return ToolResponse.Image(` at **84** running to **97**. Find each by name, not by line.
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (DI)
- Test: `test/FlaUI.Mcp.Tests/Perception/ScreenshotProjectionShapeTests.cs`

- [ ] **Step 1: Read the precedent first**

Run: `cat test/FlaUI.Mcp.Tests/Perception/ListWindowsProjectionShapeTests.cs`
Follow its structure. Do not invent a different one.

- [ ] **Step 2: Write the failing projection-shape test**

```csharp
using System.Linq;
using System.Text.Json;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class ScreenshotProjectionShapeTests
{
    // A projection-shape tripwire. A field silently dropped from the anonymous object reaches an agent as
    // an ABSENT field it has no way to notice -- and the tool description promises these by name, so a
    // mismatch between the two is a broken contract rather than a missing nicety.
    [Fact]
    public void The_metadata_object_carries_exactly_the_documented_fields()
    {
        var metadata = new
        {
            bounds = new { x = 1, y = 2, w = 3, h = 4 },
            dpiScale = 1.0,
            scaleApplied = 1.0,
            redactions = 0,
            maskEscalations = 0,
            escalated = System.Array.Empty<object>(),
            unmaskedProcesses = System.Array.Empty<string>(),
            captureMethod = "printWindow",
            captureWarnings = System.Array.Empty<object>(),
        };

        var json = JsonSerializer.Serialize(metadata);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(new[]
        {
            "bounds", "dpiScale", "scaleApplied", "redactions", "maskEscalations", "escalated",
            "unmaskedProcesses", "captureMethod", "captureWarnings",
        }, names);
    }

    // captureWarnings entries are {code, recourse} OBJECTS, not bare strings. The analogy to
    // unmaskedProcesses holds only on the axis where the entries are things a consumer can branch on.
    [Fact]
    public void A_warning_entry_serializes_as_code_plus_recourse()
    {
        var w = FlaUI.Mcp.Core.Perception.CaptureWarnings.For(
            FlaUI.Mcp.Core.Perception.CaptureWarnings.UniformCanvas);
        var json = JsonSerializer.Serialize(new { code = w.Code, recourse = w.Recourse });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "code", "recourse" },
                     doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("uniformCanvas", doc.RootElement.GetProperty("code").GetString());
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ScreenshotProjectionShapeTests"`

Expected: both tests PASS immediately. **They pin literals, so they are green from the start and are NOT the gate** — they exist only so the expected shape is written down before production is touched.

⚠ **The real tripwire is `The_tool_description_enumerates_exactly_the_fields_the_projection_emits`, which Step 6 adds.** It is the test the Step 9 mutants must be able to turn red. It is written in Step 6 rather than here because it asserts against the production file, which Steps 4 and 5 have not yet changed — writing it now would leave it red for two steps for the wrong reason.

- [ ] **Step 4: Rewrite the window/element branch to use the coordinator**

Replace `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs` lines 30–59 with:

```csharp
            CaptureResult result;
            IReadOnlyList<MaskEscalationEntry> escalations;
            // AB-9. Only the full-desktop path can SKIP a window and carry on; a window- or element-scoped
            // capture names its target, so a failure to resolve it throws rather than degrading. Empty on
            // that branch is therefore the truth, not a placeholder.
            IReadOnlyList<string> unmaskedProcesses;
            if (string.IsNullOrEmpty(window))
            {
                var present = await _perception.DenylistedWindowsVisibleAsync();
                if (present)
                    throw new ToolException(ToolErrorCode.TargetDenied, "A credential/denylisted window is currently visible; full-desktop capture is refused.", "capture a specific non-sensitive window: desktop_screenshot window=<handle>");
                var vbounds = ScreenCapture.VirtualScreenBounds();
                // DEF-2: this passed Array.Empty<Rectangle>() — full-desktop capture masked NOTHING, even
                // though window- and element-scoped capture both masked correctly. The refusal above only
                // covers DENYLISTED windows; an ordinary window holding a password field was photographed
                // in the clear.
                var desk = await _perception.AllMaskRectsAsync();
                escalations = desk.Escalations;
                unmaskedProcesses = desk.UnmaskedProcesses;
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(
                    vbounds, desk.Rects, maxWidth, CaptureScope.FullDesktop,
                    System.Array.Empty<CaptureWarning>()));
            }
            else
            {
                // The coordinator owns the walk, the retry loop, the bookend validation walk and the
                // scrape fallbacks (canonical steps 1-9). The geometry-time refusals it raises are the
                // SAME ones this method used to raise inline at :54 and :55.
                var scope = string.IsNullOrEmpty(@ref) ? CaptureScope.Window : CaptureScope.Element;
                var outcome = await _coordinator.CaptureAsync(new WindowHandle(window!), @ref, scope, maxWidth);
                result = outcome.Result;
                // ⚠ From the OUTCOME, not the geometry. On a fallback scrape the masks come from the
                // DESKTOP walk, so the target walk's escalations would describe a different mask set from
                // the one painted. *(Driver's solo pass, round 7.)*
                escalations = outcome.Escalations;
                // ⚠ NOT hardcoded empty. On the PrintWindow path this IS empty and that is the truth --
                // the image contains one window and its own mask set covered it. On a FALLBACK SCRAPE the
                // image contains whatever overlapped the target, and the coordinator's desktop mask walk
                // is what fills this in. Hardcoding empty there told the agent "everything needing masking
                // was masked" while background windows sat unmasked in the pixels.
                // *(AGY-AFTER panel over this plan, round 6, Guard-Consistency Auditor.)*
                unmaskedProcesses = outcome.UnmaskedProcesses;

                // §2.6, canonical step 10. AFTER the result is final, never before: a signal raised
                // earlier can appear in the captured pixels on the scrape path.
                _auditSignal.SignalIfOcclusionBypassed(new WindowHandle(window!),
                                                       outcome.Geometry.NativeWindowHandle,
                                                       result.CaptureMethod);
            }
```

- [ ] **Step 5: Extend the metadata projection**

Replace the `return ToolResponse.Image(...)` object with the block below. ⚠ **The plan said "lines
71-84"; MEASURED, the return starts at line 84 and runs to 97** — Tasks 10, 12 and 13 grew the file.
Find it with `grep -n "return ToolResponse.Image" src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs`.

```csharp
            return ToolResponse.Image(result.Png, new
            {
                bounds = new { x = result.X, y = result.Y, w = result.W, h = result.H },
                dpiScale = dpi,
                scaleApplied = result.ScaleApplied,
                redactions = result.Redactions,
                maskEscalations = escalations.Count,
                escalated = escalations.Select(e => new { automationId = e.AutomationId, controlType = e.ControlType }),
                // AB-9: ALWAYS present, empty when nothing was skipped — same reasoning as the two fields
                // above. A NON-EMPTY list means this image is NOT fully redacted: those processes' windows
                // are in the pixels and their redactions are not. Full-desktop only; a named window that
                // cannot be resolved throws instead of degrading.
                unmaskedProcesses,
                // Which backend produced these pixels. The two scopes now use different mechanisms and a
                // caller comparing images needs to know which it holds. camelCase, matching every other
                // field here.
                captureMethod = result.CaptureMethod,
                // ALWAYS present, EMPTY in the normal case — the unmaskedProcesses idiom, for the same
                // AB-9 reason: a diagnostic that appears only on failure teaches consumers to ignore its
                // absence. Entries are {code, recourse} objects: `code` is what you branch on, `recourse`
                // is what to do instead.
                captureWarnings = result.CaptureWarnings.Select(w => new { code = w.Code, recourse = w.Recourse }),
            });
```

- [ ] **Step 5b: Add the two new fields to the tool description — WITHOUT THIS, STEP 8 CANNOT GO GREEN**

⛔ **THIS STEP DID NOT EXIST, AND ITS ABSENCE BLOCKS THIS TASK'S OWN GATE.** Step 5 above adds
`captureMethod` and `captureWarnings` to the projection, and Step 6's test asserts the tool description
enumerates **exactly** the fields the projection emits — nine of them. MEASURED: the live description
lists **seven**:

```
JSON metadata {bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses}
```

So `Assert.Equal(expected, documented)` fails, and no step in this task fixed it. **Task 22 owns the full
description rewrite** — it carries the complete nine-field text — but Task 22 runs two tasks later, and
Step 8 demands a green suite now.

In the `[Description(...)]` attribute on `DesktopScreenshot`, change that enumeration to:

```
JSON metadata {bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses,captureMethod,captureWarnings}
```

Change **nothing else** in the description here. Task 22 replaces the whole string later, including this
list, so a minimal edit now is overwritten cleanly rather than conflicting.

- [ ] **Step 6: Add the two-directional tripwire — the third test, and the real gate**

⚠ **Step 2's two tests pin literals and are green from the start; this is the test Step 3 expects to be RED.** Add it to the same file now. *(An earlier draft of this plan claimed it was "already written in Step 2" and it was not — so a literal implementer following Step 2 then hit a Step 3 expectation referring to a test that did not exist. AGY-AFTER round 2, Literal Implementer.)*

Add these two members to `ScreenshotProjectionShapeTests`:

```csharp
    // ⚠ Add these usings to the file: System.IO, System.Text.RegularExpressions.
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // The tool description at ScreenshotTools.cs:17 promises these field names. A field in the payload
    // but missing from that list is one the model has no reason to read; a field in the list but missing
    // from the payload is a broken promise. This asserts the two agree — BOTH directions, because a
    // sweep that reads only one side of a promise proves nothing. (Item 12 shipped exactly that defect:
    // BuildPropertySweepTests passed against a COMMENTED-OUT property.)
    [Fact]
    public void The_tool_description_enumerates_exactly_the_fields_the_projection_emits()
    {
        var root = RepoRoot();
        // ONE read. An earlier version bound the identical path to two variables (`projection` and
        // `src`), which reads as though two different files are being compared and is the kind of
        // detail a reviewer trusts rather than checks. The description and the projection are both in
        // this one file; that is the whole reason a single-file sweep can check both directions.
        var src = File.ReadAllText(Path.Combine(root, "src", "FlaUI.Mcp.Server", "Tools", "ScreenshotTools.cs"));
        var m = Regex.Match(src, @"JSON metadata \{([^}]+)\}");
        Assert.True(m.Success, "the tool description no longer contains a {field,field,...} enumeration");
        var documented = m.Groups[1].Value.Split(',').Select(s => s.Trim()).ToArray();
        var expected = new[]
        {
            "bounds", "dpiScale", "scaleApplied", "redactions", "maskEscalations", "escalated",
            "unmaskedProcesses", "captureMethod", "captureWarnings",
        };
        Assert.Equal(expected, documented);

        // THE OTHER DIRECTION. Without this the test passes while the projection emits nothing at all.
        //
        // ⚠⚠ COMMENTS ARE STRIPPED FIRST, AND THAT IS LOAD-BEARING. Matching the raw source means
        // `// captureMethod = result.CaptureMethod,` still satisfies the regex, so the tripwire is
        // defeated by typing two slashes -- which is EXACTLY the defect item 12 shipped, reappearing
        // inside the very test written to prevent it. MEASURED: `\bcaptureMethod\s*=` matches the
        // commented line. Do not "simplify" this back.
        var code = StripComments(src);
        foreach (var field in expected)
            Assert.True(Regex.IsMatch(code, $@"\b{Regex.Escape(field)}\s*="),
                $"the tool description promises '{field}' but the metadata projection never assigns it");
    }

    /// <summary>Remove block and line comments so a commented-out assignment cannot satisfy a sweep.</summary>
    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join("\n", noBlocks
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }
```

- [ ] **Step 7: Wire DI in `Program.cs`**

Add near the existing `ScreenshotTools` registration:

```csharp
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.IWindowImageSource,
                              FlaUI.Mcp.Server.Capture.PrintWindowImageSource>();
builder.Services.AddSingleton(_ => FlaUI.Mcp.Core.Perception.CaptureCircuitBreaker.Default);
builder.Services.AddSingleton(sp =>
{
    var perception = sp.GetRequiredService<PerceptionManager>();
    return new FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator(
        (h, r) => perception.ResolveWindowCaptureGeometryAsync(h, r),
        sp.GetRequiredService<FlaUI.Mcp.Core.Perception.IWindowImageSource>(),
        FlaUI.Mcp.Core.Perception.CaptureRetryOptions.Default,
        breaker: sp.GetRequiredService<FlaUI.Mcp.Core.Perception.CaptureCircuitBreaker>(),
        // ⚠⚠ BOTH OF THESE ARE LOAD-BEARING AND BOTH ARE OPTIONAL PARAMETERS. Omitting the first is
        // what made round 5's denylist fix INERT in production while every test still passed; the seam
        // now throws rather than silently skipping the guard, but the real fix is passing it here.
        denylistedVisible: () => perception.DenylistedWindowsVisibleAsync(),
        desktopMasks: () => perception.AllMaskRectsAsync());
});
```

And replace `ScreenshotTools`' fields and constructor (`ScreenshotTools.cs:14-15`) with:

```csharp
    private readonly PerceptionManager _perception;
    private readonly FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator _coordinator;
    private readonly FlaUI.Mcp.Server.Capture.CaptureAuditSignal _auditSignal;

    public ScreenshotTools(PerceptionManager perception,
                           FlaUI.Mcp.Core.Perception.WindowCaptureCoordinator coordinator,
                           FlaUI.Mcp.Server.Capture.CaptureAuditSignal auditSignal)
    {
        _perception = perception;
        _coordinator = coordinator;
        _auditSignal = auditSignal;
    }
```

⚠ **`_perception` STAYS.** The full-desktop branch still calls `DenylistedWindowsVisibleAsync` and `AllMaskRectsAsync` on it directly — only the window/element branch moves to the coordinator.

⚠ **`CaptureAuditSignal` is created in Task 21.** If you are executing Task 20 first, that type does not exist yet: do Task 21's Steps 4–5 before this step, or stub the field and come back. Do NOT drop the parameter and "add it later" — a signal wired in later is a signal nobody notices is missing.

- [ ] **Step 8: Run the headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed, build 0/0, **total 1045** — 1037 after Task 19, plus Task 21's five and this
task's three. State the total verbatim: a bare "0 failed" cannot tell a suite that grew from one that
silently lost tests.

- [ ] **Step 9: Prove the gate is non-vacuous with a logic mutant**

1. Delete `captureMethod = result.CaptureMethod,` from the projection.
   Expected: `The_tool_description_enumerates_exactly_the_fields_the_projection_emits` FAILS on the second half — *"the tool description promises 'captureMethod' but the metadata projection never assigns it"*.
2. **Comment it out** rather than deleting it: `// captureMethod = result.CaptureMethod,`.
   Expected: it FAILS the same way. **This mutant is the important one.** Item 12 shipped a sweep that passed against a commented-out property — the anti-gaming half of that feature was defeatable by typing `<!--`. If this mutant does NOT go red, the regex is matching the comment and the tripwire is worthless.

**Revert both.**

- [ ] **Step 10: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Perception/ScreenshotProjectionShapeTests.cs
git commit -m "feat(capture): wire the coordinator into desktop_screenshot; pin the metadata projection"
```

### Task 21: The occlusion audit signal (§2.6)

**Off by default.** The trigger deliberately over-signals — a non-foreground window can be perfectly visible side-by-side — and agents screenshot non-foreground windows constantly, so a signal on by default would be continuous noise. A notification everyone learns to ignore is worse than none, which is the same AB-9 reasoning §3 turns on.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Capture/CaptureAuditSignal.cs`
- Modify: `src/FlaUI.Mcp.Server/ServerOptions.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs`
- Test: `test/FlaUI.Mcp.Tests/Capture/CaptureAuditSignalTests.cs`

- [ ] **Step 1: Read the existing seam first**

Run: `cat src/FlaUI.Mcp.Core/Attention/IAttentionSignal.cs` and `sed -n '140,160p' src/FlaUI.Mcp.Server/Program.cs`.

The contract this depends on is in that file's doc comment: *"Signal is best-effort and MUST NEVER throw — a failed signal must not turn a tool result into an error."* `CompositeAttentionSignal` already swallows a faulting child, and `TtsDebounce(capacity: 3, window: 30s)` is already registered.

- [ ] **Step 2: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

public class CaptureAuditSignalTests
{
    private sealed class Recorder : IAttentionSignal
    {
        public List<WindowHandle> Signalled { get; } = new();
        public bool Enabled => true;
        public void Signal(WindowHandle target) => Signalled.Add(target);
    }

    private static CaptureAuditSignal Make(Recorder r, bool enabled, bool targetIsForeground)
        => new(r, enabled, foregroundProbe: () => targetIsForeground ? new IntPtr(0x1234) : new IntPtr(0x9999));

    [Fact]
    public void It_fires_for_a_non_foreground_printWindow_capture_when_enabled()
    {
        var r = new Recorder();
        Make(r, enabled: true, targetIsForeground: false)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
        Assert.Single(r.Signalled);
    }

    // OFF BY DEFAULT. The trigger over-signals, so an operator opts in.
    [Fact]
    public void It_does_not_fire_when_the_flag_is_off()
    {
        var r = new Recorder();
        Make(r, enabled: false, targetIsForeground: false)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
        Assert.Empty(r.Signalled);
    }

    // A foreground window is by definition visible to the person at the console -- nothing was bypassed.
    [Fact]
    public void It_does_not_fire_for_the_foreground_window()
    {
        var r = new Recorder();
        Make(r, enabled: true, targetIsForeground: true)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
        Assert.Empty(r.Signalled);
    }

    // A scrape read only what was already on the screen. There is no widening to audit.
    [Fact]
    public void It_does_not_fire_on_the_scrape_path()
    {
        var r = new Recorder();
        Make(r, enabled: true, targetIsForeground: false)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "screenScrape");
        Assert.Empty(r.Signalled);
    }

    // Best-effort: a faulting channel must never turn a successful capture into an error.
    [Fact]
    public void A_throwing_channel_does_not_propagate()
    {
        var throwing = new ThrowingSignal();
        var s = new CaptureAuditSignal(throwing, true, () => new IntPtr(0x9999));
        s.SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
    }

    private sealed class ThrowingSignal : IAttentionSignal
    {
        public bool Enabled => true;
        public void Signal(WindowHandle target) => throw new InvalidOperationException("boom");
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureAuditSignalTests"`
Expected: FAIL — `CaptureAuditSignal` does not exist.

- [ ] **Step 4: Write the implementation**

Create `src/FlaUI.Mcp.Server/Capture/CaptureAuditSignal.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Capture;

/// <summary>§2.6. Tells the person at the console that a capture read a window they cannot see.
///
/// WHY IT EXISTS: the process denylist protects named APPLICATIONS. What item 8 actually widens is the
/// operator's spatial assumption that what is not on the screen is not being read, and a denylist has
/// nothing to say about that. `captureMethod` records every occlusion-bypassing capture -- but it records
/// it to the AGENT, so it audits nothing for the human.
///
/// ⚠ THE TRIGGER OVER-SIGNALS, DELIBERATELY. A non-foreground window can be perfectly visible
/// side-by-side, so this fires on captures that revealed nothing hidden. It never MISSES a covered
/// window, which is the direction that matters. Computing true occlusion needs a hit-test this server
/// does not do, and it was rejected as disproportionate.
///
/// ⚠ WHICH IS WHY IT IS OFF BY DEFAULT. Agents screenshot non-foreground windows constantly -- that is
/// the tool's normal use -- so a signal on by default would be continuous noise, and a notification
/// everyone learns to ignore is worse than none (the same AB-9 reasoning §3 turns on).
///
/// ⚠ BEST-EFFORT, NEVER THROWS. IAttentionSignal's own contract requires it: a failed signal must not
/// turn a tool result into an error. CompositeAttentionSignal already swallows a faulting child; this
/// catches anyway, because the seam it is handed may not be a Composite.</summary>
public sealed class CaptureAuditSignal
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly IAttentionSignal _signal;
    private readonly bool _enabled;
    private readonly Func<IntPtr> _foregroundProbe;

    public CaptureAuditSignal(IAttentionSignal signal, bool enabled, Func<IntPtr>? foregroundProbe = null)
    { _signal = signal; _enabled = enabled; _foregroundProbe = foregroundProbe ?? GetForegroundWindow; }

    /// <summary>Canonical step 10. Call AFTER the result is final -- never before, because a signal
    /// raised earlier can appear in the captured pixels on the scrape path, and GdiActionOverlay in
    /// particular draws a real top-most window on the screen.</summary>
    public void SignalIfOcclusionBypassed(WindowHandle handle, IntPtr hwnd, string captureMethod)
    {
        if (!_enabled) return;
        // A scrape read only what was already on the screen. Nothing was bypassed, so nothing to audit.
        if (!string.Equals(captureMethod, "printWindow", StringComparison.Ordinal)) return;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (_foregroundProbe() == hwnd) return;   // visible to the operator by definition
            if (_signal.Enabled) _signal.Signal(handle);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        { _ = ex; /* best-effort: never throw from the signal path */ }
    }
}
```

- [ ] **Step 5: Add the flag**

In `src/FlaUI.Mcp.Server/ServerOptions.cs`, add a `CaptureAuditSignal` bool defaulting to `false`, parsed from `--capture-audit-signal`, following exactly how `Autosound` is declared and parsed in that file. Read it first — do not guess the parsing idiom.

In `Program.cs`:

```csharp
builder.Services.AddSingleton(sp => new FlaUI.Mcp.Server.Capture.CaptureAuditSignal(
    sp.GetRequiredService<FlaUI.Mcp.Core.Attention.IAttentionSignal>(),
    sp.GetRequiredService<ServerOptions>().CaptureAuditSignal));
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureAuditSignalTests"`
Expected: PASS — 5 passed.

- [ ] **Step 7: Prove the gates are non-vacuous with two logic mutants**

1. Delete the `if (!_enabled) return;` line.
   Expected: `It_does_not_fire_when_the_flag_is_off` FAILS.
2. Delete the `captureMethod` check.
   Expected: `It_does_not_fire_on_the_scrape_path` FAILS.

**Revert both.**

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Server/ test/FlaUI.Mcp.Tests/Capture/CaptureAuditSignalTests.cs
git commit -m "feat(capture): the occlusion audit signal on the existing IAttentionSignal seam, off by default"
```

### Task 22: The documentation that goes stale

Three separate edits to the same tool-description string, plus the class doc. The spec records this obligation in two places because an engineer working through the contract changes reads §5, not the failure policy.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs` — the `[Description(...)]` string. It was at
  line 17; **Task 20 replaces the fields/constructor above it with a longer block, so it has moved.**
  Find it with `grep -n "JSON metadata" src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs`.
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` — the **`ScreenCapture` class** doc comment.
  ⛔ **The cited range `:11-14` IS WRONG AND DESTRUCTIVE — see Step 4.**

- [ ] **Step 1: Edit 1 — the metadata enumeration (VERIFY; Task 20 already did this)**

⚠ **Task 20's Step 5b already made this exact change**, because Task 20's own projection-shape test
demands the nine-field list and its gate could not go green without it. So this step is now a CHECK, not
an edit. Confirm with:

```bash
grep -o "JSON metadata {[^}]*}" src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs
```
Expected: the nine-field list ending `...,captureMethod,captureWarnings}`. If it still shows seven,
Task 20 was not completed — stop and finish Task 20 rather than patching it here.

**Step 6's mutant still applies unchanged**: reverting the enumeration to the old seven-field list must
turn Task 20's agreement test red, whichever task wrote the nine.

In the description string, replace:

```
{bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses}
```

with exactly:

```
{bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses,captureMethod,captureWarnings}
```

- [ ] **Step 2: Edit 2 — remove the stale focus instruction**

Delete `Focus the window first (no occlusion handling).` It stops being true, and a stale instruction in a tool description is read by every agent on every call.

- [ ] **Step 3: Edit 3 — add the new contract sentences**

Append to the description, before `Minimized→ElementNotActionable.`:

```
captureMethod says which mechanism produced THIS image, and it is NOT a proxy for scope: 'printWindow' means the window's OWN pixels were rendered, so occlusion is irrelevant; 'screenScrape' means the screen was photographed, so anything overlapping is in the image. Window/element scope normally returns 'printWindow' but FALLS BACK to 'screenScrape' when the target is unresponsive or will not hold still - check captureMethod on every response rather than assuming it from the scope you asked for. Full-desktop is always 'screenScrape'. IMPORTANT: when captureMethod is 'printWindow' the image coordinates are NOT clickable - the pixels may be behind another window or on no monitor at all, so a click computed from bounds lands on whatever is drawn there instead. Act through the UIA tree (desktop_snapshot + desktop_click ref) rather than mapping image coordinates back to the screen. captureWarnings is ALWAYS present and EMPTY when nothing is wrong; each entry is {code,recourse} where code is stable to branch on - uniformCanvas, elementCanvasUniform, desktopCanvasUniform, popupsNotRendered, scrapeFallbackTargetUnresponsive, scrapeFallbackTargetChanging. A window that keeps resizing is retried briefly (worst case ~4.5s) and then falls back to a scrape, whose redaction masks are re-measured at capture time so they match the pixels returned. NOTE a fallback costs MORE than the retry budget: re-measuring redactions walks every visible window, so a fallback on a busy desktop can take noticeably longer than a normal capture - budget for it if you are polling. It is REFUSED with RedactionUnmaskable only when the redacted regions are observed to MOVE during the capture itself, which no re-measurement can fix.
```

- [ ] **Step 3b: Edit 3b — `unmaskedProcesses` says "full-desktop only" and that is now FALSE**

⚠ The existing description reads *"unmaskedProcesses (full-desktop only) …"*. Since the fallback began re-measuring masks desktop-wide (panel round 6), **the window and element paths populate it too**, and `maskEscalations` / `escalated` alongside it now describe a desktop-wide walk rather than the region returned. A consumer reading "full-desktop only" concludes an empty list on a window capture means nothing was missed — and on a fallback that is exactly backwards.

Replace `unmaskedProcesses (full-desktop only) lists processes whose windows contributed NO masks - unbindable, usually elevated, or closed mid-capture; NON-EMPTY means the image is NOT fully redacted.` with:

```
unmaskedProcesses lists processes whose windows contributed NO masks - unbindable, usually elevated, or closed mid-capture; NON-EMPTY means the image is NOT fully redacted. IMPORTANT it is populated on full-desktop captures AND on any window/element capture that fell back to a scrape (captureMethod:'screenScrape' with a scrapeFallback* warning) - on that fallback the redaction masks are re-measured across the WHOLE desktop, so unmaskedProcesses, maskEscalations and escalated all describe that desktop-wide walk rather than only the region returned, and may name processes that contributed no pixels to this image. On a printWindow capture they describe the target window alone.
```

⚠ **This is disclosure, not filtering, and the difference is deliberate.** Filtering `escalated` to the captured region is not possible without adding a rectangle to `MaskEscalationEntry`, which today carries only `automationId` and `controlType`. Over-reporting is the fail-safe direction — it can only make a consumer more cautious — so the fix is to make the sentence true rather than to make the data narrower. *(AGY-AFTER panel over this plan, round 11, Contract Liar Hunter.)*

- [ ] **Step 3c: Edit 3c — THE FRESHNESS LIMIT. A CARRIED OBLIGATION FROM PHASE 0, and it was absent**

⛔ **THIS STEP EXISTED NOWHERE IN THE TASK.** MEASURED: Task 22 contained **zero** occurrences of
"fresh", "Chromium", "suspend", "last frame", "arbitrarily old" or "refresh". The obligation was recorded
only in the measurements doc's gate disposition, which this task never told anyone to open — the third
time on this branch that a Phase 0 obligation lived only in a sibling document. Executing Task 22 as
written would have shipped the feature with this limit undocumented.

**Why it exists.** Phase 0 gate answer 3 CHANGED during the panel: a fully occluded **Chromium-family**
window can **SUSPEND rendering**, after which `PrintWindow` returns the last frame it painted —
**150 successive calls did not wake it**. Electron did not suspend under the same treatment. This is a
**FRESHNESS** hazard, not a leak and not a masking failure: `dTree = 0` throughout, because the tree and
the pixels come from the same suspended renderer, so the masks and geometry stay correct. Only the
image's AGE is in question. The operator accepted it as a **documentation** item rather than a
mitigation, on the record, because no mitigation exists.

Append to the description, after the `captureMethod`/`captureWarnings` sentences added in Step 3:

```
FRESHNESS: a 'printWindow' capture returns the frame the target's renderer last painted. A window left fully occluded can SUSPEND rendering - observed with Chromium-family windows, not with Electron - and this call will then keep returning that last painted frame, which may be arbitrarily old; capturing again does NOT wake it. Masks and geometry remain correct, so this is about the image's AGE and nothing else. If freshness matters, bring the window to the foreground (desktop_focus_window) and capture again.
```

⚠ **Do not soften this into a promise.** The description must not imply the capture refreshes the
window, because MEASURED it cannot.

⚠ **This sentence is pinned by no test, and that is accepted.** Task 20's agreement test checks only the
`{field,...}` enumeration, not the prose. A prose assertion would pin wording rather than behaviour and
would break on every legitimate edit. The gate here is the panel and this note, not a test.

- [ ] **Step 4: Edit 4 — the class doc**

⛔ **THE CITED RANGE IS WRONG, AND FOLLOWING IT DOES TWO KINDS OF DAMAGE.** This step said *"Replace
`ScreenCapture.cs` lines 11-14"*. MEASURED: lines 9–17 are the **`CaptureResult` record's** doc comment,
and 11–14 sit inside it — they are its *"⚠ POSITIONAL RECORD, constructed positionally... APPEND ONLY,
NEVER INSERT: X/Y/W/H are four interchangeable ints, so a field added mid-list rebinds arguments silently
wherever the types line up. CaptureResultShapeTests pins the order for exactly that reason."* Overwriting
those lines **deletes a load-bearing warning about silent argument rebinding** and drops the class doc
into the middle of an unrelated record — while leaving the doc this step exists to fix untouched, still
claiming "no occlusion handling".

**The `ScreenCapture` class doc is at lines 21–24**, immediately above `public static class
ScreenCapture`. Find it by content, not by line: `grep -n "Screen-region capture" ScreenCapture.cs`. It
currently reads:

```csharp
/// <summary>Screen-region capture (no occlusion handling — callers focus-first; no UIA element reads).
/// Captures by absolute screen rectangle so it can run OFF the query STA (spec §8). Paints black
/// redaction rects (live bounds passed in), clamps width to a hard ceiling, PNG-encodes. Headless/
/// disconnected sessions are detected before capture so we never hand back a black frame.</summary>
```

Replace **those four lines, and only those**, with:

```csharp
/// <summary>Screen-region and per-window capture. Paints black redaction rects (live bounds passed in),
/// clamps width to a hard ceiling, PNG-encodes.
///
/// TWO BACKENDS. CaptureRectangle SCRAPES a screen rectangle — used by full-desktop, by the OCR path, and
/// as the fallback when PrintWindow cannot deliver. CaptureWindow renders a window's OWN content via
/// PrintWindow, so it is unaffected by occlusion; window and element scope use it.
///
/// ⚠ The old promise that "headless/disconnected sessions are detected before capture so we never hand
/// back a black frame" NO LONGER HOLDS in general. The session guard still runs (ScreenshotTools.cs:27-28
/// refuses a non-renderable desktop), but PrintWindow can return a blank render for reasons no guard
/// catches — MEASURED: its BOOL return is True on every call, including every all-black one. That is what
/// the uniformCanvas / elementCanvasUniform / desktopCanvasUniform warnings exist to report.
///
/// ⚠ Callers no longer focus-first, and must not: the whole point is capturing without touching focus.</summary>
```

- [ ] **Step 5: Run the headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed, build 0/0, **total 1045** — unchanged from Task 20, because this task adds no
tests. State the total verbatim. It must include Task 20's description/projection agreement test, which
is what makes Step 1's check non-optional.

- [ ] **Step 6: Prove the gate is non-vacuous with a logic mutant**

Revert Step 1's enumeration to the old seven-field list.
Expected: `The_tool_description_enumerates_exactly_the_fields_the_projection_emits` FAILS. **Revert the mutant.**

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs
git commit -m "docs(capture): tool description and class doc - five edits, incl. the Phase 0 freshness limit"
```

### Task 23: The GDI handle gate (risk 4)

**The acceptance criterion is checkable, not an instruction to be careful:** GDI handle count and user-object count for the process must return to their starting values after a run of repeated captures, including runs that hit **every path that exits AFTER the bitmap is allocated at canonical step 6b** — a null GDI handle during acquisition, a timeout, and an empty crop.

**The degenerate, minimized and window-scope-resize refusals are NOT in that list and cannot be.** They all exit at canonical steps 2, 5 or 6 — before step 6b allocates anything — so there is no handle for them to leak. Do not write handle-leak tests for guards that hold no handles.

**Files:**
- Test: `test/FlaUI.Mcp.Tests/Capture/GdiHandleLeakTests.cs`
- Modify: `docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md`

- [ ] **Step 1: Write the Desktop-category test**

```csharp
using System;
using System.Diagnostics;
using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

[Trait("Category", "Desktop")]
public class GdiHandleLeakTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public GdiHandleLeakTests(TestAppFixture app) => _app = app;

    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

    private static (uint Gdi, uint User) Counts()
    {
        var h = Process.GetCurrentProcess().Handle;
        return (GetGuiResources(h, GR_GDIOBJECTS), GetGuiResources(h, GR_USEROBJECTS));
    }

    [Fact]
    public async Task Repeated_successful_captures_leave_handle_counts_flat()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();
        // Warm up first: the first call allocates caches that are not a leak.
        for (int i = 0; i < 3; i++) src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000)?.Dispose();

        var before = Counts();
        for (int i = 0; i < 30; i++) src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000)?.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = Counts();

        Assert.True(after.Gdi <= before.Gdi + 2, $"GDI objects grew {before.Gdi} -> {after.Gdi}");
        Assert.True(after.User <= before.User + 2, $"USER objects grew {before.User} -> {after.User}");
    }

    // THE EMPTY-CROP PATH exits AFTER step 6b allocated the bitmap, so it is in scope for this gate.
    [Fact]
    public async Task An_empty_crop_refusal_leaves_handle_counts_flat()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();
        // An element rect far outside the window forces WindowCropGeometry to return null.
        var geo = new CaptureGeometry(new Rectangle(rect.X + 99999, rect.Y + 99999, 10, 10),
            Array.Empty<Rectangle>(), false, false, null, Array.Empty<MaskEscalationEntry>(),
            rect, hwnd, false);

        var before = Counts();
        for (int i = 0; i < 20; i++)
        {
            try
            {
                ScreenCapture.CaptureWindow(geo, 0, CaptureScope.Element,
                    Array.Empty<CaptureWarning>(), src, 5000);
            }
            catch (FlaUI.Mcp.Core.Errors.ToolException) { /* expected */ }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = Counts();

        Assert.True(after.Gdi <= before.Gdi + 2, $"GDI objects grew {before.Gdi} -> {after.Gdi}");
    }
}
```

Add `using System.Runtime.InteropServices;`.

- [ ] **Step 2: Run it**

On a physical console, quiet machine:
`dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~GdiHandleLeakTests"`
Expected: PASS — 2 passed.

- [ ] **Step 3: Record the measurement**

Append a `## Risk 4 — GDI handle counts` section to the measurements doc with the before/after numbers for both tests and an explicit list of which exit paths were covered and which were excluded, with the reason (they exit before allocation).

- [ ] **Step 4: Prove the gate is non-vacuous with a logic mutant**

In `PrintWindowImageSource.Render`, delete `if (hbm != IntPtr.Zero) DeleteObject(hbm);` from the `finally`.
Expected: `Repeated_successful_captures_leave_handle_counts_flat` FAILS with a growth of ~30. **Revert.**

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Capture/GdiHandleLeakTests.cs docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md
git commit -m "test(capture): GDI handle gate - flat across success and the post-allocation refusal"
```

### Task 24: The Desktop occlusion test — the feature's actual success criterion

**Files:**
- Test: `test/FlaUI.Mcp.Tests/Capture/OccludedCaptureTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using System;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

[Trait("Category", "Desktop")]
public class OccludedCaptureTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public OccludedCaptureTests(TestAppFixture app) => _app = app;

    // THE SUCCESS CRITERION OF THE WHOLE FEATURE: capture a window that is behind another window and get
    // THAT window's pixels, not the occluder's.
    [Fact]
    public async Task An_occluded_window_yields_its_own_pixels_not_the_occluders()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();

        // CONTROL: capture while it is on top.
        using var control = src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000);
        Assert.NotNull(control);

        // Cover it with a topmost opaque window over the SAME rect.
        using var occluder = OccluderWindow.Show(rect);
        await Task.Delay(400);   // let DWM compose the occluder

        // A SCRAPE of the same rect now photographs the occluder -- this asserts the defect exists, so
        // the PrintWindow assertion below is not passing for some unrelated reason.
        var scraped = ScreenCapture.CaptureRectangle(rect, Array.Empty<Rectangle>(), 0,
            CaptureScope.Window, Array.Empty<CaptureWarning>());
        Assert.True(scraped.Png.Length > 0);

        // THE ASSERTION. PrintWindow still returns the target's own content.
        using var occludedCapture = src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000);
        Assert.NotNull(occludedCapture);
        Assert.False(UniformCanvasDetector.IsUniform(occludedCapture!),
            "the occluded capture came back a single colour - PrintWindow did not render the target");
        Assert.True(SimilarEnough(control!, occludedCapture!),
            "the occluded capture does not match the control - it may be a photograph of the occluder");
    }

    // Compare a coarse grid rather than every pixel: a caret blink or a hover highlight must not fail it.
    private static bool SimilarEnough(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        int same = 0, total = 0;
        for (int y = 0; y < a.Height; y += Math.Max(1, a.Height / 32))
            for (int x = 0; x < a.Width; x += Math.Max(1, a.Width / 32))
            {
                total++;
                if (a.GetPixel(x, y).ToArgb() == b.GetPixel(x, y).ToArgb()) same++;
            }
        return total > 0 && (double)same / total > 0.90;
    }
}
```

- [ ] **Step 2: Write the occluder helper**

Create `test/FlaUI.Mcp.Tests/Capture/OccluderWindow.cs`: a `System.Windows.Forms.Form` on its own STA thread, `FormBorderStyle.None`, `TopMost = true`, `ShowInTaskbar = false`, `BackColor = Color.Magenta`, positioned and sized to the given rect, `IDisposable` closing it. **Model it on `GdiActionOverlay`'s STA-thread-plus-pump structure** (`src/FlaUI.Mcp.Server/Overlay/GdiActionOverlay.cs`) rather than inventing one — read that file first.

- [ ] **Step 3: Run it**

On a physical console. `SendInput` does not deliver over RDP and this test needs a real composited desktop:
`dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~OccludedCaptureTests"`
Expected: PASS — 1 passed.

- [ ] **Step 4: Add the RUNTIME metadata assertion**

Task 20's sweep reads the projection's **source text**. That catches a deleted or commented-out field, but it never observes the object the tool actually emits — a shape defect that only appears at runtime would slip through both. This is the one place in the plan with a live desktop, so the runtime half belongs here.

Append to `OccludedCaptureTests`:

```csharp
    // The RUNTIME half of the metadata contract. Task 20's sweep reads the projection's SOURCE; this
    // reads the JSON the tool actually produced. Both are needed: the sweep catches a field removed from
    // the code, this catches a shape that is wrong only once it is serialized.
    [Fact]
    public async Task The_emitted_metadata_carries_all_nine_documented_fields()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var tools = TestHost.ResolveScreenshotTools(mgr);   // see the note below
        var call = await tools.DesktopScreenshot(window: handle.Id);
        var json = CallResultJson(call);                     // the JSON block of the CallToolResult

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var field in new[]
        {
            "bounds", "dpiScale", "scaleApplied", "redactions", "maskEscalations", "escalated",
            "unmaskedProcesses", "captureMethod", "captureWarnings",
        })
            Assert.True(doc.RootElement.TryGetProperty(field, out _),
                $"the emitted metadata is missing '{field}'");

        Assert.Equal("printWindow", doc.RootElement.GetProperty("captureMethod").GetString());
        // ALWAYS PRESENT, and empty is the normal case -- an absence would read as "nothing to report"
        // only by accident.
        Assert.Equal(System.Text.Json.JsonValueKind.Array,
                     doc.RootElement.GetProperty("captureWarnings").ValueKind);
    }
```

⚠ **`TestHost.ResolveScreenshotTools` and `CallResultJson` are helpers this test needs and the repo may not have.** Before writing them, run `grep -rn "ScreenshotTools\|CallToolResult" test/FlaUI.Mcp.Tests/ | head -20` and follow whatever pattern already exists for constructing a tool class in a test. **If no such pattern exists, construct `ScreenshotTools` directly** with a real `PerceptionManager`, a `WindowCaptureCoordinator` over `PrintWindowImageSource`, and a `CaptureAuditSignal` bound to `NullAttentionSignal.Instance` — and extract the JSON from the `CallToolResult`'s text content block. **Do not invent a test host.**

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Capture/
git commit -m "test(capture): the occlusion success criterion, plus the runtime metadata contract"
```

### Task 25: Full gates, and complete the measurements record

- [ ] **Step 0: Pin the refusal surface against the spec's table**

⚠ **The spec's §4 table asserts it enumerates the WHOLE refusal surface, and it has now gone stale THREE times** — panel rounds 24 and 25 during the spec review, and again at plan round 8, when four refusals the plan's own panel had introduced were missing from it. Every time, a fold added a refusal and **nothing mechanically noticed**.

This is the gate that notices. Create `test/FlaUI.Mcp.Tests/Perception/RefusalSurfaceSweepTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RefusalSurfaceSweepTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // Every ToolErrorCode this feature's capture path can throw, with the reason it exists. Adding a
    // refusal WITHOUT adding it here fails this test, which is the only thing standing between the
    // spec's completeness claim and another silent drift.
    //
    // ⚠ This does NOT verify the spec prose - a test cannot read intent. What it pins is that the SET of
    // codes thrown by these files is the set someone deliberately wrote down. When it goes red, the fix
    // is to update BOTH this list and the spec's section 4 table, in the same commit.
    private static readonly HashSet<string> Documented = new()
    {
        "TargetDenied",           // denylisted process at geometry time; denylisted window on a fallback
        "ElementNotActionable",   // minimized, destroyed, degenerate-on-exhaustion, empty crop, OCR degenerate
        "RedactionUnmaskable",    // resize/bookend exhaustion with masks; the desktop walk refusing
        "CaptureUnavailable",     // null GDI handle, desktop not renderable, unwired denylist guard
    };

    [Fact]
    public void The_capture_path_throws_only_documented_error_codes()
    {
        var root = RepoRoot();
        var files = new[]
        {
            Path.Combine(root, "src", "FlaUI.Mcp.Core", "Perception", "ScreenCapture.cs"),
            Path.Combine(root, "src", "FlaUI.Mcp.Core", "Perception", "WindowCaptureCoordinator.cs"),
            Path.Combine(root, "src", "FlaUI.Mcp.Server", "Capture", "PrintWindowImageSource.cs"),
        };

        var thrown = new HashSet<string>();
        foreach (var f in files)
        {
            Assert.True(File.Exists(f), $"{f} does not exist - the sweep is pointing at the wrong path");
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"ToolErrorCode\.(\w+)"))
                thrown.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(thrown);
        var undocumented = thrown.Except(Documented).OrderBy(x => x).ToList();
        Assert.True(undocumented.Count == 0,
            "these error codes are thrown but not documented in the spec's section 4 refusal table: " +
            string.Join(", ", undocumented));
    }
}
```

- [ ] **Step 0b: Prove the sweep is non-vacuous with a logic mutant**

Temporarily add `throw new ToolException(ToolErrorCode.NotImplemented, "x", "y");` inside `WindowCaptureCoordinator`.
Expected: `The_capture_path_throws_only_documented_error_codes` FAILS naming `NotImplemented`. **Revert.**

⚠ **This sweep does NOT strip comments, and unlike the others that is the SAFE direction — but check it.**
It collects codes *thrown* and asserts they are all documented, so a `ToolErrorCode.X` appearing in a
**comment** in one of the three swept files produces a false ALARM, never a false pass. If it goes red
naming a code you know is only mentioned in a comment, the fix is to strip comments here too — not to
add the code to `Documented`.

- [ ] **Step 0c: The wire-code reachability gate — MOVED HERE FROM TASK 5**

⚠ **This test was written into Task 5 and cannot live there.** MEASURED during execution:
`grep -rn "CaptureWarnings\." src/` returned nothing at Phase 1, the FIRST emission site is created in
Phase 2 (`phase-2-crop-and-encode.md:299`) and the LAST in Phase 5 (`phase-5-coordinator.md:497`). In
Phase 1 the gate fails for all six codes, so Task 5 could never have committed green. **Here, every
emission site exists.** *(AGY-FIRST consult, 2026-08-21: option A, peer and driver ALIGNED. The
rejected alternative — keeping it in Task 5 but asserting only over codes that already have an emitter —
is a tautology: if a code goes dead later the asserted set simply shrinks and the test stays green.)*

Add to `test/FlaUI.Mcp.Tests/Perception/CaptureWarningTests.cs`, along with `using System.Linq;` at the
top of that file:

```csharp
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

    /// <summary>⚠ COMMENTS STRIPPED FIRST. Without it, a code named only in a comment — and this plan's
    /// comments name these codes constantly, to explain the paths that emit them — satisfies the gate
    /// while nothing emits it. That is the same defect item 12 shipped and the same one the metadata
    /// sweep had, arriving a third time in the test written to prevent a code going dead.
    /// MEASURED: the substring check matches a commented line.</summary>
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
```

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureWarningTests"`
Expected: **10 passed** — the 9 from Task 5 plus this one.

⚠ **KNOWN LIMIT, do not mistake this gate for more than it is.** It is a string search: it proves the
token exists in a `.cs` file, **not** that the emission site is reachable at runtime. If an upstream
guard starts returning early and the branch goes dead, this still passes. *(Raised by the AGY-FIRST
consult; recorded rather than fixed, because runtime reachability needs a functional test per code and
Task 24's Desktop test covers the paths that matter.)*

- [ ] **Step 0d: TWO mutants for the reachability gate, and the second matters more**

1. Temporarily add `"windowResized"` back to `AllCodes` without adding an emission site.
   Expected: `No_shipped_code_is_unreachable` FAILS naming it, and `Exactly_six_codes_ship` FAILS at 7.
   **That is the gate that would have caught this code going dead on its own, so see it go red once.**
2. Do the same, but ALSO add `// CaptureWarnings.WindowResized` as a COMMENT in any production file.
   Expected: **it still FAILS.** If it passes, `StripComments` is not working and the gate is defeated by
   typing two slashes — the third time that defect would have shipped in this repo, after item 12's
   property sweep and this plan's own metadata sweep.

**Revert both.**

- [ ] **Step 1: Build clean**

Run: `dotnet build FlaUI.Mcp.slnx`
Expected: **0 warnings, 0 errors.** Warnings are errors repo-wide, so anything less is a hard fail.

- [ ] **Step 2: Headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: all pass, **0 skipped**. Record the total.

- [ ] **Step 3: Desktop suite**

**Main thread, quiet machine, physical console.** Takes ~12 minutes against a 600s tool cap, so run it backgrounded. Do not co-run a subagent — that has produced four spurious failures and a 40%-longer run.

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category=Desktop&Category!=KnownDefect&Category!=Measurement&FullyQualifiedName!~PopupGrafting"`
Expected: all pass, 0 skipped.

- [ ] **Step 4: PopupGrafting**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PopupGrafting"`
Expected: 1 passed.

- [ ] **Step 4b: Measure the FALLBACK's end-to-end wall clock**

⚠ **The fallback path grew expensive across panel rounds 6-9 and nobody has timed it.** It now performs a
full-desktop redaction walk (`AllMaskRectsAsync`, a UIA descendant walk per visible window) plus two
denylist enumerations, on a request that has already spent its retry budget.

Time a window-scope fallback end to end on a **busy** desktop — a dozen or more visible windows, at least
one Chromium-family — by forcing the timeout path with the `HangProbe` fixture from Task 1. Record:

| | |
|---|---|
| retry budget | `MaxAttempts × TimeoutMs` = 3 × 1500 = 4500ms |
| `AllMaskRectsAsync` alone | measure |
| 2 × `DenylistedWindowsVisibleAsync` | measure |
| **fallback, end to end** | measure |

**There is no pass/fail threshold here and that is deliberate** — the walk cannot be bounded without
scraping a PARTIAL mask set, which is the leak it closes. What the measurement decides is whether the tool
description's wording is honest enough, and whether the operator wants the fallback gated behind an opt-in
on very busy desktops. Report the number; the disposition is the operator's.

- [ ] **Step 5: Complete the measurements record**

The measurements doc must now hold all five risks plus the two added by ratification item 5:

| § | Risk | Recorded? |
|---|---|---|
| Risk 2 | does `PrintWindow` block | Task 1 |
| Risk 3 | stale composition, **one-sided** | Task 2 |
| Risk 1 | Chromium AND Electron, separately | Task 2 |
| Risk 4 | GDI handles flat | Task 23 |
| Risk 5 | detector sampling | Task 11 |
| **added** | bookend cost + static-window behaviour | Task 18's tests + a timing note |
| **added** | the OCR path's yardstick regression | Task 10 + Task 12 |

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md
git commit -m "docs(item8): complete the measurement record - five risks plus the two ratification adds"
```

- [ ] **Step 7: Run AGY-CAPSTONE**

Per the repo's standing discipline, before declaring this complete: convene a convergent agy review of the **committed implementation** (executable code + tests, not the plan), rounds until green, with a do-not-re-raise ledger. **Point it hardest at §2.5 and §2.6** — they are the only parts of this design that never faced the panel.

Then AGY-TEST-AUDIT, then `finishing-a-development-branch`. **Merge `--no-ff`, KEEP the branch, push NOTHING** — this project's standing rules.

---
