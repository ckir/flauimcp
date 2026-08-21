> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

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

- [ ] **Step 5b: Measure `IsHungAppWindow` against the SAME hung window — it costs nothing and closes an assumption**

⚠ **The circuit breaker calls `IsHungAppWindow` on a window it suspects is hung, and the plan asserts that call "does not block on the target's message loop, so asking is safe on precisely the window we are avoiding".** That is documented Win32 behaviour rather than a measured fact, and **if it were wrong the breaker would hang on exactly the window it exists to avoid hanging on** — turning a containment into the failure it contains. This project's standard is measured-not-assumed, and the fixture is already running from Step 4.

While the `HANGPROBE` window is still hung, run:

```powershell
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class HP { [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h); }
'@
$p = Get-Process | Where-Object { $_.MainWindowTitle -like '*HANGPROBE*' } | Select-Object -First 1
$sw = [Diagnostics.Stopwatch]::StartNew()
$hung = [HP]::IsHungAppWindow($p.MainWindowHandle)
$sw.Stop()
Write-Host "IsHungAppWindow=$hung elapsedMs=$($sw.ElapsedMilliseconds)"
```

**Expected: `IsHungAppWindow=True` with `elapsedMs` at or near 0.** Two failure modes to watch for, and each changes something:

- **It BLOCKS** (elapsed is large) → the breaker's recovery probe is unusable. Drop the `_isHung` check and go back to the plain cooldown, accepting that a recovered window is skipped for the full five minutes. Record that decision.
- **It returns FALSE for a window that is plainly hung** → the probe is useless in the other direction: the breaker would never divert. Same fallback, same record.

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
