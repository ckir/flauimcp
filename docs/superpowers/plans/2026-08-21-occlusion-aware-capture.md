# Occlusion-Aware Window Capture (`PrintWindow`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture a window's own pixels regardless of occlusion, without touching focus, with redaction masks still landing on the right regions and no wrong image ever returned unlabelled.

**Architecture:** Window and element scope stop scraping the screen and acquire through `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`. Full-desktop and OCR keep the scrape. A new `WindowCaptureCoordinator` owns the retry loop, the bookend mask-validation walk and the scrape fallbacks; a new `ScreenCapture.CaptureWindow` seam owns acquisition, the `W2`-side guards and the crop; acquisition sits behind `IWindowImageSource` so everything except the interop is headless-testable.

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.19041.0`), xUnit, `System.Drawing`, FlaUI, Win32 interop (`user32`/`gdi32`).

---

## READ THIS BEFORE TASK 1

**Source spec:** `docs/superpowers/specs/2026-08-21-occlusion-aware-capture-design.md` at commit `b11b87b`. Read §1, §2, §2.5, §2.6, §4 and §5 before starting. **Do not read the spec at `6ab0087`** — that is the pre-ratification version and three of its rules are superseded.

**Five things this plan carries that re-deriving from the panel record would undo:**

1. **§2.5 and §2.6 did not exist when the panel ran.** They were added by operator ratification on 2026-08-21 and have had **no adversarial review at all**. They are the newest and least-attacked ideas in the design. Treat every claim in them as unproven.
2. **Window scope RETRIES before refusing** (ratification item 2). The panel-era rule refused on the first size mismatch. Do not restore it.
3. **The hung-window leak is staged** (ratification item 3): measure first; the containments ship, the out-of-process worker does not.
4. **`CaptureRectangle` has THREE other callers, not one.** `ScreenshotTools.cs:49`, `FindTextTools.cs:62`, `FindTextTools.cs:110`. Risk 6 in the spec said "one" until this plan corrected it.
5. **Two measurements gate the design, not just its tuning.** Tasks 1 and 2 come first for that reason, and Task 3 is a hard stop.

**Repo rules that will fail the build if ignored:**

- **Warnings are errors repo-wide** (root `Directory.Build.props`, since `54b1dc5`). Every build must be 0 warnings / 0 errors.
- **Never pass `--no-build` to `dotnet test`.** A deleted test still runs from the stale DLL.
- The solution is **`FlaUI.Mcp.slnx`**. There is no `.sln`.
- **Every new gate needs a logic mutant** proving that *specific* test goes red. A structural break (deleting a property) proves only that the symbol was referenced.
- `CaptureResult` and `CaptureGeometry` are **positional records**: **append fields, never insert**.

**Branch:** `item8-occlusion-aware-capture`, already created from master `6ab0087`. Spec amendment is `b11b87b`. Nothing is pushed this release.

**Two properties this plan STATES rather than lets you discover.** Neither is a defect and neither has a task; they are here because finding them mid-implementation would look like one.

1. **The width ceiling bounds the PAYLOAD, never the ALLOCATION** (spec risk 7). `MaxCaptureWidth = 1920` (`ScreenCapture.cs:17`) is applied inside `Encode` (`:47-50`), *after* the source bitmap exists. Under the scrape that changed nothing, because the scrape only ever grabbed the rectangle asked for. Under `PrintWindow` the allocation is dictated by the **window's own size**, so a 4K-wide window is a ~33 MB managed bitmap plus its GDI twin, per capture, in a long-lived server. `maxWidth` does not reduce it. If you are looking at memory during Task 23 and see a large transient, this is why.

2. **DPI already lines up, and depends on the manifest.** The server is `PerMonitorV2` DPI-aware (`src/FlaUI.Mcp.Server/app.manifest:5`), so `GetWindowRect` returns physical pixels that match `geo.Bounds` and the `W1`/`W2` comparison is apples-to-apples. Measured: run DPI-aware the probe's sizes matched `desktop_list_windows` exactly; run DPI-*un*aware they were scaled to 80%. **Do not remove or weaken that manifest entry** — every rectangle in this design silently becomes wrong if it goes.

---

## File Structure

**New files — `src/FlaUI.Mcp.Core/Perception/`**

| File | Responsibility |
|---|---|
| `CaptureScope.cs` | The four-value scope enum both seams branch on |
| `CaptureWarning.cs` | The `{code, recourse}` pair, the seven code constants, and their recourse text |
| `CaptureOutcome.cs` | The three-case seam outcome: completed / resized / timed out |
| `WindowCropGeometry.cs` | The PURE crop function — rectangles in, rectangles out, no handles, no pixels |
| `IWindowImageSource.cs` | The injectable acquisition seam |
| `UniformCanvasDetector.cs` | The uniform-colour predicate and its sampling strategy |
| `WindowCaptureCoordinator.cs` | Steps 1–9: the walk, the retry loop, the bookend walk, the fallbacks |

**New files — `src/FlaUI.Mcp.Server/Capture/`**

| File | Responsibility |
|---|---|
| `PrintWindowImageSource.cs` | The real interop. The ONLY Desktop-category production file in this feature |

**Modified**

| File | Change |
|---|---|
| `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs:9` | `CaptureResult` gains two appended fields |
| `…/ScreenCapture.cs:36` | `CaptureRectangle` gains `scope` + `warningsSoFar` |
| `…/ScreenCapture.cs:45` | `Encode` gains `reported`, `method`, `warnings`; `captureBounds` renames to `absolute` |
| `…/ScreenCapture.cs:11-14` | Class doc loses the occlusion + never-black claims |
| `…/PerceptionManager.cs:852` | `ResolveWindowCaptureGeometryAsync` gains `clipToVirtualScreen` |
| `…/PerceptionManager.cs:915-935` | The degenerate-`W1` guard, between the bounds read and the yardstick |
| `…/PerceptionManager.cs:935` | The yardstick becomes conditional |
| `…/PerceptionManager.cs:1275` | `CaptureGeometry` gains three appended fields |
| `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:17` | Tool description: three separate edits |
| `…/ScreenshotTools.cs:49,58,71-84` | Call sites and the metadata projection |
| `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:62,110` | The third caller — explicit scope, explicit yardstick |
| `src/FlaUI.Mcp.Server/Program.cs` | DI for `IWindowImageSource`; the audit-signal flag |
| `src/FlaUI.Mcp.Server/ServerOptions.cs` | The audit-signal flag |
| `ROADMAP.md` | The out-of-process worker as tracked debt |

---

## Phase 0 — Measurements that gate the design

**Tasks 1–3 produce no production code.** Two of the three answers can change what Phases 1–6 build, so nothing else starts until Task 3 records them.

### Task 1: Measure whether `PrintWindow` blocks on a window that is not pumping messages

This is **risk 2**, and it decides whether ratification item 3's containments get built at all.

**Files:**
- Create: `.clavity/scratch/item8-plan/hang-probe/HangProbe.ps1`
- Create: `.clavity/scratch/item8-plan/hang-probe/HangWindow.ps1`
- Create: `docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md`

- [ ] **Step 1: Write the fixture that stops pumping**

A WinForms window that renders once, then blocks its UI thread inside a button handler. Save as `.clavity/scratch/item8-plan/hang-probe/HangWindow.ps1`:

```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object Windows.Forms.Form
$f.Text = 'HANGPROBE'; $f.Width = 640; $f.Height = 400
$f.BackColor = [Drawing.Color]::FromArgb(0, 120, 215)
$lbl = New-Object Windows.Forms.Label
$lbl.Text = 'PUMPING'; $lbl.AutoSize = $true; $lbl.Location = '20,20'
$lbl.Font = New-Object Drawing.Font('Segoe UI', 24)
$f.Controls.Add($lbl)
# Block the UI thread 8s after Shown, so the window has definitely rendered first.
$t = New-Object Windows.Forms.Timer
$t.Interval = 8000
$t.Add_Tick({ $t.Stop(); $lbl.Text = 'HUNG'; [Threading.Thread]::Sleep(120000) })
$f.Add_Shown({ $t.Start() })
[Windows.Forms.Application]::Run($f)
```

- [ ] **Step 2: Write the probe that times the call**

Save as `.clavity/scratch/item8-plan/hang-probe/HangProbe.ps1`:

```powershell
param([Parameter(Mandatory=$true)][string]$TitleMatch)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PW {
  [DllImport("user32.dll", SetLastError=true)]
  public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
[void][PW]::SetProcessDPIAware()

$p = Get-Process | Where-Object { $_.MainWindowTitle -like "*$TitleMatch*" } | Select-Object -First 1
if (-not $p) { throw "no window matching '$TitleMatch'" }
$h = $p.MainWindowHandle
$r = New-Object PW+RECT
[void][PW]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T
Write-Host "hwnd=$h rect=${w}x${ht} title='$($p.MainWindowTitle)'"

$bmp = New-Object Drawing.Bitmap $w, $ht
$g   = [Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$sw  = [Diagnostics.Stopwatch]::StartNew()
$ok  = [PW]::PrintWindow($h, $hdc, 2)
$sw.Stop()
$g.ReleaseHdc($hdc); $g.Dispose()
Write-Host "RESULT ret=$ok elapsedMs=$($sw.ElapsedMilliseconds)"
$bmp.Save("$PSScriptRoot\hang-$TitleMatch-$($sw.ElapsedMilliseconds)ms.png")
$bmp.Dispose()
```

- [ ] **Step 3: Run the control — the window while it is still pumping**

Launch the fixture, then within 8 seconds run:

```
powershell -ExecutionPolicy Bypass -File .clavity/scratch/item8-plan/hang-probe/HangProbe.ps1 -TitleMatch HANGPROBE
```

Expected: `RESULT ret=True elapsedMs=<small>`, and a PNG showing `PUMPING`. This is the baseline the hung case is compared against. If this does not complete quickly, stop — the probe itself is wrong.

- [ ] **Step 4: Run the experiment — the same window once it is hung**

Wait for the label to read `HUNG` (past the 8s timer), then run the identical command. Record `elapsedMs`.

**This is the measurement the design turns on:**
- **elapsed is small and `ret=True`** → `PrintWindow` does NOT block. Ratification item 3 evaporates. Tasks 15 and 19's timeout and containments are **not built**; `CaptureOutcome.TimedOut` and `scrapeFallbackTargetUnresponsive` still ship (they cost nothing and the code is already specified) but are unreachable in practice, and the plan says so.
- **elapsed runs to the 120s sleep** → it DOES block. Build the timeout, the dedicated thread and the circuit breaker.

- [ ] **Step 5: Run it a second and third time against the still-hung window**

If it blocks, each call is a permanent leak. Record the process's GDI handle count between runs:

```
powershell -Command "(Get-Process -Id (Get-Process -Name FlaUI.Mcp.Server -ErrorAction SilentlyContinue).Id).HandleCount"
```

This confirms or refutes the *cumulative* degradation claim in ratification item 3 — the claim the circuit breaker exists to bound. A cumulative rise confirms it.

- [ ] **Step 6: Record the result**

Create `docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md` with a `## Risk 2 — does PrintWindow block?` section holding the exact command, the control number, the experiment number, the repeat-run handle counts, and a one-line **VERDICT: BLOCKS** or **VERDICT: DOES NOT BLOCK**.

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md
git commit -m "measure(item8): risk 2 - does PrintWindow block on a non-pumping window"
```

### Task 2: Measure stale composition, Chromium and Electron

This is **risks 1 and 3**, folded into one session because they need the same window. Risk 3's answer can change the design; risk 1's cannot.

**Files:**
- Create: `.clavity/scratch/item8-plan/stale-probe/StaleProbe.ps1`
- Modify: `docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md`

- [ ] **Step 1: Write the probe**

It captures via `PrintWindow` and reads the UIA tree with no delay between them, so the two observations are as close to simultaneous as the harness allows. Save as `.clavity/scratch/item8-plan/stale-probe/StaleProbe.ps1`:

```powershell
param([Parameter(Mandatory=$true)][string]$TitleMatch,
      [int]$Iterations = 40)

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PW2 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
[void][PW2]::SetProcessDPIAware()

$p = Get-Process | Where-Object { $_.MainWindowTitle -like "*$TitleMatch*" } | Select-Object -First 1
if (-not $p) { throw "no window matching '$TitleMatch'" }
$h = $p.MainWindowHandle
$r = New-Object PW2+RECT; [void][PW2]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$mismatch = 0
for ($i = 0; $i -lt $Iterations; $i++) {
  $bmp = New-Object Drawing.Bitmap $w, $ht
  $g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  [void][PW2]::PrintWindow($h, $hdc, 2)
  $g.ReleaseHdc($hdc); $g.Dispose()
  # Tree read immediately after the pixels.
  $name = $root.Current.Name
  # Sample a 12x12 grid and hash it, so a frame change is detectable without storing every PNG.
  $sb = New-Object Text.StringBuilder
  for ($y = 0; $y -lt 12; $y++) { for ($x = 0; $x -lt 12; $x++) {
    [void]$sb.Append($bmp.GetPixel([int]($x * ($w-1) / 11), [int]($y * ($ht-1) / 11)).ToArgb())
  } }
  Write-Host ("{0:d3} treeName='{1}' pixHash={2}" -f $i, $name, $sb.ToString().GetHashCode())
  $bmp.Save("$PSScriptRoot\stale-$TitleMatch-$i.png")
  $bmp.Dispose()
}
```

- [ ] **Step 2: Run it against a Chromium browser**

Open Chrome or Edge on a page with a visible state you can toggle fast (a password field with a reveal button is ideal). Start the probe, and while it runs, toggle the state repeatedly.

```
powershell -ExecutionPolicy Bypass -File .clavity/scratch/item8-plan/stale-probe/StaleProbe.ps1 -TitleMatch Chrome
```

Two questions from one run:
- **Risk 1:** do the PNGs show real page content, or are they blank/black? Record the answer. A blank result narrows where the feature is usable but changes no design.
- **Risk 3:** does any PNG show the OLD state while the tree line beside it reports the NEW one? Inspect the frames around each toggle.

- [ ] **Step 3: Run it against an Electron app**

VS Code, Slack, Discord — whichever is installed. **Do not treat the Chromium result as a proxy for this**: Electron embeds Chromium but drives its own compositor and window chrome.

```
powershell -ExecutionPolicy Bypass -File .clavity/scratch/item8-plan/stale-probe/StaleProbe.ps1 -TitleMatch "Visual Studio Code"
```

- [ ] **Step 4: Record the result, honouring the one-sidedness**

Append to the measurements doc. **The stale-composition probe is ONE-SIDED and the record must say so in those words:** a positive result CONFIRMS the race; a negative does NOT refute it, because this is a timing race between an asynchronous compositor and a separate tree walk, so a finite number of clean runs means "not observed at these timings", never "cannot happen".

Write the verdict as one of:
- **CONFIRMED** — the race is real. Stop and escalate to the operator: the spec has no mitigation designed for it and §2.5 explicitly does not cover it.
- **NOT OBSERVED IN N RUNS** — ship with the risk documented and unmitigated. **Do not delete risk 3.**

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md
git commit -m "measure(item8): risks 1+3 - Chromium, Electron, and the stale-composition probe"
```

### Task 3: The design gate — stop, report, confirm

**Files:**
- Modify: `docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md`

- [ ] **Step 1: Write the gate section**

Append a `## DESIGN GATE` section answering exactly three questions:

```markdown
## DESIGN GATE (Task 3)

1. Does PrintWindow BLOCK on a non-pumping window?   BLOCKS | DOES NOT BLOCK
   -> If DOES NOT BLOCK: Tasks 15b and 19b are NOT BUILT. Record that here.
2. Is the stale composition CONFIRMED?               CONFIRMED | NOT OBSERVED IN <n> RUNS
   -> If CONFIRMED: STOP. Escalate to the operator before any further task.
3. Do Chromium AND Electron render under PW_RENDERFULLCONTENT?  BOTH | ONE | NEITHER
   -> If not BOTH: record which, and add the limitation to the tool description in Task 22.
```

- [ ] **Step 2: Stop and report to the operator**

Do not begin Phase 1 until the operator has seen these three answers. Question 2 in particular is an unmitigated privacy risk being accepted, and the spec states plainly that accepting it is the operator's call and not the plan's.

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/plans/2026-08-21-occlusion-aware-capture-measurements.md
git commit -m "measure(item8): design gate - the three answers Phases 1-6 depend on"
```

---

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

### Task 5: `CaptureWarning` and the seven codes

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
    [InlineData("windowResized")]
    [InlineData("popupsNotRendered")]
    [InlineData("scrapeFallbackTargetUnresponsive")]
    [InlineData("scrapeFallbackTargetChanging")]
    public void Every_shipped_code_has_a_recourse(string code)
    {
        var w = CaptureWarnings.For(code);
        Assert.Equal(code, w.Code);
        Assert.False(string.IsNullOrWhiteSpace(w.Recourse));
    }

    [Fact]
    public void Exactly_seven_codes_ship()
        => Assert.Equal(7, CaptureWarnings.AllCodes.Count);

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

/// <summary>The seven codes this feature ships, with their recourse text. Centralised so the wire
/// contract has exactly one definition -- §5 settles these strings, not the implementation.</summary>
public static class CaptureWarnings
{
    public const string UniformCanvas = "uniformCanvas";
    public const string DesktopCanvasUniform = "desktopCanvasUniform";
    public const string ElementCanvasUniform = "elementCanvasUniform";
    public const string WindowResized = "windowResized";
    public const string PopupsNotRendered = "popupsNotRendered";
    public const string ScrapeFallbackTargetUnresponsive = "scrapeFallbackTargetUnresponsive";
    public const string ScrapeFallbackTargetChanging = "scrapeFallbackTargetChanging";

    private static readonly Dictionary<string, string> Recourses = new(StringComparer.Ordinal)
    {
        [UniformCanvas] =
            "The whole window rendered as a single colour, so this image may not be usable. " +
            "Read the UIA tree with desktop_snapshot instead.",
        [DesktopCanvasUniform] =
            "The whole desktop came back a single colour. The usual causes are a secure desktop (a UAC " +
            "prompt), DRM-protected content, or a session in transition. UIA is normally blocked in " +
            "those states too, so desktop_snapshot will not help - wait for the condition to clear and " +
            "re-capture.",
        [ElementCanvasUniform] =
            "This element's pixels may have failed to render even though the window as a whole did - a " +
            "hardware-accelerated child viewport is the usual cause. Verify through the UIA tree before " +
            "concluding the control is blank.",
        [WindowResized] =
            "The window changed size mid-capture. The image itself is sound - it was cropped back to the " +
            "region you asked for - but the layout inside it may have reflowed, so any UIA tree or " +
            "element ref you hold for this window may be geometrically stale. Re-snapshot before acting " +
            "on cached coordinates.",
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

    public static IReadOnlyList<string> AllCodes { get; } = new[]
    {
        UniformCanvas, DesktopCanvasUniform, ElementCanvasUniform, WindowResized,
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
Expected: PASS — 10 passed.

- [ ] **Step 5: Prove the gate is non-vacuous with a logic mutant**

Temporarily set `[WindowResized]` to `""`. Re-run. Expected: `Every_shipped_code_has_a_recourse` FAILS on the `windowResized` row. **Revert.**

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
Expected: PASS — 11 passed.

- [ ] **Step 5: Prove the gates are non-vacuous with three logic mutants**

Each mutant must turn a *named* test red. Run them one at a time and revert each.

1. Change `effective.X + w1.X` to `effective.X + w2.X` in the `Absolute` line.
   Expected: `A_pure_move_keeps_masks_on_W1_and_reports_on_W2` FAILS with absolute `(200,300)` — the exact 100px misalignment from the spec's trace.
2. Change the guard to `if (effective.IsEmpty) return null;`.
   Expected: `A_zero_width_touching_intersection_is_rejected_even_though_IsEmpty_is_false` FAILS.
3. Change `relative` to use `e` unmodified (drop the `- w1` translation).
   Expected: `A_pure_move_keeps_masks_on_W1_and_reports_on_W2` and `A_grown_window_is_cropped_back_to_the_region_that_was_scanned` both FAIL.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCropGeometry.cs test/FlaUI.Mcp.Tests/Perception/WindowCropGeometryTests.cs
git commit -m "feat(capture): the pure crop geometry - effective, absolute, reported"
```

### Task 9: `Encode` takes the two rectangles, the method and the warnings

`Encode` builds the `CaptureResult`, so it must be HANDED the `W2`-anchored rectangle — it cannot derive one from the other. Both cross the call.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs:45-71`
- Test: `test/FlaUI.Mcp.Tests/Perception/EncodeContractTests.cs`

- [ ] **Step 1: Make `Encode` internal so it can be tested directly**

`Encode` is currently `private static`. Change it to `internal static` and add to `src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj` inside the existing `<PropertyGroup>` if not already present:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="FlaUI.Mcp.Tests" />
  </ItemGroup>
```

Check first — run `grep -n "InternalsVisibleTo" src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj`. If it already exists, add nothing.

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
        var warn = new[] { CaptureWarnings.For(CaptureWarnings.WindowResized) };
        var r = ScreenCapture.Encode(src, new Rectangle(0, 0, 10, 10), new Rectangle(0, 0, 10, 10),
                                     System.Array.Empty<Rectangle>(), 0, "printWindow", warn);
        Assert.Equal("printWindow", r.CaptureMethod);
        Assert.Single(r.CaptureWarnings);
        Assert.Equal("windowResized", r.CaptureWarnings[0].Code);
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

Replace `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` lines 45–71 with:

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
git add src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj test/FlaUI.Mcp.Tests/Perception/EncodeContractTests.cs
git commit -m "feat(capture): Encode takes absolute+reported, method and warnings"
```

### Task 10: `CaptureRectangle` learns its scope — and the third caller is wired explicitly

The scrape seam now has three callers with divergent requirements: full-desktop must evaluate `desktopCanvasUniform`, a fallback scrape must not and carries a `scrapeFallback*` code its caller decided, and the OCR path must not either.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs:36-43`
- Modify: `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:49`
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

        Assert.Equal(4, sites.Count);   // 1 declaration + 3 call sites

        var calls = sites.Where(x => !x.Text.Contains("public static CaptureResult")).ToList();
        Assert.Equal(3, calls.Count);

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

- [ ] **Step 4: Update all three call sites**

`src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:49`:

```csharp
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(
                    vbounds, desk.Rects, maxWidth, CaptureScope.FullDesktop,
                    System.Array.Empty<CaptureWarning>()));
```

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

## Phase 3 — The detector, the yardstick, and the geometry walk

### Task 11: `UniformCanvasDetector` — settle the sampling by measurement

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
            var windowBounds = string.IsNullOrEmpty(@ref) ? captureBounds : SafeWindowRect(win, captureBounds);
            if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
                return new CaptureGeometry(captureBounds, System.Array.Empty<System.Drawing.Rectangle>(),
                    false, false, null, System.Array.Empty<MaskEscalationEntry>(),
                    windowBounds, NativeHandleOf(win), DegenerateWindow: true);
```

- [ ] **Step 5: Add the two helpers**

Add as private static members of `PerceptionManager`:

```csharp
    /// <summary>W1 — the WINDOW's own rect, needed even when `target` is an element. Guarded because it
    /// is a UIA read on a possibly-dying window and this region must never raise a raw exception; a
    /// failure yields the capture rect, which the degeneracy check above then judges normally.</summary>
    private static System.Drawing.Rectangle SafeWindowRect(AutomationElement win, System.Drawing.Rectangle fallback)
    {
        try { return win.BoundingRectangle; }
        catch (System.Exception ex) when (ex is not System.OutOfMemoryException
                                          and not System.OperationCanceledException)
        { _ = ex; return fallback; }
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

    // WINDOW SCOPE, NO MASKS, RESIZED -> CONTINUE to the crop and warn. It MUST continue: the crop is what
    // discards the region a GROWN window added that the walk never inspected. Skipping it would hand
    // Encode a W2-sized bitmap against a W1-sized rect AND return unscanned pixels.
    [Fact]
    public void Window_scope_without_masks_crops_and_warns_on_a_resize()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 1000, 700);
        var o = Run(Geo(w1, w1), w2, CaptureScope.Window, FakeWindowImageSource.Solid(Color.White));
        Assert.Equal(CaptureOutcomeKind.Completed, o.Kind);
        Assert.Equal(800, o.Result!.W);        // cropped back to the scanned region
        Assert.Equal(600, o.Result.H);
        Assert.Contains(o.Result.CaptureWarnings, w => w.Code == "windowResized");
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
        if (w1.Size != w2.Size)
        {
            // Element scope: the caller retries and, on exhaustion, falls back to the scrape.
            // Window scope WITH masks: the caller retries and, on exhaustion, REFUSES -- the masks were
            // computed against the pre-resize layout and a reflow moves what they were sampled to cover.
            if (scope == CaptureScope.Element || geo.MaskRects.Count > 0)
                return CaptureOutcome.Resized;

            // Window scope with NO masks: nothing was going to be redacted, so no misalignment is
            // possible. Warn and CONTINUE -- the crop below is what discards the region a grown window
            // added that the walk never inspected.
            warnings = Append(warnings, CaptureWarnings.For(CaptureWarnings.WindowResized));
        }

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

- [ ] **Step 5: Prove the gates are non-vacuous with three logic mutants**

1. Move the `probeMin` check to AFTER the resize branch.
   Expected: `A_window_minimized_at_capture_time_refuses_and_does_not_report_a_resize` FAILS with a `Resized` outcome instead of the refusal — the exact ordering defect the canonical list was written to prevent.
2. Change `if (scope == CaptureScope.Element || geo.MaskRects.Count > 0)` to `if (scope == CaptureScope.Element)`.
   Expected: `Window_scope_with_masks_reports_a_resize_rather_than_refusing` FAILS with `Completed`.
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

## Phase 5 — The coordinator: the retry loop, the bookend walk, the fallbacks

**The caller owns the loop, and this is the component that IS the caller.** `CaptureWindow` reports a mismatch; it cannot resolve one, because resolving means re-walking the UIA tree and a seam handed a finished `CaptureGeometry` has no way to do that.

**Why a new class rather than putting this in `ScreenshotTools`:** the loop takes the geometry walk as a delegate, which makes the whole of steps 1–9 headless-testable against a fake walk and a fake acquisition. That is the only way the bookend walk and the retry policy get tests at all, and those two are the least-reviewed parts of the design.

### Task 17: `WindowCaptureCoordinator` — the retry loop and the resize policy

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/WindowCaptureCoordinatorTests.cs`
- Modify: `test/FlaUI.Mcp.Tests/Perception/CaptureGeometryCallSiteTests.cs` (the count goes 3 → 4)

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class WindowCaptureCoordinatorTests
{
    private static CaptureGeometry Geo(Rectangle capture, Rectangle window, params Rectangle[] masks)
        => new(capture, masks, false, false, null, Array.Empty<MaskEscalationEntry>(),
               window, new IntPtr(0x1234), false);

    /// <summary>A walk that returns a scripted sequence, one entry per attempt, so a test can stage a
    /// window that settles on attempt N.</summary>
    private static Func<WindowHandle, string?, Task<CaptureGeometry>> Walk(params CaptureGeometry[] seq)
    {
        int i = 0;
        return (_, _) => Task.FromResult(seq[Math.Min(i++, seq.Length - 1)]);
    }

    private static WindowCaptureCoordinator Make(
        Func<WindowHandle, string?, Task<CaptureGeometry>> walk,
        IWindowImageSource source,
        Func<IntPtr, Rectangle?> w2,
        CaptureRetryOptions? opts = null)
        => new(walk, source, opts ?? new CaptureRetryOptions(MaxAttempts: 3, TimeoutMs: 1000),
               w2Probe: w2, minimizedProbe: _ => false,
               scrape: (bounds, masks, mw, scope, warns) =>
                   new CaptureResult(Array.Empty<byte>(), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                     1.0, masks.Count, "screenScrape", warns));

    [Fact]
    public async Task A_static_window_captures_on_the_first_attempt()
    {
        var w = new Rectangle(0, 0, 400, 300);
        var c = Make(Walk(Geo(w, w)), FakeWindowImageSource.Solid(Color.White), _ => w);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Empty(r.Result.CaptureWarnings);
    }

    // THE RATIFIED BEHAVIOUR. Window scope WITH masks retries rather than refusing on the first mismatch.
    // Attempt 1 sees a resize; attempt 2 is consistent; the capture succeeds with NO warning, because
    // nothing about the returned image is stale.
    [Fact]
    public async Task Window_scope_with_masks_retries_and_succeeds_when_the_window_settles()
    {
        var stale  = new Rectangle(0, 0, 800, 600);
        var settled = new Rectangle(0, 0, 900, 600);
        var mask = new Rectangle(10, 10, 50, 20);
        var c = Make(Walk(Geo(stale, stale, mask), Geo(settled, settled, mask)),
                     FakeWindowImageSource.Solid(Color.White), _ => settled);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.DoesNotContain(r.Result.CaptureWarnings, w => w.Code == "windowResized");
    }

    // ...and REFUSES on exhaustion. Never falls back to the scrape: a scraped image of a reflowed window
    // carries the SAME stale masks, so switching backends cannot fix a mask problem.
    [Fact]
    public async Task Window_scope_with_masks_refuses_on_exhaustion_and_never_scrapes()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var mask = new Rectangle(10, 10, 50, 20);
        var c = Make(Walk(Geo(w1, w1, mask)), FakeWindowImageSource.Solid(Color.White),
                     _ => new Rectangle(0, 0, 900, 600));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
    }

    // Element scope on exhaustion DOES fall back -- its failure is a geometry mismatch on an image that
    // is otherwise sound, so the scrape genuinely offers something better than nothing.
    [Fact]
    public async Task Element_scope_falls_back_to_the_scrape_on_exhaustion()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(100, 100, 200, 150);
        var c = Make(Walk(Geo(e, w1)), FakeWindowImageSource.Solid(Color.White),
                     _ => new Rectangle(0, 0, 700, 600));
        var r = await c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0);
        Assert.Equal("screenScrape", r.Result.CaptureMethod);
        Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetChanging");
    }

    // ⚠⚠ ...BUT ONLY WHEN THERE IS NOTHING TO MASK. A resize reflows the WINDOW's layout regardless of
    // which scope asked, so painting pre-resize mask rects onto a post-resize scrape under-redacts exactly
    // as it would for window scope. The test above passes an EMPTY mask set, which is the population the
    // fallback was justified by -- spinners, progress dialogs, expanding windows.
    [Fact]
    public async Task Element_scope_WITH_masks_refuses_on_exhaustion_rather_than_scraping()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(100, 100, 200, 150);
        var mask = new Rectangle(110, 110, 40, 20);
        bool scraped = false;
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(e, new[] { mask }, false, false, null,
                Array.Empty<MaskEscalationEntry>(), w1, new IntPtr(0x1234), false)),
            FakeWindowImageSource.Solid(Color.White), new CaptureRetryOptions(2, 1000),
            w2Probe: _ => new Rectangle(0, 0, 700, 600), minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => { scraped = true; return new CaptureResult(Array.Empty<byte>(),
                b.X, b.Y, b.Width, b.Height, 1.0, m.Count, "screenScrape", warns); });

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0));
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
        Assert.False(scraped, "pre-resize masks must never be painted onto a post-resize scrape");
    }

    // The fallback must use the LAST attempt's geometry. Reusing the first would hand the scrape
    // coordinates staler by the entire duration of the loop -- the loop actively degrading the fallback
    // it exists to reach.
    [Fact]
    public async Task The_fallback_scrape_uses_the_LAST_attempts_geometry()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var first  = new Rectangle(100, 100, 200, 150);
        var latest = new Rectangle(140, 160, 200, 150);
        var c = Make(Walk(Geo(first, w1), Geo(latest, w1), Geo(latest, w1)),
                     FakeWindowImageSource.Solid(Color.White),
                     _ => new Rectangle(0, 0, 700, 600));
        var r = await c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0);
        Assert.Equal(140, r.Result.X);
        Assert.Equal(160, r.Result.Y);
    }

    // A timeout is a MECHANISM failure: the window is on screen with real pixels and PrintWindow simply
    // could not get a copy. Refusing here would be a regression -- an agent can photograph a hung window
    // perfectly well today.
    [Fact]
    public async Task A_timeout_falls_back_to_the_scrape_with_the_unresponsive_code()
    {
        var w = new Rectangle(0, 0, 400, 300);
        var c = Make(Walk(Geo(w, w)), FakeWindowImageSource.TimesOut(), _ => w);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("screenScrape", r.Result.CaptureMethod);
        Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetUnresponsive");
    }

    // A window caught mid-open reports a degenerate rect for a frame. That is the transient the loop
    // exists to absorb, and it must NOT fail terminally on the first bad frame.
    [Fact]
    public async Task A_degenerate_W1_retries_rather_than_failing_terminally()
    {
        var good = new Rectangle(0, 0, 400, 300);
        var degenerate = new CaptureGeometry(default, Array.Empty<Rectangle>(), false, false, null,
            Array.Empty<MaskEscalationEntry>(), default, new IntPtr(0x1234), DegenerateWindow: true);
        var c = Make(Walk(degenerate, Geo(good, good)), FakeWindowImageSource.Solid(Color.White), _ => good);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
    }

    // ...but an ALREADY-MINIMIZED window must NOT be retried: retrying just fails identically until the
    // budget runs out. Both surface as ElementNotActionable; the internal signal is what differs.
    [Fact]
    public async Task An_already_minimized_window_refuses_without_burning_the_retry_budget()
    {
        var minimized = new CaptureGeometry(default, Array.Empty<Rectangle>(), Minimized: true, false, null,
            Array.Empty<MaskEscalationEntry>(), default, new IntPtr(0x1234), false);
        int walks = 0;
        var c = Make((_, _) => { walks++; return Task.FromResult(minimized); },
                     FakeWindowImageSource.Solid(Color.White), _ => new Rectangle(0, 0, 1, 1));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
        Assert.Equal(1, walks);
    }

    [Fact]
    public async Task A_denied_window_refuses_with_TargetDenied()
    {
        var denied = new CaptureGeometry(default, Array.Empty<Rectangle>(), false, Denied: true, "keeper",
            Array.Empty<MaskEscalationEntry>(), default, IntPtr.Zero, false);
        var c = Make(Walk(denied), FakeWindowImageSource.Solid(Color.White), _ => new Rectangle(0, 0, 1, 1));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
    }

    // WARNINGS ARE SCOPED TO THE ATTEMPT. Each retry re-walks, so each attempt has its OWN geometry-time
    // warnings. Carrying attempt 1's popupsNotRendered into attempt 2's successful result would state
    // something false about the image that was actually returned.
    [Fact]
    public async Task A_discarded_attempts_warnings_are_discarded_with_it()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var settled = new Rectangle(0, 0, 900, 600);
        // Attempt 1 has a popup root; attempt 2 does not.
        var withPopup = new CaptureGeometry(w1, Array.Empty<Rectangle>(), false, false, null,
            Array.Empty<MaskEscalationEntry>(), w1, new IntPtr(0x1234), false) { HasPopupRoots = true };
        var without = Geo(settled, settled);
        var c = Make(Walk(withPopup, without), FakeWindowImageSource.Solid(Color.White), _ => settled);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.DoesNotContain(r.Result.CaptureWarnings, w => w.Code == "popupsNotRendered");
    }
}
```

- [ ] **Step 2: Add `HasPopupRoots` to `CaptureGeometry`**

The test above needs it and so does `popupsNotRendered`, which is decided at geometry time. Add it as an **init-only property**, not a positional parameter, so the append-only ordering test is unaffected:

In `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`, change the `CaptureGeometry` declaration's closing `;` to a body:

```csharp
    System.Drawing.Rectangle WindowBounds, System.IntPtr NativeWindowHandle, bool DegenerateWindow)
{
    /// <summary>This window had popup roots at geometry time — PopupFinder.SearchRoots returned more
    /// than the window itself. Decided during the WALK, consumed by the caller, which is why it travels
    /// on the geometry rather than being re-derived downstream.
    ///
    /// An init-only PROPERTY rather than a positional parameter, deliberately: the positional list is
    /// pinned by CaptureGeometryShapeTests and adding to it would churn every construction site for a
    /// flag most of them do not care about.</summary>
    public bool HasPopupRoots { get; init; }
}
```

Then set it at the success return (`PerceptionManager.cs:1110`), where `roots` is in scope:

```csharp
            return new CaptureGeometry(captureBounds, pw, false, false, null, escalations,
                                       windowBounds, NativeHandleOf(win), false)
            { HasPopupRoots = roots.Count > 1 };
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~WindowCaptureCoordinatorTests"`
Expected: FAIL — `WindowCaptureCoordinator` does not exist.

- [ ] **Step 4: Write the coordinator**

Create `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

/// <param name="MaxAttempts">The retry bound. "Retry" MUST be bounded or a continuously-changing window
/// is a livelock: an animating window never satisfies W1.Size == W2.Size, so an unconditional retry tells
/// the agent to loop forever on a capture that can never succeed.</param>
/// <param name="TimeoutMs">The per-acquisition bound handed to IWindowImageSource.</param>
public sealed record CaptureRetryOptions(int MaxAttempts, int TimeoutMs)
{
    /// <summary>The whole retry sequence must terminate within a wall-clock budget small enough that a
    /// caller does not experience it as a hang, and that budget is documented in the tool description
    /// alongside the timeout. A retry loop whose worst case is unbounded in time is the livelock in a
    /// different costume. 3 x 1500ms = 4.5s worst case.</summary>
    public static readonly CaptureRetryOptions Default = new(MaxAttempts: 3, TimeoutMs: 1500);

    public int WorstCaseMs => MaxAttempts * TimeoutMs;
}

/// <summary>What a capture produced, plus the geometry it came from — the tool layer needs both, because
/// escalations live on the geometry and not on the result.</summary>
public sealed record WindowCaptureOutcome(CaptureResult Result, CaptureGeometry Geometry);

/// <summary>Canonical steps 1-9: the walk, the retry loop, the bookend validation walk and the scrape
/// fallbacks. THE CALLER, in the sense the spec uses that word.
///
/// The geometry walk arrives as a DELEGATE so this whole class is headless-testable against a scripted
/// walk and a fake acquisition. That is the only way the retry policy and the bookend walk get tests, and
/// they are the two least-reviewed parts of this design.</summary>
public sealed class WindowCaptureCoordinator
{
    private readonly Func<WindowHandle, string?, Task<CaptureGeometry>> _walk;
    private readonly IWindowImageSource _source;
    private readonly CaptureRetryOptions _opts;
    private readonly Func<IntPtr, Rectangle?>? _w2Probe;
    private readonly Func<IntPtr, bool>? _minimizedProbe;
    private readonly Func<IntPtr, bool> _isHung;
    private readonly Func<Task<bool>>? _denylistedVisible;
    private readonly Func<Rectangle, IReadOnlyList<Rectangle>, int, CaptureScope,
                          IReadOnlyList<CaptureWarning>, CaptureResult> _scrape;

    public WindowCaptureCoordinator(
        Func<WindowHandle, string?, Task<CaptureGeometry>> walk,
        IWindowImageSource source,
        CaptureRetryOptions opts,
        Func<IntPtr, Rectangle?>? w2Probe = null,
        Func<IntPtr, bool>? minimizedProbe = null,
        Func<Rectangle, IReadOnlyList<Rectangle>, int, CaptureScope,
             IReadOnlyList<CaptureWarning>, CaptureResult>? scrape = null,
        CaptureCircuitBreaker? breaker = null,
        Func<IntPtr, bool>? isHungProbe = null,
        Func<Task<bool>>? denylistedVisible = null)
    {
        _denylistedVisible = denylistedVisible;
        _walk = walk; _source = source; _opts = opts;
        _w2Probe = w2Probe; _minimizedProbe = minimizedProbe;
        _scrape = scrape ?? ScreenCapture.CaptureRectangle;
        _breaker = breaker;
        // Injected so the breaker's recovery behaviour is headless-testable; production uses the OS.
        _isHung = isHungProbe ?? IsHungAppWindow;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    public async Task<WindowCaptureOutcome> CaptureAsync(WindowHandle handle, string? @ref,
                                                         CaptureScope scope, int maxWidth)
    {
        CaptureGeometry geo = default!;

        for (int attempt = 1; attempt <= _opts.MaxAttempts; attempt++)
        {
            // Step 1. Every attempt re-walks, which is what makes a retry meaningful.
            geo = await _walk(handle, @ref);

            // Step 1b. The EXISTING geometry-time refusals, unchanged. Neither is retryable.
            if (geo.Denied)
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Capturing windows owned by '{geo.DeniedProcess}' is blocked.",
                    "capture a non-sensitive window");
            if (geo.Minimized)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Window is minimized; restore it first.",
                    "desktop_window_transform restore, then retry");

            // Step 2's signal. RETRYABLE, and distinguishable from minimized above -- a window caught
            // mid-open reports a degenerate rect for a frame, which is exactly the transient this loop
            // exists to absorb. Both surface to the agent as ElementNotActionable once the budget is
            // spent; the internal signal is what differs.
            if (geo.DegenerateWindow)
            {
                if (attempt < _opts.MaxAttempts) continue;
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "The target window reported no renderable area on every attempt.",
                    "restore or resize the window, then retry");
            }

            // Warnings are scoped to THIS ATTEMPT. A discarded attempt's warnings are discarded with it.
            var warnings = geo.HasPopupRoots
                ? new[] { CaptureWarnings.For(CaptureWarnings.PopupsNotRendered) }
                : Array.Empty<CaptureWarning>();

            // Steps 4-8, inside the seam. The in-flight marker brackets the acquisition so a CONCURRENT
            // request for the same window can see that this one is stuck before any timeout is recorded.
            CaptureOutcome outcome;
            _breaker?.BeginAcquisition(geo.NativeWindowHandle);
            try
            {
                outcome = ScreenCapture.CaptureWindow(geo, maxWidth, scope, warnings, _source,
                                                      _opts.TimeoutMs, _w2Probe, _minimizedProbe);
            }
            finally { _breaker?.EndAcquisition(geo.NativeWindowHandle); }

            switch (outcome.Kind)
            {
                case CaptureOutcomeKind.Completed:
                    // Step 9 is added in Task 18.
                    return new WindowCaptureOutcome(outcome.Result!, geo);

                case CaptureOutcomeKind.TimedOut:
                    // A MECHANISM failure: the window is on screen with real pixels and PrintWindow simply
                    // could not get a copy because the target's loop is blocked. The scrape reads the
                    // composited desktop and is unaffected, so it genuinely has a better answer than
                    // nothing. Refusing here would take away behaviour that works today.
                    return new WindowCaptureOutcome(
                        await ScrapeAsync(geo, scope, maxWidth, warnings,
                                          CaptureWarnings.ScrapeFallbackTargetUnresponsive),
                        geo);

                case CaptureOutcomeKind.TargetTransient:
                    // The target's geometry was momentarily unusable -- a degenerate W2, or an element
                    // rect outside its own window. Both are UIA reporting a bad rectangle for a frame,
                    // which is the transient this loop exists to absorb and which the design already
                    // absorbs for a degenerate W1. Terminal only when the budget is spent, and it
                    // surfaces to the agent as the SAME code the W1 case does.
                    if (attempt < _opts.MaxAttempts) continue;
                    throw new ToolException(ToolErrorCode.ElementNotActionable,
                        $"{outcome.TransientReason} This held on every attempt.",
                        "re-snapshot the window for fresh bounds, or restore/resize it, then retry");

                case CaptureOutcomeKind.Resized:
                    if (attempt < _opts.MaxAttempts) continue;
                    return new WindowCaptureOutcome(
                        await OnResizeExhaustedAsync(geo, scope, maxWidth, warnings), geo);
            }
        }

        throw new ToolException(ToolErrorCode.ElementNotActionable,
            "The capture did not converge within the retry budget.",
            "wait for the window to settle, then retry");
    }

    /// <summary>The terminal outcome when the size never settled. The two scopes differ, and the
    /// difference is NOT inconsistency.</summary>
    private async Task<CaptureResult> OnResizeExhaustedAsync(CaptureGeometry geo, CaptureScope scope,
                                                             int maxWidth,
                                                             IReadOnlyList<CaptureWarning> warnings)
    {
        // WINDOW SCOPE WITH MASKS: REFUSE, and never scrape. The mask rects were computed against the
        // pre-resize layout; a resize reflows content, so they no longer necessarily cover what they were
        // sampled to cover. A scrape reproduces that exactly -- the same stale rects over the same
        // reflowed content -- so switching backends cannot fix a MASK problem.
        if (geo.MaskRects.Count > 0 && scope == CaptureScope.Window)
            throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                "The window kept changing size, so its redacted regions cannot be reliably located.",
                "wait for the window to settle, then retry, or capture a different window");

        // ⚠⚠ ELEMENT SCOPE WITH A NON-EMPTY MASK SET REFUSES TOO, and an earlier version of this plan
        // let it fall back. A resize reflows the WINDOW's layout regardless of which scope asked for the
        // capture, so painting pre-resize mask rects onto a post-resize scrape under-redacts exactly as it
        // would for window scope. Falling back "because that is what the tool does today" is the
        // pre-existing-defect defence this project does not accept, and it contradicted the very argument
        // that makes window scope refuse.
        // *(AGY-AFTER panel over this plan, round 4, Guard-Consistency Auditor.)*
        //
        // ⚠ The REGRESSION ARGUMENT FOR THE FALLBACK SURVIVES INTACT, because it was always about a
        // different population. Spinners, progress dialogs and expanding windows -- the cases the fallback
        // was justified by -- carry NO redacted content, so they still fall back. Only a resizing window
        // that also holds something worth masking now refuses.
        if (geo.MaskRects.Count > 0)
            throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                "The window kept changing size, so its redacted regions cannot be reliably located.",
                "wait for the window to settle, then retry, or capture a different window");

        // ELEMENT SCOPE, NOTHING TO MASK: fall back. Its failure is a GEOMETRY mismatch on an image that
        // is otherwise sound, nothing was going to be redacted, and the scrape is what this tool does for
        // this window today -- so the fallback restores current behaviour rather than returning nothing.
        return await ScrapeAsync(geo, scope, maxWidth, warnings,
                                 CaptureWarnings.ScrapeFallbackTargetChanging);
    }

    /// <summary>The fallback scrape, using the LAST attempt's geometry. Reusing the first attempt's would
    /// hand the scrape coordinates staler by the entire duration of the loop.
    ///
    /// ⚠ The scrape is NOT synchronised with the geometry, and nothing here claims it is: it takes PIXELS
    /// at one instant while the element rect still comes from a UIA walk that happened earlier. That
    /// staleness is the inherent race §2 documents -- it is today's behaviour, not a new defect -- and the
    /// fallback is justified by "better than nothing", not by synchronisation it does not have.</summary>
    private async Task<CaptureResult> ScrapeAsync(CaptureGeometry geo, CaptureScope scope, int maxWidth,
                                                  IReadOnlyList<CaptureWarning> warnings, string code)
    {
        // ⚠⚠ THE DENYLIST GUARD RUNS ON EVERY FALLBACK, and without it this path bypassed a refusal the
        // full-desktop path treats as absolute. A fallback takes RAW DESKTOP PIXELS of the target's rect,
        // but the mask set was walked for the TARGET ONLY -- so a credential window overlapping the
        // target is photographed completely unmasked. `ScreenshotTools.cs:38` refuses a full-desktop
        // capture outright when any denylisted window is visible, for exactly this reason, and that guard
        // sits inside `if (string.IsNullOrEmpty(window))` so it never reached here.
        //
        // ⚠ THIS EXPOSURE IS PRE-EXISTING, AND ITEM 8 NARROWS IT RATHER THAN CREATING IT. Today EVERY
        // window-scope capture is a scrape with this same hole. After item 8 the primary path renders only
        // the target window, so an overlapping credential window is STRUCTURALLY ABSENT -- the exposure
        // survives only on the fallbacks. Closing it here means the feature strictly improves the posture
        // instead of carrying a known hole into its own new code paths.
        //
        // Refusing is the only honest option: we are already here because PrintWindow could not deliver,
        // so there is no unaffected image to return instead.
        if (_denylistedVisible is not null && await _denylistedVisible())
            throw new ToolException(ToolErrorCode.TargetDenied,
                "A credential/denylisted window is currently visible, and this capture can only fall " +
                "back to a screen scrape, which would photograph it unmasked.",
                "dismiss the credential window, then retry");

        return _scrape(geo.Bounds, geo.MaskRects, maxWidth, scope,
                       ScreenCapture.Append(warnings, CaptureWarnings.For(code)));
    }
}
```

- [ ] **Step 5: Make `ScreenCapture.Append` visible to the coordinator**

It is `internal` and both types are in `FlaUI.Mcp.Core`, so no change is needed. Confirm with `dotnet build FlaUI.Mcp.slnx`.

- [ ] **Step 6: Confirm Task 12's call-site sweep is still green and still says 3**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureGeometryCallSiteTests"`
Expected: PASS, unchanged.

The coordinator takes the walk as a **delegate**, so it adds no call site to `ResolveWindowCaptureGeometryAsync` — the count is still `ScreenshotTools:53`, `PerceptionManager:1136` (OCR) and `PerceptionManager:1181` (full-desktop). **If this test went red, the coordinator was wired by calling `PerceptionManager` directly instead of through the injected delegate, which would also destroy every headless test in Tasks 17–19. Fix the wiring, not the number.**

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed, build 0 warnings / 0 errors.

- [ ] **Step 8: Prove the gates are non-vacuous with three logic mutants**

1. Change `OnResizeExhausted`'s window-scope branch to fall through to `Scrape`.
   Expected: `Window_scope_with_masks_refuses_on_exhaustion_and_never_scrapes` FAILS.
2. Change the degenerate branch to `throw` immediately instead of `continue`.
   Expected: `A_degenerate_W1_retries_rather_than_failing_terminally` FAILS.
3. Hoist `warnings` outside the `for` loop and accumulate into it across attempts.
   Expected: `A_discarded_attempts_warnings_are_discarded_with_it` FAILS.

**Revert every mutant.**

- [ ] **Step 9: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/
git commit -m "feat(capture): the coordinator - bounded retry, ratified resize policy, scrape fallbacks"
```

### Task 18: The bookend validation walk

**This closes the relayout leak, and it is the newest and least-reviewed idea in the design.** It postdates all thirty panel rounds. Treat every claim in §2.5 as unproven.

**What it does NOT do:** it does not mitigate risk 3's stale composition. There the pixels are older than the tree and a value can be revealed in place, leaving the element's rectangle mathematically identical. `M1 == M2` and the walk passes a genuinely stale capture.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/BookendWalkTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class BookendWalkTests
{
    private static CaptureGeometry Geo(Rectangle w, params Rectangle[] masks)
        => new(w, masks, false, false, null, Array.Empty<MaskEscalationEntry>(),
               w, new IntPtr(0x1234), false);

    private static WindowCaptureCoordinator Make(List<CaptureGeometry> script, out Func<int> walkCount)
    {
        int i = 0;
        walkCount = () => i;
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>(
            (_, _) => Task.FromResult(script[Math.Min(i++, script.Count - 1)]));
        return new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(3, 1000),
            w2Probe: _ => new Rectangle(0, 0, 400, 300), minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));
    }

    private static readonly Rectangle W = new(0, 0, 400, 300);

    // THE PROPERTY THAT KEEPS THE COMMON CASE FREE. An empty mask set needs no second walk: there is
    // nothing that could have gone stale. Invisible if it breaks, so it is pinned.
    [Fact]
    public async Task An_empty_mask_set_performs_no_second_walk()
    {
        var c = Make(new List<CaptureGeometry> { Geo(W) }, out var walks);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Equal(1, walks());   // ONE walk, not two
    }

    [Fact]
    public async Task A_stable_mask_set_performs_a_second_walk_and_succeeds()
    {
        var mask = new Rectangle(10, 10, 50, 20);
        var c = Make(new List<CaptureGeometry> { Geo(W, mask), Geo(W, mask) }, out var walks);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Equal(2, walks());   // pre-capture + bookend
    }

    // THE LEAK THIS CLOSES. The window did not change size -- W1.Size == W2.Size throughout, so the
    // resize guard never fires -- but the content reflowed and the mask moved. Without the bookend the
    // stale rect is painted over the wrong region and sensitive content is exposed silently.
    [Fact]
    public async Task A_mask_that_moved_under_the_capture_triggers_a_retry()
    {
        var before = new Rectangle(10, 10, 50, 20);
        var after  = new Rectangle(10, 90, 50, 20);   // reflowed down, window size unchanged
        var c = Make(new List<CaptureGeometry>
        {
            Geo(W, before),   // attempt 1 pre-capture
            Geo(W, after),    // attempt 1 bookend  -> MISMATCH, retry
            Geo(W, after),    // attempt 2 pre-capture
            Geo(W, after),    // attempt 2 bookend  -> match, succeed
        }, out var walks);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Equal(4, walks());
    }

    // ⚠ A PURE MOVE MUST NOT TRIP IT. Mask rects are ABSOLUTE screen coordinates, so a user dragging the
    // window between the two walks shifts every one of them. Compared absolutely, that reads as a total
    // relayout and a harmless drag becomes a refusal -- contradicting the design's rule, stated in three
    // places, that a pure move is harmless. The comparison is WINDOW-RELATIVE for exactly this reason.
    [Fact]
    public async Task A_pure_window_move_between_the_two_walks_does_not_trip_the_bookend()
    {
        var at0   = new Rectangle(0, 0, 400, 300);
        var at500 = new Rectangle(500, 250, 400, 300);      // dragged, SAME size
        // The mask sits at the same place INSIDE the window in both walks.
        var before = new CaptureGeometry(at0, new[] { new Rectangle(10, 10, 50, 20) }, false, false, null,
            Array.Empty<MaskEscalationEntry>(), at0, new IntPtr(0x1234), false);
        var after = new CaptureGeometry(at500, new[] { new Rectangle(510, 260, 50, 20) }, false, false, null,
            Array.Empty<MaskEscalationEntry>(), at500, new IntPtr(0x1234), false);

        int i = 0;
        var script = new[] { before, after };
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(script[Math.Min(i++, script.Length - 1)]),
            FakeWindowImageSource.Solid(Color.White), new CaptureRetryOptions(3, 1000),
            w2Probe: _ => at0, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));

        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);   // NOT a refusal
    }

    // ⚠ A BOOKEND WALK THAT THROWS IS A FAILED CONFIRMATION, NOT AN ESCAPING ERROR. Unguarded it escaped
    // CaptureAsync and discarded a good image already in hand. It is now treated as a MISMATCH -- retry,
    // then the bookend's own terminal outcome.
    //
    // The walk here throws ElementNotActionable while the coordinator's refusal is RedactionUnmaskable,
    // so this proves the coordinator's OWN defined outcome surfaces rather than the walk's exception
    // simply passing through. Those two being the same code would make this test vacuous.
    [Fact]
    public async Task A_bookend_walk_that_throws_becomes_the_defined_refusal_not_a_passthrough()
    {
        var w1 = new Rectangle(0, 0, 400, 300);
        var e  = new Rectangle(50, 50, 100, 80);
        var mask = new Rectangle(60, 60, 20, 10);
        int call = 0;
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>((_, _) =>
        {
            call++;
            // Odd calls are the pre-capture walk; even calls are the bookend, which always throws.
            if (call % 2 == 0)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "the window vanished mid-confirmation", "re-list windows and retry");
            return Task.FromResult(new CaptureGeometry(e, new[] { mask }, false, false, null,
                Array.Empty<MaskEscalationEntry>(), w1, new IntPtr(0x1234), false));
        });
        var c = new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(2, 1000),
            w2Probe: _ => w1, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0));
        // The coordinator's OWN outcome, not the walk's exception passing through.
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
    }

    // ⚠ A BOOKEND EXHAUSTION REFUSES ON ELEMENT SCOPE TOO -- unlike a RESIZE exhaustion, which falls back
    // to the scrape. The difference is what the failure PROVES: a resize proves a geometry mismatch and
    // says nothing about the masks, while a bookend mismatch is direct evidence the mask set MOVED.
    // Scraping with rects we have proven stale would ship an under-redacted image.
    [Fact]
    public async Task A_moving_mask_set_refuses_for_ELEMENT_scope_too_and_never_scrapes()
    {
        int n = 0;
        var e = new Rectangle(50, 50, 100, 80);
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>(
            (_, _) => Task.FromResult(new CaptureGeometry(e,
                new[] { new Rectangle(60, 60 + (n++ * 7), 20, 10) }, false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(0x1234), false)));
        bool scraped = false;
        var c = new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(3, 1000),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => { scraped = true; return new CaptureResult(Array.Empty<byte>(),
                b.X, b.Y, b.Width, b.Height, 1.0, m.Count, "screenScrape", warns); });

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0));
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
        Assert.False(scraped, "a proven-stale mask set must never be painted onto a scrape");
    }

    // A window whose masked content animates continuously never settles. Window scope with masks refuses
    // -- the SAME terminal outcome as the resize case, reached by the other detector.
    [Fact]
    public async Task A_continuously_moving_mask_set_refuses_for_window_scope()
    {
        int n = 0;
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>(
            (_, _) => Task.FromResult(Geo(W, new Rectangle(10, 10 + (n++ * 7), 50, 20))));
        var c = new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(3, 1000),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
    }

    // ⚠ THE PROPERTY THAT KEEPS IT A GUARD RATHER THAN AN OUTAGE. A bookend that reports a difference for
    // a window nobody touched would refuse every masked capture on the machine. This is ratification item
    // 5's added measurement, expressed as a test.
    [Fact]
    public async Task A_static_window_never_trips_the_bookend()
    {
        var mask = new Rectangle(10, 10, 50, 20);
        var script = new List<CaptureGeometry>();
        for (int i = 0; i < 10; i++) script.Add(Geo(W, mask));
        var c = Make(script, out var walks);
        for (int i = 0; i < 5; i++)
        {
            var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
            Assert.Equal("printWindow", r.Result.CaptureMethod);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~BookendWalkTests"`
Expected: FAIL — `An_empty_mask_set_performs_no_second_walk` passes trivially, the rest fail because no second walk happens.

- [ ] **Step 3: Add step 9 to the coordinator**

Replace the `case CaptureOutcomeKind.Completed:` arm in `CaptureAsync` with:

```csharp
                case CaptureOutcomeKind.Completed:
                {
                    // Step 9. THE BOOKEND VALIDATION WALK (§2.5). Closes the relayout leak: a window can
                    // reflow massively at a CONSTANT outer size -- an SPA navigating, an accordion
                    // opening, a splitter dragged -- so W1.Size == W2.Size holds, the resize guard never
                    // fires, and mask rects sampled during the walk are painted over the WRONG REGIONS of
                    // an image composed afterwards.
                    //
                    // ⚠ IT DOES NOT COVER RISK 3's STALE COMPOSITION. There the pixels are older than the
                    // tree and a value can be revealed IN PLACE, leaving the element's rect mathematically
                    // identical. M1 == M2 and this walk passes a genuinely stale capture. It closes the
                    // RELAYOUT leak and nothing else.
                    //
                    // ⚠ It CANNOT run between steps 6b and 7 where it would be cheapest: those are both
                    // inside CaptureWindow, and the walk lives out here. So a mismatch WASTES a full
                    // encode. Accepted -- the mismatch is the rare path, and the alternative is giving the
                    // seam the ability to walk the UIA tree.
                    //
                    // ⚠ An EMPTY mask set skips the walk entirely. Nothing could have gone stale, and this
                    // is what keeps the common case free.
                    if (geo.MaskRects.Count == 0)
                        return new WindowCaptureOutcome(outcome.Result!, geo);

                    // ⚠ THE BOOKEND WALK IS GUARDED, AND UNGUARDED IT TURNED SUCCESS INTO A REFUSAL.
                    // This walk can throw for the same reasons the first one can -- a tearing-down window
                    // raises RedactionUnmaskable from the mask sweep. Letting that escape would discard a
                    // GOOD image that is already in hand, and would introduce a terminal refusal on
                    // ELEMENT scope, which the design states has none.
                    //
                    // A walk that fails is treated as a MISMATCH, not as an error: we could not confirm
                    // the mask set survived the capture, and "could not confirm" must not read as
                    // "confirmed". The retry then re-walks, and on exhaustion the scope's own terminal
                    // outcome applies -- window-with-masks refuses, element falls back. If the window is
                    // genuinely gone, the NEXT attempt's step-1 walk throws and that one is deliberately
                    // unguarded, so the agent still learns the target died.
                    bool confirmed;
                    try
                    {
                        var after = await _walk(handle, @ref);
                        confirmed = MaskSetsMatch(geo, after);
                    }
                    catch (ToolException) { confirmed = false; }

                    if (confirmed)
                        return new WindowCaptureOutcome(outcome.Result!, geo);

                    if (attempt < _opts.MaxAttempts) continue;

                    // ⚠⚠ A BOOKEND EXHAUSTION REFUSES ON **BOTH** SCOPES, and it must NOT be routed
                    // through OnResizeExhausted. That method falls back to the scrape for element scope,
                    // which is correct for a RESIZE -- there the failure is a geometry mismatch and the
                    // masks may be perfectly fine. It is WRONG here.
                    //
                    // A bookend mismatch is DIRECT EVIDENCE that the mask set moved under the capture.
                    // Falling back would scrape the window and paint those same rects -- rects we have
                    // just PROVEN are stale -- producing an under-redacted image. That is the failure
                    // class SP4 exists to close, and the argument is the identical one that makes window
                    // scope refuse rather than scrape.
                    //
                    // Note this needs no mask-set test: the bookend only runs when M1 is non-empty.
                    throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                        "The window's redacted regions kept moving during the capture, so they cannot be " +
                        "reliably located in the image.",
                        "wait for the window to settle, then retry, or capture a different window");
                }
```

Add the comparison as a private static member:

```csharp
    /// <summary>M1 vs M2, as an ORDERED SEQUENCE of WINDOW-RELATIVE rectangles.
    ///
    /// ⚠⚠ WINDOW-RELATIVE, NOT ABSOLUTE, AND THAT IS THE WHOLE CORRECTNESS OF THIS GUARD. Mask rects are
    /// absolute SCREEN coordinates. Comparing them absolutely means a user DRAGGING the window between
    /// the two walks shifts every rect, the bookend reports a mismatch, and a harmless move is treated as
    /// an internal reflow -- retried, and on exhaustion REFUSED. The design states in three separate
    /// places that a pure move is harmless and must not be flagged, so an absolute comparison contradicts
    /// it directly. Normalising each list against ITS OWN walk's window origin isolates internal layout
    /// from window position, which is the only thing this guard is trying to see.
    /// *(AGY-AFTER panel over this plan, round 1, Type-Flow Auditor. The bookend walk is not panel-tested
    /// -- it postdates the spec's thirty rounds -- and this was the first defect found in it.)*
    ///
    /// Ordered rather than as a set because the walk is deterministic: it enumerates roots and descendants
    /// in a fixed order, so a REORDERING is itself evidence the tree changed under the capture.
    ///
    /// A window RESIZE between the two walks can also produce a mismatch here. That is correct and not
    /// double-handling: a resize genuinely may have reflowed the content, and the terminal outcome is the
    /// same one the resize rule would reach.</summary>
    private static bool MaskSetsMatch(CaptureGeometry before, CaptureGeometry after)
    {
        var a = before.MaskRects;
        var b = after.MaskRects;
        if (a.Count != b.Count) return false;

        var oa = before.WindowBounds.Location;
        var ob = after.WindowBounds.Location;

        // ⚠ ORDER-INSENSITIVE. An earlier version compared by index, on the reasoning that the walk is
        // deterministic so a reordering would itself be evidence the tree changed. That reasoning is
        // wrong twice over: UIA enumeration order across two walks is not a guarantee this repo owns, and
        // -- more importantly -- a REORDERING WITH IDENTICAL GEOMETRY IS NOT A REFLOW. The question this
        // guard asks is "do the masks still cover the same regions", and the answer does not depend on
        // the order the walk happened to return them in. Comparing by index only added a false-refusal
        // mode. *(AGY-AFTER panel over this plan, round 4, direct answer 1.)*
        static (int X, int Y, int W, int H) Key(Rectangle r, Point o)
            => (r.X - o.X, r.Y - o.Y, r.Width, r.Height);

        var ka = a.Select(r => Key(r, oa)).OrderBy(k => k.X).ThenBy(k => k.Y)
                                          .ThenBy(k => k.W).ThenBy(k => k.H).ToList();
        var kb = b.Select(r => Key(r, ob)).OrderBy(k => k.X).ThenBy(k => k.Y)
                                          .ThenBy(k => k.W).ThenBy(k => k.H).ToList();
        for (int i = 0; i < ka.Count; i++)
            if (ka[i] != kb[i]) return false;
        return true;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~BookendWalkTests"`
Expected: PASS — 5 passed.

- [ ] **Step 5: Prove the gates are non-vacuous with two logic mutants**

1. Change `MaskSetsMatch` to `=> true`.
   Expected: `A_mask_that_moved_under_the_capture_triggers_a_retry` FAILS at `Assert.Equal(4, walks())` with 2 — the leak is open again, and seeing this fail once is the point.
2. Delete the `if (geo.MaskRects.Count == 0)` short circuit.
   Expected: `An_empty_mask_set_performs_no_second_walk` FAILS with 2 walks.

**Revert both.**

- [ ] **Step 6: Run the whole headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed, build 0/0.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs test/FlaUI.Mcp.Tests/Perception/BookendWalkTests.cs
git commit -m "feat(capture): the bookend validation walk - closes the relayout leak (ratification 4)

Closes the leak that made the spec's 'none is paid in a leak' claim false:
a window can reflow at a constant outer size, so the resize guard never
fires and stale masks land on the wrong regions. Compares the mask list
before and after the capture; empty mask set skips it entirely.

Does NOT cover risk 3's stale composition - a value revealed IN PLACE
leaves the rect identical. That risk remains unmitigated and one-sided."
```

### Task 19: The per-HWND circuit breaker

**Build this only if Task 1 measured a block.** If it did not, skip the task and record the skip in the measurements doc.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureCircuitBreakerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureCircuitBreakerTests
{
    private static readonly Rectangle W = new(0, 0, 400, 300);

    private static CaptureGeometry Geo() =>
        new(W, Array.Empty<Rectangle>(), false, false, null, Array.Empty<MaskEscalationEntry>(),
            W, new IntPtr(0xBEEF), false);

    // N captures of a hung window must cost ONE leaked acquisition, not N. This CONTAINS the cumulative
    // degradation; it does NOT reclaim anything -- the first blocked call still holds its thread, its HDC
    // and both bitmaps forever. Only killing a separate process reclaims, and that is ROADMAP debt.
    [Fact]
    public async Task A_window_that_timed_out_is_not_retried_through_PrintWindow_during_the_cooldown()
    {
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(cooldown: TimeSpan.FromMinutes(5), clock: () => DateTime.UtcNow);
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(Geo()), src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        for (int i = 0; i < 5; i++)
        {
            var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
            Assert.Equal("screenScrape", r.Result.CaptureMethod);
            Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetUnresponsive");
        }

        Assert.Equal(1, src.Calls);   // ONE leak, not five
    }

    [Fact]
    public async Task The_breaker_reopens_after_the_cooldown()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => now);
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(Geo()), src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(1, src.Calls);
        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(1, src.Calls);           // still tripped

        now = now.AddMinutes(6);
        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(2, src.Calls);           // reopened, and leaked once more
    }

    // ⚠ THE DICTIONARY MUST NOT GROW WITHOUT BOUND. Every window that ever hung would otherwise leave a
    // permanent entry in a server designed to run for weeks — and because the OS RECYCLES HWND values, a
    // stale entry can mis-trip the breaker for an unrelated window that reuses the handle.
    [Fact]
    public void Expired_entries_are_pruned_so_the_breaker_does_not_grow_without_bound()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => now);

        for (int i = 1; i <= 50; i++) breaker.Trip(new IntPtr(i));
        Assert.Equal(50, breaker.TrackedCount);

        now = now.AddMinutes(6);          // every existing entry is now expired
        breaker.Trip(new IntPtr(9999));   // pruning happens on write
        Assert.Equal(1, breaker.TrackedCount);
        Assert.True(breaker.IsTripped(new IntPtr(9999)));
        Assert.False(breaker.IsTripped(new IntPtr(1)));
    }

    // ⚠⚠ THE BREAKER MUST NOT SMUGGLE A DEAD TARGET PAST THE GUARDS. A hung window trips the breaker,
    // then minimizes. Short-circuiting straight to the scrape skips canonical steps 4-5, so the scrape
    // photographs the rectangle the window USED to occupy and returns whatever is now behind it -- as a
    // success. That is the exact defect item 8 exists to remove, reintroduced by the containment added
    // for a different problem.
    [Fact]
    public async Task A_tripped_breaker_still_refuses_a_window_that_minimized()
    {
        var W = new Rectangle(0, 0, 400, 300);
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => DateTime.UtcNow);
        bool minimized = false;
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(W, Array.Empty<Rectangle>(), false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(0xBEEF), false)),
            src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => minimized,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        // First capture times out and trips the breaker.
        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(1, src.Calls);

        // Now the window minimizes. The breaker is tripped, so PrintWindow is skipped -- but the target
        // is gone from the screen and the scrape must NOT run.
        minimized = true;
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
        Assert.Equal(1, src.Calls);   // still short-circuited; it refused rather than scraping
    }

    // ⚠⚠ THE BREAKER MUST BOUND CONCURRENT CAPTURES, NOT ONLY SERIALIZED ONES. Trip() runs AFTER a
    // timeout elapses, so overlapping requests for the same hung window would all read IsTripped as
    // false, all call Acquire, all block, and all leak -- N threads and N bitmaps for one window,
    // defeating the containment this class exists to provide.
    [Fact]
    public async Task Overlapping_captures_of_one_hung_window_cost_ONE_acquisition_not_N()
    {
        var W = new Rectangle(0, 0, 400, 300);
        // Blocks until released, so all three requests genuinely overlap.
        var gate = new System.Threading.ManualResetEventSlim(false);
        int calls = 0;
        var src = new FakeWindowImageSource(_ =>
        {
            System.Threading.Interlocked.Increment(ref calls);
            gate.Wait(5000);
            return null;                       // then reports a timeout
        });

        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => now);
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(W, Array.Empty<Rectangle>(), false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(0xBEEF), false)),
            src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        var first = c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        // Let the first acquisition outlive the whole timeout budget, so it is KNOWN stuck.
        while (System.Threading.Volatile.Read(ref calls) == 0) await Task.Yield();
        now = now.AddMilliseconds(500);

        var second = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        var third  = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);

        Assert.Equal("screenScrape", second.Result.CaptureMethod);
        Assert.Equal("screenScrape", third.Result.CaptureMethod);
        Assert.Equal(1, System.Threading.Volatile.Read(ref calls));   // ONE acquisition, not three

        gate.Set();
        await first;
    }

    [Fact]
    public async Task A_different_window_is_unaffected()
    {
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => DateTime.UtcNow);
        int hwnd = 1;
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(W, Array.Empty<Rectangle>(), false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(hwnd), false)),
            src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        hwnd = 2;
        await c.CaptureAsync(new WindowHandle("w2"), null, CaptureScope.Window, 0);
        Assert.Equal(2, src.Calls);   // the breaker is PER-HWND
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureCircuitBreakerTests"`
Expected: FAIL — `CaptureCircuitBreaker` does not exist.

- [ ] **Step 3: Write the breaker**

Append to `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`:

```csharp
/// <summary>Remembers windows whose PrintWindow acquisition timed out, and routes subsequent captures of
/// those windows straight to the scrape for a cooldown.
///
/// ⚠ THIS CONTAINS; IT DOES NOT RECLAIM. Each blocked call keeps its thread, its HDC, its GDI bitmap and
/// its managed bitmap permanently -- a blocked Win32 call cannot be cancelled, so nothing in-process can
/// take them back. What this bounds is the MULTIPLIER: N captures of a hung window cost ONE leak instead
/// of N. Operator ratification of 2026-08-21 accepted that trade explicitly; the out-of-process worker
/// that would actually reclaim is filed as ROADMAP debt.</summary>
public sealed class CaptureCircuitBreaker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, DateTime> _tripped = new();
    // ⚠ IN-FLIGHT ACQUISITIONS, and without this the breaker bounds only SERIALIZED captures. Trip() runs
    // AFTER a timeout elapses, so three overlapping requests for the same hung window all read IsTripped
    // as false, all call Acquire, all block, and all leak -- three threads and three bitmaps for one
    // window, defeating the containment this class exists to provide.
    // *(AGY-AFTER panel over this plan, round 3, State Corruptor.)*
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, DateTime> _inFlight = new();
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTime> _clock;

    public CaptureCircuitBreaker(TimeSpan cooldown, Func<DateTime> clock)
    { _cooldown = cooldown; _clock = clock; }

    public static CaptureCircuitBreaker Default => new(TimeSpan.FromMinutes(5), () => DateTime.UtcNow);

    /// <summary>How many windows are currently tracked. Exists so a test can prove the dictionary is
    /// pruned rather than growing forever — the growth is otherwise invisible until it matters.</summary>
    public int TrackedCount => _tripped.Count;

    public bool IsTripped(IntPtr hwnd)
        => _tripped.TryGetValue(hwnd, out var at) && _clock() - at < _cooldown;

    /// <summary>TRUE when another acquisition for this window has ALREADY outlived <paramref name="budget"/>
    /// and is therefore known to be blocked -- so this call would block too, for the same reason, and add
    /// one more permanent leak.
    ///
    /// ⚠ It deliberately does NOT divert merely because another acquisition is in flight. Two agents
    /// capturing the same HEALTHY window concurrently is ordinary, and those calls finish in milliseconds;
    /// diverting them to a scrape would reintroduce occlusion for a window that was working fine. Only an
    /// acquisition that has already exceeded the whole timeout budget is evidence of a hang.</summary>
    public bool AnotherAcquisitionIsStuck(IntPtr hwnd, TimeSpan budget)
        => _inFlight.TryGetValue(hwnd, out var started) && _clock() - started > budget;

    /// <summary>Records the start of an acquisition. Keeps the EARLIEST start for a window, so a stream of
    /// overlapping requests cannot keep pushing the "stuck" judgement into the future.</summary>
    public void BeginAcquisition(IntPtr hwnd) => _inFlight.TryAdd(hwnd, _clock());

    /// <summary>Clears the in-flight marker. Safe to call for a request that TIMED OUT: the abandoned
    /// thread is still blocked, but Trip() has by then recorded the window and the cooldown takes over.</summary>
    public void EndAcquisition(IntPtr hwnd) => _inFlight.TryRemove(hwnd, out _);

    /// <summary>Forget a window entirely -- called when the OS reports it is no longer hung, so a target
    /// that recovered stops being penalised for having hung once.</summary>
    public void Reset(IntPtr hwnd)
    {
        _tripped.TryRemove(hwnd, out _);
        _inFlight.TryRemove(hwnd, out _);
    }

    public void Trip(IntPtr hwnd)
    {
        // ⚠ PRUNE ON WRITE. Without this the dictionary is UNBOUNDED: every window that ever hung leaves
        // a permanent entry, in a server designed to run for weeks. The entries are tiny, so this is not
        // the leak that matters -- but a subproject whose entire subject is not leaking must not ship a
        // collection that only grows, and HWNDs are recycled by the OS, so a stale entry can also
        // mis-trip the breaker for an unrelated window that happens to reuse the handle value.
        //
        // Pruning on Trip rather than on a timer keeps this allocation-free in the common case: Trip only
        // runs when a capture actually timed out, which is rare by construction.
        var now = _clock();
        foreach (var kv in _tripped)
            if (now - kv.Value >= _cooldown) _tripped.TryRemove(kv.Key, out _);
        _tripped[hwnd] = now;
    }
}
```

- [ ] **Step 4: Wire it into the coordinator**

Add a `CaptureCircuitBreaker? breaker = null` constructor parameter stored as `_breaker`, then:

Before the `ScreenCapture.CaptureWindow` call:

```csharp
            // The breaker short-circuits BEFORE acquisition, which is the whole point: the leak happens
            // inside Acquire, so avoiding the call is the only way to avoid the leak.
            // Two conditions divert to the scrape, and they cover different windows in time: the breaker
            // covers everything AFTER a timeout was observed, the in-flight check covers the gap DURING
            // the first request, before any timeout has been recorded.
            // ⚠⚠ AND THE BREAKER ASKS WHETHER THE TARGET IS STILL HUNG BEFORE IT DIVERTS. Without this
            // the path emits `scrapeFallbackTargetUnresponsive`, whose recourse tells the agent in the
            // PRESENT TENSE that "the target is not pumping messages: it will not respond to input
            // either, so do not queue clicks against it" -- on the strength of a timeout that may be
            // almost five minutes old. An app that hung once and recovered would have every capture
            // degraded to a scrape, and every one of them labelled with a false statement about it.
            //
            // `IsHungAppWindow` is the OS's own answer to this question -- it is what Task Manager uses --
            // and it does not block on the target's message loop, so asking is safe on precisely the
            // window we are avoiding. A recovered window RESETS the breaker and takes the normal path, so
            // the cooldown becomes a bound on how long a STILL-hung window is skipped rather than a flat
            // penalty for having hung once.
            // *(Driver's solo Guard-Consistency pass, round 5: a warning whose text is false of the image
            // it annotates is the same defect class as a guard that disagrees with its neighbour.)*
            if (_breaker is not null
                && (_breaker.IsTripped(geo.NativeWindowHandle)
                    || _breaker.AnotherAcquisitionIsStuck(geo.NativeWindowHandle,
                                                          TimeSpan.FromMilliseconds(_opts.TimeoutMs)))
                && _isHung(geo.NativeWindowHandle))
            {
                // ⚠⚠ THE TARGET-STATE GUARDS STILL RUN. Skipping straight to the scrape also skips
                // canonical steps 4-5, which live inside CaptureWindow -- and those are the guards that
                // stop a DEAD or MINIMIZED window being photographed at the rectangle it used to occupy.
                //
                // The reachable defect: a hung window trips the breaker, then minimizes. Without this
                // call the next capture scrapes its old rect and returns a photograph of whatever is now
                // behind it -- confidently, as a success. That is precisely the failure item 8 exists to
                // remove, reintroduced by the mechanism added to contain a different problem.
                //
                // These guards are about the TARGET's state, not about the backend, so every path that is
                // about to photograph a named window owes them.
                var bw2 = ScreenCapture.GuardTargetState(geo.NativeWindowHandle, _w2Probe, _minimizedProbe);
                // Degeneracy is retryable here for the same reason it is inside the seam, and scraping a
                // window with no renderable area would photograph whatever now occupies its old rect.
                if (bw2.Width <= 0 || bw2.Height <= 0)
                {
                    if (attempt < _opts.MaxAttempts) continue;
                    throw new ToolException(ToolErrorCode.ElementNotActionable,
                        "The target window reported no renderable area on every attempt.",
                        "restore or resize the window, then retry");
                }
                // ⚠⚠ AND THE RESIZE CHECK. Skipping the seam also skips `if (w1.Size != w2.Size)`, so a
                // hung window that RECOVERS and resizes during the cooldown would be scraped with stale
                // W1 masks and never checked -- bypassing the exact protection the seam path enforces.
                // *(AGY-AFTER panel over this plan, round 4, Guard-Consistency Auditor.)*
                if (geo.WindowBounds.Size != bw2.Size && geo.MaskRects.Count > 0)
                    throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                        "The window changed size, so its redacted regions cannot be reliably located.",
                        "wait for the window to settle, then retry, or capture a different window");

                return new WindowCaptureOutcome(
                    await ScrapeAsync(geo, scope, maxWidth, warnings,
                                      CaptureWarnings.ScrapeFallbackTargetUnresponsive),
                    geo);
            }
```

In the `TimedOut` arm, before returning:

```csharp
                case CaptureOutcomeKind.TimedOut:
                    _breaker?.Trip(geo.NativeWindowHandle);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureCircuitBreakerTests"`
Expected: PASS — 3 passed.

- [ ] **Step 6: Prove the gate is non-vacuous with a logic mutant**

Change `IsTripped` to `=> false`.
Expected: `A_window_that_timed_out_is_not_retried_through_PrintWindow_during_the_cooldown` FAILS with `src.Calls == 5` — five leaks instead of one. **Revert.**

- [ ] **Step 7: File the out-of-process worker as ROADMAP debt**

Append to `ROADMAP.md`:

```markdown
### 17. A hung-window `PrintWindow` capture leaks permanently — the containments bound it, nothing reclaims it

`PrintWindow` sends `WM_PRINT` synchronously to the target, so a target whose message loop is blocked
blocks the call, and a blocked Win32 call cannot be cancelled. Item 8 ships two CONTAINMENTS — a dedicated
background thread so the leak is a thread rather than a CLR threadpool slot, and a per-HWND circuit
breaker so N captures of a hung window cost one leak rather than N. **Neither reclaims anything:** the
blocked call keeps its thread, its HDC, its GDI bitmap and its managed bitmap until the process exits.

The fix that DOES reclaim is running the acquisition in a sacrificial out-of-process worker terminated on
timeout, letting the OS take the handles back. It costs IPC, bitmap serialization across a process
boundary, child-process lifetime management, and a second DPI-aware CLR process that must be on the right
desktop and session. Deliberately not built in item 8 — see that spec's ratification item 3, where the
operator accepted the containments and staged this.

Build it if the containments prove insufficient in practice.
```

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs test/FlaUI.Mcp.Tests/Perception/CaptureCircuitBreakerTests.cs ROADMAP.md
git commit -m "feat(capture): per-HWND circuit breaker; file the out-of-process worker as ROADMAP item 17"
```

---

## Phase 6 — Wiring, the wire contract, the audit signal, and the gates

### Task 20: Wire `ScreenshotTools` to the coordinator, and pin the metadata projection

**Failure mode 3 of the three the spec names as having no test that would go red:** nothing asserts the metadata projection's shape, so a field silently dropped from the anonymous object reaches an agent as an absent field it has no way to notice. The precedent to follow is `test/FlaUI.Mcp.Tests/Perception/ListWindowsProjectionShapeTests.cs`.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:30-84`
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
                escalations = outcome.Geometry.Escalations;
                unmaskedProcesses = System.Array.Empty<string>();

                // §2.6, canonical step 10. AFTER the result is final, never before: a signal raised
                // earlier can appear in the captured pixels on the scrape path.
                _auditSignal.SignalIfOcclusionBypassed(new WindowHandle(window!),
                                                       outcome.Geometry.NativeWindowHandle,
                                                       result.CaptureMethod);
            }
```

- [ ] **Step 5: Extend the metadata projection**

Replace lines 71–84's `return ToolResponse.Image(...)` object with:

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
        var projection = File.ReadAllText(Path.Combine(root, "src", "FlaUI.Mcp.Server", "Tools", "ScreenshotTools.cs"));
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
        var code = StripComments(projection);
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
        breaker: sp.GetRequiredService<FlaUI.Mcp.Core.Perception.CaptureCircuitBreaker>());
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
Expected: PASS, 0 failed, build 0/0.

- [ ] **Step 9: Prove the gate is non-vacuous with a logic mutant**

1. Delete `captureMethod = result.CaptureMethod,` from the projection.
   Expected: `The_tool_description_enumerates_exactly_the_fields_the_projection_emits` FAILS on the second half — *"the tool description promises 'captureMethod' but the metadata projection never assigns it"*.
2. **Comment it out** rather than deleting it: `// captureMethod = result.CaptureMethod,`.
   Expected: it FAILS the same way. **This mutant is the important one.** Item 12 shipped a sweep that passed against a commented-out property — the anti-gaming half of that feature was defeatable by typing `<!--`. If this mutant does NOT go red, the regex is matching the comment and the tripwire is worthless.

**Revert both.**

- [ ] **Step 10: Commit**

```bash
git add src/FlaUI.Mcp.Server/ test/FlaUI.Mcp.Tests/Perception/ScreenshotProjectionShapeTests.cs
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
- Modify: `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:17`
- Modify: `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs:11-14`

- [ ] **Step 1: Edit 1 — the metadata enumeration**

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
Window/element scope captures the window's OWN pixels even when it is covered or off-screen (captureMethod:'printWindow'); full-desktop scrapes the screen (captureMethod:'screenScrape'). IMPORTANT: when captureMethod is 'printWindow' the image coordinates are NOT clickable - the pixels may be behind another window or on no monitor at all, so a click computed from bounds lands on whatever is drawn there instead. Act through the UIA tree (desktop_snapshot + desktop_click ref) rather than mapping image coordinates back to the screen. captureWarnings is ALWAYS present and EMPTY when nothing is wrong; each entry is {code,recourse} where code is stable to branch on - uniformCanvas, elementCanvasUniform, desktopCanvasUniform, windowResized, popupsNotRendered, scrapeFallbackTargetUnresponsive, scrapeFallbackTargetChanging. A window that keeps resizing is retried briefly (worst case ~4.5s) and then either falls back to a scrape (element scope) or is refused with RedactionUnmaskable (window scope with redactable content).
```

- [ ] **Step 4: Edit 4 — the class doc**

Replace `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs` lines 11–14 with:

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
Expected: PASS — including Task 20's description/projection agreement test, which is what makes Step 1 non-optional.

- [ ] **Step 6: Prove the gate is non-vacuous with a logic mutant**

Revert Step 1's enumeration to the old seven-field list.
Expected: `The_tool_description_enumerates_exactly_the_fields_the_projection_emits` FAILS. **Revert the mutant.**

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs
git commit -m "docs(capture): tool description and class doc - three edits plus the stale focus claim"
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

## AGY-AFTER panel over this plan — round 1

Brief `.clavity/seams/item8-plan-panel.md`; report `.clavity/scratch/item8-plan-panel/agy-round1.md`.
Seats: Type-Flow Auditor, Literal Implementer, Mechanism Gamer. **Verdict: RED.** Five findings, of which
**three were folded and two were REFUTED BY MEASUREMENT.**

**Folded:**

1. **The bookend walk compared ABSOLUTE mask rectangles, so a pure window MOVE tripped it.** Mask rects
   are absolute screen coordinates; dragging the window between the two walks shifts every one of them, so
   a harmless drag read as a total relayout — retried, then REFUSED. The design states in three separate
   places that a pure move is harmless, so the comparison contradicted it directly. Now normalised against
   each walk's own window origin, with `A_pure_window_move_between_the_two_walks_does_not_trip_the_bookend`
   pinning it. **This was the first defect found in §2.5, which is the least-reviewed idea in the design.**
2. **The metadata sweep's regex matched COMMENTED-OUT code**, so the tripwire was defeated by typing two
   slashes — *the exact defect item 12 shipped, reappearing inside the very test written to prevent it.*
   MEASURED: `\bcaptureMethod\s*=` matches `// captureMethod = result.CaptureMethod,`. Comments are now
   stripped before matching. **The severity here is not the regex; it is that an anti-gaming guard was
   authored, reviewed, and shipped in a plan while being trivially gameable.**
3. **The sweep reads SOURCE, never the runtime shape.** A serialization-level defect would pass both
   halves. Task 24 gains a runtime assertion over the JSON the tool actually emits — placed there because
   it is the only task with a live desktop.

**Refuted by measurement, do NOT re-raise:**

4. **"Task 21 provides no logic mutants and stops at Step 6."** FALSE. Task 21 has **Step 7, "Prove the
   gates are non-vacuous with two logic mutants"**, followed by Step 8's commit. Verified by reading the
   task.
5. **"The plan invents a DEF-2 defect it did not verify: `ScreenshotTools.cs:49` already passes
   `desk.Rects`."** The line does pass `desk.Rects` — and so does the plan. The `// DEF-2: this passed
   Array.Empty<Rectangle>()` comment is **pre-existing source at `ScreenshotTools.cs:42-45`**, written in
   the PAST TENSE, recording a defect that was fixed. The plan reproduces it verbatim because preserving
   an existing comment is correct. The peer read a historical note as a present-tense claim.

**Already fixed before the report arrived:** the Literal Implementer's finding that Task 20 references
`CaptureAuditSignal` before Task 21 creates it. The driver's own solo pass caught it in `c5bc0b2`;
independent agreement is worth recording even though there was nothing left to fold.

⚠ **The round is RED and the panel is NOT closed.** Three folds spawn their own edges — the corrected
bookend comparison in particular is new code that has been reviewed by nobody. A further round should be
run against the folded plan before Task 1 begins.

## AGY-AFTER panel over this plan — round 2

Brief `.clavity/seams/item8-plan-panel-r2.md`; report `.clavity/scratch/item8-plan-panel/agy-round2.md`.
Seats rotated onto uncovered ground: Fold Auditor (round 1's edits only), Cascade Analyst, Resource
Vampire. **Verdict: RED.** Plus a driver solo pass that ran before the peer's report returned.

**Folded — from the peer:**

1. **The abandoned acquisition thread leaked an undisposed managed `Bitmap`.** If a hung target eventually
   processes `WM_PRINT`, the abandoned thread finishes `Render()` and allocates a `Bitmap` wrapping GDI+
   resources for a caller that returned long ago. Those accumulate against the process's 10,000-handle GDI
   ceiling, reclaimed only when the GC gets round to finalizing them. **The thread now disposes its own
   output when the caller has given up.** This does not make the accepted leak go away — what is held
   inside a still-blocked call is unreachable either way — it removes the one resource that becomes
   reclaimable afterwards and was being dropped anyway.
2. **Task 20's step ordering was broken for a literal implementer.** Step 3 expected a test to be RED that
   Step 6 had not yet written, while Step 6's own header claimed it was "already written in Step 2". Both
   are now correct: Step 2 writes two literal tests, Step 6 adds the real tripwire, and Step 3 says so.

**Folded — from the driver's solo pass, before the peer's report arrived:**

3. **The bookend walk was unguarded** — a throwing confirmation discarded a good image already in hand and
   introduced a terminal refusal on element scope. *(The peer's Cascade Analyst found this independently.)*
4. **The circuit breaker's dictionary was unbounded**, and because the OS recycles `HWND` values a stale
   entry could mis-trip an unrelated window. *(The peer flagged the growth as a secondary finding.)*
5. **The circuit breaker smuggled a dead target past the target-state guards.** Short-circuiting to the
   scrape skipped canonical steps 4-5, so a hung window that tripped the breaker and then MINIMIZED was
   scraped at the rectangle it used to occupy — returning a photograph of whatever was now behind it, as a
   success. **That is the exact defect item 8 exists to remove, reintroduced by the containment added for a
   different problem.** Steps 4-5 are now `ScreenCapture.GuardTargetState`, called by both paths.

**REFUTED BY MEASUREMENT — do NOT re-raise:**

6. **"A pure move leaves masks spatially offset inside the image, so `MaskSetsMatch` approving a move
   returns a leaking image."** FALSE, and traced on the spec's own numbers: window `(-8,-8,1936,1036)` →
   `(92,92,…)`, element `(100,200,300,50)`, mask `(110,210,50,20)`. The crop yields `absolute =
   (100,200,300,50)` and the mask paints at `(10,10)` — exactly its true offset inside the element. The
   claim assumes `c.Absolute` is `W2`-anchored; it is **`W1`-anchored**, which is §1's rule 1 and what
   panel round 7 verified by hand. Masks and the rectangle that translates them come from the same
   observation, which is the entire point of that rule.
7. **"Task 24's `OccludedCaptureTests.cs` must be invented from scratch — namespace, usings, class
   declaration and the `_app` fixture must all be guessed."** FALSE. Task 24 gives the complete file:
   `namespace FlaUI.Mcp.Tests.Capture`, `public class OccludedCaptureTests : IClassFixture<TestAppFixture>`,
   the `_app` field and its constructor. The instruction the finding quotes does not appear in the plan.
8. **"The plan asserts `PerceptionPolicy.cs:48` reads `string.IsNullOrWhiteSpace(processName) || …`."**
   FALSE. The plan mentions `PerceptionPolicy` **zero times**.
9. **"`Autosound` being declared and parsed in `ServerOptions.cs` is unverified."** It is verified:
   `ServerOptions.cs:11` declares `bool Autosound = false` and `:18` parses `args.Contains("--autosound")`.
   The plan also instructs the engineer to read that file rather than copy an idiom.

⚠ **Round 2 is RED and the panel is NOT closed.** Five folds, and three of them are new code — the
extracted `GuardTargetState`, the guarded bookend, and the thread handoff. In this project a fix has
carried its own defect in a large fraction of rounds.

⚠ **The pattern across both rounds is worth stating for whoever runs round 3: every defect so far has been
in a GUARD, not in the happy path.** The bookend contradicting the move rule, the anti-gaming sweep being
gameable, the breaker smuggling a dead target past the guards, the containment leaking the thing it was
containing. The common case has been correct throughout; the machinery added to protect it has not.

## AGY-AFTER panel over this plan — round 3

Brief `.clavity/seams/item8-plan-panel-r3.md`; report `.clavity/scratch/item8-plan-panel/agy-round3.md`.
Seats: Fold Auditor (round 2's edits only), State Corruptor, Axiom Breaker. **Verdict: RED.**

**Folded:**

1. **The circuit breaker bounded only SERIALIZED captures.** `Trip()` runs *after* a timeout elapses, so
   three overlapping requests for the same hung window all read `IsTripped` as false, all call `Acquire`,
   all block, and all leak — three threads and three bitmaps for one window, defeating the containment the
   class exists to provide. Added an IN-FLIGHT register and `AnotherAcquisitionIsStuck(hwnd, budget)`.
   ⚠ It deliberately does **not** divert merely because another acquisition is in flight: two agents
   capturing the same HEALTHY window concurrently is ordinary and those calls finish in milliseconds, so
   diverting them would reintroduce occlusion for a window that was working fine. Only an acquisition that
   has already outlived the whole timeout budget counts as evidence of a hang. *(State Corruptor.)*
2. **A degenerate `W2` threw while a degenerate `W1` was a retryable transient — the same physical
   condition, terminal or recoverable purely according to which of two reads milliseconds apart caught
   it.** That defeated the retry loop for exactly the animating window it was built for. `CaptureOutcome`
   gains a fourth case, `TargetTransient`; `GuardTargetState` now raises only the genuinely terminal
   conditions (destroyed, minimized) and each caller routes degeneracy into the loop. *(Axiom Breaker —
   the seat's first outing, and it found a contradiction three rounds had walked past.)*
3. **The acquisition-thread handoff race.** *Already folded at `3433cf0` by the driver's own solo pass
   before this report arrived; the peer's Fold Auditor found it independently.* Setting `abandoned` alone
   narrows the window without closing it — the thread can publish into `result` between `Join` expiring
   and the caller taking the lock. Both orderings are now covered.

**Also folded this round, from the driver's solo Axiom Breaker pass — and it is leak-class:**

4. **A bookend exhaustion was routed through `OnResizeExhausted`, which FALLS BACK TO THE SCRAPE for
   element scope.** That is correct for a RESIZE, where the failure is a geometry mismatch and the masks
   may be perfectly fine. It is wrong for a bookend mismatch, which is *direct evidence the mask set
   moved*: falling back would scrape the window and paint rects we have just PROVEN are stale, producing
   an under-redacted image. **A bookend exhaustion now refuses on BOTH scopes**, which is what the spec's
   §4 table said all along — the code contradicted it and the spec was right.

**REFUTED BY MEASUREMENT — do NOT re-raise:**

5. *"The plan asserts `AutomationDispatcher.cs:61` is `_query.RunAsync(func)` with no timeout…"* The plan
   mentions `AutomationDispatcher` **zero times**. (The claim is also TRUE of the real file, which makes
   the pre-existing comment it came from accurate.) **This is the THIRD round running in which a direct
   answer attributed to the plan an assertion the plan does not make** — grep every quoted claim.

⚠ **Round 3 is RED. Four folds, three of them new code:** the in-flight register, the `TargetTransient`
case, and the bookend's own terminal outcome.

⚠ **The guards-only pattern held for a third round** — every one of the four is in protective machinery.
But note the shift: rounds 1-2 found guards that were WRONG, round 3 found guards that were INCONSISTENT
WITH EACH OTHER. `W1` versus `W2` degeneracy, and bookend-exhaustion versus resize-exhaustion, are both
"two guards for the same class of condition that disagree". That is a different lens and it is not
exhausted — round 4 should enumerate every PAIR of guards and ask whether they agree.

## AGY-AFTER panel over this plan — round 4

Brief `.clavity/seams/item8-plan-panel-r4.md`; report `.clavity/scratch/item8-plan-panel/agy-round4.md`.
Seats: Guard-Consistency Auditor (bespoke), Protocol Pedant, Blindspot Auditor. **Verdict: RED.**

**Folded — two are leak-class:**

1. **⚠ LEAK. Element scope with a NON-EMPTY mask set fell back to the scrape on resize exhaustion.** A
   resize reflows the WINDOW's layout regardless of which scope asked, so painting pre-resize mask rects
   onto a post-resize scrape under-redacts exactly as it would for window scope — while window scope
   refuses for precisely that reason. The fallback was defended as "restoring current behaviour", which is
   the pre-existing-defect defence this project does not accept. **Element scope with masks now refuses
   too.** ⚠ The regression argument for the fallback survives intact because it was always about a
   different population: spinners, progress dialogs and expanding windows carry nothing to mask, so they
   still fall back. *(Guard-Consistency Auditor.)*
2. **⚠ LEAK. The circuit-breaker path bypassed the resize guard.** Skipping the seam also skips
   `if (w1.Size != w2.Size)`, so a hung window that RECOVERED and resized during the cooldown was scraped
   with stale `W1` masks and never checked. The breaker path now runs the resize check as well as
   `GuardTargetState`. *(Guard-Consistency Auditor.)*
3. **A window-scope FALLBACK SCRAPE that came back one colour emitted nothing** — recreating the exact
   defect the `uniformCanvas` widening was folded in to close. The TWO-STAGE detector genuinely cannot run
   on a scrape, but the FIRST stage needs no second operand. A window-scope fallback now emits
   `uniformCanvas`. ⚠ An ELEMENT-scope fallback still emits nothing, **deliberately**: there the captured
   region is the element, so the code's text would claim the whole window rendered as one colour — a
   statement the tool never measured. That gap is stated rather than papered over. *(Protocol Pedant. The
   spec has been corrected too — it said "a fallback scrape gets neither".)*
4. **`MaskSetsMatch` compared by INDEX.** Its justification was that the walk is deterministic so a
   reordering is itself evidence the tree changed. Wrong twice: UIA enumeration order across two walks is
   not a guarantee this repo owns, and **a reordering with identical geometry is not a reflow.** The
   question is whether the masks still cover the same regions, which does not depend on walk order. Now
   order-insensitive, removing a false-refusal mode. *(Direct answer 1 — correctly identified as the most
   likely surviving defect.)*

**Also folded this round, from the driver's solo Guard-Consistency pass:**

5. **The empty-crop refusal was terminal on the FIRST occurrence, while a degenerate `W1` was retryable.**
   Both are "UIA reported a bad rectangle for a frame". The design's own text calls the empty crop
   "pathological rather than impossible, this repo's mask-escalation machinery exists because **UIA does
   report inconsistent rectangles**" — which argues for absorbing it, not refusing it. Now a retryable
   transient carrying its own reason, so the terminal message is not the false "no renderable area".
   ⚠ **The peer had this in its BELOW-FLOOR list**, discarded as "rare and likely terminal". Reading the
   below-floor list first has now produced a real finding in this repository several times.

**Not folded:**

6. *"The audit signal floods the operator with false positives on visible background windows."* **Already
   argued in the plan**, in those words: the trigger over-signals deliberately, it never MISSES a covered
   window, computing true occlusion needs a hit-test this server does not do, **and that is exactly why it
   is OFF BY DEFAULT.** The finding proposes no better trigger. Recorded rather than folded.
7. *"Task 17's three call sites are unverified."* Verified this session by `grep -rn`.

⚠ **Round 4 is RED. Five folds, and the two leak-class ones were BOTH "a guard that exists on one path
and not on the adjacent one".** That is the same lens as round 3 and it is still producing.
