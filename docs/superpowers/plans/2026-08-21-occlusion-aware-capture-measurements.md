# Occlusion-Aware Capture — Phase 0 measurements

Companion to the [implementation plan](2026-08-21-occlusion-aware-capture.md). Phase 0 (Tasks 1–3)
produces no production code; it answers the questions the design turns on.

## Environment — record this, or none of it is reproducible

`PrintWindow`'s behaviour depends on the compositing path, so "it worked on my machine" is not a result
unless the machine is written down.

| | |
|---|---|
| OS | Windows 11 Pro, build **26200** |
| Session | **`console`, Active** — not RDP (`qwinsta`; `rdp-tcp` is merely listening) |
| Display | **1366 × 768**, single monitor |
| GPU | **Intel HD Graphics 3000**, driver 9.17.10.4459 (secondary: NVIDIA GeForce GT 540M) |
| Compositing | **GPU compositing ACTIVE** — a Chrome `--type=gpu-process` was running and **no** renderer carried `--disable-gpu-compositing`, the flag Chrome adds when it falls back to software |
| DWM | running |
| Chrome | **151.0.7922.172** |
| VS Code | **1.131.0** |
| DPI | all probes call `SetProcessDPIAware`, matching the server's `PerMonitorV2` manifest |

⚠ **The GPU is 2011-era integrated hardware with a 2011 driver.** GPU compositing was active, so these
are not software-path numbers — but a modern discrete GPU is a materially different compositing path and
these results are not evidence about it.

**Probe scripts** live in `.clavity/scratch/item8-plan/` — **gitignored, so they will not survive.**
Each section below therefore carries the exact command and enough of the mechanism to re-derive it.

**Raw evidence IS committed**, in `2026-08-21-occlusion-aware-capture-evidence/`:
`chromium-foreground.csv`, `chromium-occluded.csv`, `electron-foreground.csv` — one row per iteration.
An operator re-checking the risk-3 acceptance later should read those, not this summary of them.

---

## Risk 2 — does `PrintWindow` block on a window that is not pumping messages?

### Fixture

A WinForms `Form` (`HANGPROBE`, 640×400, solid blue, a label reading `PUMPING`) that starts an 8 s
timer on `Shown`; the tick sets the label to `HUNG` and then calls `Thread.Sleep(120000)` **on the UI
thread**, so the window stops pumping while remaining visible and correctly sized.

```powershell
# .clavity/scratch/item8-plan/hang-probe/HangWindow.ps1  (launched via powershell.exe -File)
$t.Add_Tick({ $t.Stop(); $lbl.Text = 'HUNG'; [Threading.Thread]::Sleep(120000) })
```

### Probe

```powershell
# .clavity/scratch/item8-plan/hang-probe/HangProbe.ps1 -TitleMatch HANGPROBE
$bmp = New-Object Drawing.Bitmap $w, $ht
$g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
$sw = [Diagnostics.Stopwatch]::StartNew()
$ok = [PW]::PrintWindow($h, $hdc, 2)     # 2 = PW_RENDERFULLCONTENT
$sw.Stop()
```

⚠ **Deviation from the plan's pinned probe, and why.** Task 1 Step 2 located the fixture with
`Get-Process | Where-Object { $_.MainWindowTitle -like "*$TitleMatch*" }`. That **cannot** work for this
fixture and threw `no window matching 'HANGPROBE'` while the Form was demonstrably on screen
(`desktop_list_windows` reported it: `{"Title":"HANGPROBE","ProcessName":"powershell","Pid":16684}`).
The cause: the fixture is a Form hosted by `powershell.exe`, and `MainWindowHandle` /`MainWindowTitle`
resolve to the **host's console window**, not the Form. Replaced with an `EnumWindows` + `GetWindowTextW`
title scan. `GetWindowTextW` on a cross-process window that has a caption reads the cached caption and
does **not** send `WM_GETTEXT`, so the lookup is itself hang-safe — which matters, since its whole job is
to run against a window that is not pumping. **The measured call is unchanged.**

### Results

| Run | Window state | `ret` | `elapsedMs` |
|---|---|---|---|
| Experiment | **HUNG** (UI thread inside `Sleep(120000)`) | `True` | **110836** |
| Control 1 | pumping | `True` | **31** |
| Control 2 | pumping | `True` | **26** |

Controls and experiment are the **same window, same HWND (`1968550`), same 640×400 rect** — the control
runs were taken immediately after the fixture's sleep expired, so nothing but the message-loop state
differs.

The experiment did not merely run slowly: it returned **at the moment the 120 s sleep expired**, having
been launched ~9 s into it. The call is parked on the target's message loop for its entire duration.

Both images are correct and current — including the blocked one, which painted the `HUNG` label after
the loop resumed. `PrintWindow` returning `True` is therefore **not** evidence the call was cheap.

### ▶ VERDICT: **BLOCKS**

Ratification item 3 stands. **Tasks 15b and 19b ARE built**: the timeout, the dedicated capture thread,
and the circuit breaker. The margin is not marginal — **~3,600×** between control and experiment.

---

## Risk 2b (Step 5) — what does an ABANDONED call hold, and for how long?

### Why the pinned step was re-shaped

Task 1 Step 5 says "run it a second and third time against the still-hung window" and read
`HandleCount` of `FlaUI.Mcp.Server`. Taken literally that measures nothing:

1. `FlaUI.Mcp.Server` is not running. The process that leaks is whichever one **calls** `PrintWindow`.
2. A second **sequential** call cannot happen "against the still-hung window" — the first call blocks
   until the hang ends (110836 ms, measured above). The design does not wait either; it abandons the
   thread on a timeout.

So the question that actually matters is: **what do the abandoned calls hold, and is it reclaimed?**
The probe starts each call on its own background thread, abandons it after 2000 ms, and counts GDI
objects with `GetGuiResources(GetCurrentProcess(), 0)` — the breakdown `HandleCount` does not give.
Allocation is raw GDI (`GetDC` + `CreateCompatibleDC` + `CreateCompatibleBitmap`) so every object is
individually countable.

### Results — `.clavity/scratch/item8-plan/hang-probe/AbandonProbe.ps1`

```
baseline  GDI=0 USER=3
call 1: completedWithin2000ms=False  GDI 0 -> 3 (delta 3)  USER=4
call 2: completedWithin2000ms=False  GDI 3 -> 6 (delta 3)  USER=5
call 3: completedWithin2000ms=False  GDI 6 -> 9 (delta 3)  USER=6
after 3 abandoned calls: GDI=9 USER=6

window unhung at t+102544ms
after resume: GDI=0 USER=3
thread 1: RETURNED ret=True after 107260ms, resources released
thread 2: RETURNED ret=True after 105240ms, resources released
thread 3: RETURNED ret=True after 103248ms, resources released
threads still alive: 0
```

### Two findings, and the second one is not in the spec

1. **Accumulation is real and linear — the claim the breaker exists to bound is CONFIRMED.** Each
   abandoned call holds exactly **3 GDI objects** (screen DC, memory DC, bitmap) plus one USER handle,
   and they stack: 3 → 6 → 9 with no ceiling while the target stays hung. It also holds a
   **window-sized bitmap in memory** for the same duration — at 4K that is the ~33 MB transient the plan
   states as property 1, once **per outstanding abandoned call**, not once per capture.

2. **The leak lasts exactly as long as the TARGET PROCESS does — no longer, and no shorter.** Two exits
   were measured, and it is released on either:
   - **The window resumes pumping.** All three abandoned threads returned `True` the moment it did, ran
     their own cleanup, and GDI fell back to the exact baseline (`0`), USER to `3`, zero threads alive.
     All three returned at the same wall-clock instant despite being started seconds apart — queued on
     the one message loop, released together.
   - **The target process is killed.** Measured separately: 3 abandoned calls, `GDI=9`, then
     `Stop-Process -Force` on the target — **every thread returned within 130 ms**, `IsWindow` went
     false, resources released, `GDI` back to `0`.

⚠ **Both of the absolute statements in play were wrong, in opposite directions, and this is the
correction.** An earlier draft of this section said the leak "is NOT permanent" — too generous, because
it assumed every hang ends. The plan's own comment on `CaptureCircuitBreaker`
(in Task 17's Step 4b; grep `THIS CONTAINS; IT DOES NOT RECLAIM` — this was cited as `phase-5-coordinator.md:1305-1309`, stale since the class moved out of Task 19) says each blocked call keeps its thread and bitmaps
**"permanently"** — too absolute, because it ignores process exit. **The measured truth: the leak
persists while the target process stays alive AND wedged, and is reclaimed the moment either of those
stops being true.** A wedged app that never recovers is normally *killed*, which is a reclaiming event.

**Task 19 must replace the contradicting sentence. Here is the exact text** — *"soften it" was
correctly called non-actionable by the AGY-AFTER round-2 Literal Implementer, so this record supplies
the replacement rather than the intent.* In `WindowCaptureCoordinator.cs`, the
`CaptureCircuitBreaker` summary currently reads:

> ⚠ THIS CONTAINS; IT DOES NOT RECLAIM. Each blocked call keeps its thread, its HDC, its GDI bitmap and
> its managed bitmap **permanently** -- a blocked Win32 call cannot be cancelled, so nothing in-process
> can take them back.

Replace that sentence with:

> ⚠ THIS CONTAINS; IT DOES NOT RECLAIM. Each blocked call keeps its thread, its HDC, its GDI bitmap and
> its managed bitmap for as long as the TARGET PROCESS lives -- a blocked Win32 call cannot be
> cancelled, so nothing in-process can take them back. MEASURED (Phase 0): they are released when the
> target's message loop resumes, and within 130ms of the target process exiting. Treat that as
> unbounded, because neither event is under this server's control.

The rest of that comment — the multiplier framing and the ROADMAP-17 pointer — is correct and stays.

**What this changes.** The hazard is **outstanding concurrent abandoned calls against a still-hung
window**. A circuit breaker is the right shape of containment for it — but be precise about what it
bounds:

⚠ **The breaker is keyed PER-HWND, so it bounds the MULTIPLIER, not the TOTAL.** The plan says so in its
own words — *"What this bounds is the MULTIPLIER: N captures of a hung window cost ONE leak instead of
N."* (cited as `phase-5-coordinator.md:1307`, stale since the class moved into Task 17's Step 4b; grep
the sentence rather than the line.) Across **M distinct** hung windows the server still pays M
concurrent leaks, and a per-HWND breaker is blind to that sum. In a server built to run for weeks this
is the accumulation that is not contained. *(Raised by the AGY-AFTER panel, round 1, Cascade Analyst;
confirmed against the plan's own text — not a hypothetical.)* The out-of-process worker (ROADMAP 17)
remains the only fix that reclaims, and stays correctly deferred — but **ROADMAP 17 should record that
the per-HWND breaker leaves the cross-window total unbounded**, which is not currently written down
anywhere.

*Not folded:* the panel also asserted a specific figure — 20 hung windows costing "~660 MB". That number
was never measured by anyone and is a hypothesis, not evidence; the structural finding above stands on
the plan's own text without it.

**The condition this rests on — and it is a design constraint, not an observation.** Reclamation
happened because each abandoned thread was left **alive** and freed its own resources on return. It
requires all three of:

- the capture thread is a **background** thread that is abandoned, never `Abort`ed or otherwise killed;
- its resource release sits in the thread's own `finally`, so it runs on the late return **after** the
  caller has given up and moved on;
- the caller must not treat the handoff buffer as reusable — the abandoned thread still writes into its
  own bitmap at an arbitrary later time.

If Task 15's implementation violates any of these, this measurement does not transfer and the leak
becomes permanent. **Task 15 must be checked against this list.**

---

## Risk 2c (Step 5b) — does `IsHungAppWindow` itself block?

The plan asserts the breaker's recovery probe "does not block on the target's message loop, so asking is
safe on precisely the window we are avoiding". That was documented Win32 behaviour, not a measured fact,
and round 13's panel named it as the one unverified assumption above the severity floor: **if it were
wrong the breaker would hang on exactly the window it exists to avoid hanging on.**

Measured against the same hung `HANGPROBE` window:

```
t+    22ms  IsHungAppWindow=True  elapsedMs=13
```

**Both failure modes named in Step 5b are refuted.** It did not block (13 ms), and it did not report
`False` for a plainly-hung window.

⚠ **An earlier draft explained that 13 ms away as "a cold first call; the steady-state cost is below the
timer's resolution." That was an assertion, not a measurement — one sample, then a conclusion about a
distribution.** *(Raised by the AGY-AFTER panel, round 1, Blindspot Auditor — my own seat, on my own
sentence.)* Measured properly, 2000 samples against the same hung window:

```
first call in a fresh session:  6.1488 ms
n=2000   mean=0.05208 ms   median=0.02410 ms   p99=0.17320 ms   max=25.79450 ms
calls taking >1 ms: 3 / 2000
```

The direction was right and the magnitude is now known: steady state is **~24 µs median**, four orders
of magnitude under the 110,836 ms it exists to avoid. But **it is not uniformly negligible** — there is
a tail, `max = 25.79 ms`, and 3 calls in 2000 exceeded 1 ms. That is immaterial against a 110-second
block and material to nothing else in this design, but it is now written down instead of assumed.

**The HEALTHY window is the common path, and it was not measured either.** *(AGY-AFTER round 2, State
Corruptor: 2000 tight-loop calls against an already-stably-hung window may only measure a cached flag.)*
Same sampling against a live, responsive window:

```
returns False (correct); first call in a fresh session 6.1613 ms
n=2000   mean=0.08705 ms   median=0.05440 ms   p99=0.42940 ms   max=28.80160 ms
calls taking >1 ms: 8 / 2000
```

Same order of magnitude, correct answer, slightly costlier than the hung case (median 54 µs vs 24 µs) —
consistent with a cached flag being read either way. ⚠ **Still unmeasured: a window at the exact moment
it crosses into "hung"**, which is the transition the breaker will sometimes catch. Both steady states
are cheap; the boundary is not characterised.

### ▶ VERDICT: `IsHungAppWindow` is SAFE on a hung window

The breaker keeps its `_isHung` recovery check. The plain-cooldown fallback described in Step 5b is
**not** taken, and a recovered window is **not** skipped for the full five minutes.

---

## Risks 1 and 3 — Chromium and Electron, and the stale composition

### Why the pinned probe was replaced

Task 2 Step 1's probe compared the UIA root `Name` against a **12×12 pixel hash**, with a human toggling
something by hand while it ran. A hash detects *that* a frame changed; it cannot say *which* frame, so it
cannot separate a stale composition from ordinary render latency — and there is no verdict a hash can
support beyond "something moved".

Replaced with a self-animating page (`stale.html`) that **encodes its frame number into the image**, so a
captured frame decodes to an exact integer and staleness is quantified in frames. Three further changes,
each of which strengthens the test:

- **The tree is read before *and* after each capture.** The pinned probe read it only after, which biases
  toward "stale" for the trivial reason that the later read is newer. The verdict uses the **before**
  read, the order-safe direction.
- **A screen `BitBlt` is taken beside every `PrintWindow`**, with the grab order alternating so neither
  source is systematically the fresher one. That is the comparison this feature actually replaces.
- **The title is published from inside a double `requestAnimationFrame`.** `rAF` fires *before* a paint,
  so setting the title in a second `rAF` means the title only ever names a frame that has **already
  painted**. Therefore `titleBefore <= paintedFrame` always holds in a correct system, and a capture
  decoding to *less* than the title read before it is unambiguously stale rather than merely late.

⚠ **The first version of this probe reported a 100% stale rate, and it was WRONG.** It encoded the frame
number into the background *colour* as `rgb(n & 255, (n >> 8) & 255, 77)`. Chrome applies a
colour-management transform, so the captured pixels are not the CSS values: on the frame the probe
decoded as `163`, the saved PNG plainly reads **419** in both the page text and the window's own title
bar. The encoding was manufacturing the staleness. Re-encoded as 16 full-height **black/white stripes**
(threshold at 128), which no smooth colour transform can shift, and **verified by hand against a saved
frame**: the stripes decode to `89` on a frame whose text and title both read `89`.

### Chromium — Chrome app mode, 900×700, `--force-color-profile=srgb`, 200 iterations

```
RISK 1  PrintWindow ret=True on 200 / 200
        all-black frames: 0 / 200      (screen-scrape all-black: 0 / 200)
RISK 3  pw - treeBefore : min=0  max=3  mean=1.58
        pw - screenScrape: min=-1 max=1 mean=0     exact agreement: 131 / 200
        STALE rows (pw frame OLDER than a tree read taken BEFORE the capture): 0 / 200
```

`pw - treeBefore` is **never negative**: the captured frame is always the same as, or newer than, the
frame the tree had already published. `pw - screenScrape` sits in `[-1, +1]` around zero, which is the
~10 ms between the two grabs at a 100 ms animation cadence — `PrintWindow` tracks the live compositor.

### Electron — VS Code 1216×728, extensions disabled, 100 iterations

An Electron app's own UI cannot be pointed at a local URL, so the stripe page does not apply. Animation
was driven with **no synthetic input**: VS Code auto-reloads an open unmodified file when it changes on
disk, and a background writer rewrote `counter.txt` every 200 ms.

```
RISK 1  ret=True on 100 / 100
        all-black frames: 0 / 100      near-flat (<=2 colours on a 40x30 grid): 0 / 100
        mean luminance 28.9 (PrintWindow) vs 28.8 (screen)   distinct colours on grid: 48..60
RISK 3  PrintWindow vs screen-scrape grid disagreement %: min=0 max=1.33 mean=0.035
        frames agreeing EXACTLY: 94 / 100
NON-VACUITY  grid change vs the PREVIOUS PrintWindow frame: min=0 max=6.92 mean=0.473
             frames where the target visibly CHANGED: 75 / 99
```

⚠ **The non-vacuity line is load-bearing and the first Electron run failed it.** That run reported a
perfect `0.00%` disagreement on 100/100 frames — from a window covered by a first-run sign-in modal that
hid the animating editor entirely. **Two identical stills always agree**, so a staleness result measured
against a static target means nothing. The saved PNG is what exposed it. The run above is the one where
the target is provably moving, with 75 of 99 frames differing from their predecessor.

The captured Electron frame is the complete UI — editor text, minimap, sidebar, chat panel, a
notification toast — not a partial or placeholder surface.

### Chromium, OCCLUDED — the condition the feature actually exists for

⚠ **Every run above brought its target to the FOREGROUND first.** A visible window is being actively
composited, which is the case *least* likely to go stale — so those 300 runs answered a question
adjacent to the one the gate asks. **Occlusion is the only condition this entire feature exists for.**
*(Raised by the AGY-AFTER panel, round 1: my Axiom Breaker and the peer's Dependency Cynic
independently.)*

Re-run with the Chrome window left exactly where it was, fully covered by a maximized, `TopMost`,
opaque window. Same page, same 200 iterations, same decode:

```
NoForeground: target left where it is (occlusion run)
RISK 1  PrintWindow ret=True on 200 / 200
        all-black PrintWindow frames: 0 / 200
RISK 3  pw - treeBefore : min=0  max=3  mean=1.59
        pw - screenScrape: min=924 max=1097 mean=1014.69
        exact agreement pw == screenScrape: 0 / 200
        STALE rows: 0 / 200
```

**The screen-scrape column stops being a fairness control here and becomes a POSITIVE CONTROL.** It
decoded **0 on all 200 frames** — it was photographing the occluder, whose green falls below the stripe
threshold — while `PrintWindow` decoded the real advancing frame number. `pw − screenScrape ≈ 1015`,
with **exact agreement 0/200**. That is a direct measurement of the feature's entire thesis:
**`PrintWindow` sees through occlusion where the scrape sees only the occluder.**

⚠ **That control alone does NOT prove full-area occlusion, and an earlier draft claimed it did.** The
decoder reads 16 points on a single scan line, so an occluder covering only that line would satisfy it
while the rest of the window stayed exposed. *(AGY-AFTER round 2, Axiom Breaker.)* Replaced with a
full-area measurement over a 40×30 grid spanning the whole client rect:

```
OCCLUSION PROOF: 1200/1200 grid points over the FULL CLIENT AREA are occluder = 100%
```

**That control has already earned its place**: on a later run it reported `0/1200 = 0%` and correctly
invalidated the run — the occluder process had not finished starting when the probe began, and six
iterations were captured against a still-visible window. Without it that run would have been recorded
as a clean occluded result.

Staleness under occlusion is **statistically identical** to the foreground case — `mean 1.59` occluded
vs `1.58` and `1.64` across two independent foreground runs, never negative in any of them.

### The compositor-throttling mechanism — CONFIRMED, and this reverses an earlier conclusion

The panel's Dependency Cynic argued the foreground-only result could not transfer because *"Chromium
aggressively throttles or suspends its compositor for background or fully occluded windows"* — a
specific, testable mechanism rather than a general doubt, so it was tested rather than accepted.

⚠ **The first test said REFUTED. That was WRONG, and this section is the retraction.** Round 1 measured
wall-clock cadence right after raising the occluder and recorded `9.46 f/s occluded` vs `9.99 f/s
visible`, ratio 0.947, and concluded there was no throttling. **Round 2's Adversary-of-the-Reviewer seat
picked exactly that correction as the one most likely to be wrong, and it was right.** *(AGY-AFTER
round 2, Adversary of the Reviewer + open question 1.)*

What the same page does under **sustained** occlusion:

```
OCCLUDED, established:    0.00 frames/sec   (frame 214 held for 10s, and across 150 capture iterations)
un-occluded, +3s later:  10.09 frames/sec   (and it had caught up 214 -> 299 while settling)
```

**A fully occluded Chromium window can suspend rendering completely.** Confirmed twice, on separate
runs and separate frames (frozen at `214`, and later at `2892`). Round 1's cadence number was taken
during the interval before suspension engaged, so it measured the onset delay, not the steady state.

**Two further facts, both measured, and the first is the one that matters for the tool contract:**

- **`PrintWindow` does NOT wake a suspended renderer.** 150 consecutive `PW_RENDERFULLCONTENT` calls
  against the frozen window all returned `True` with real content, and the content never advanced past
  frame `214`. The capture cannot force the frame it wants to be fresh.
- **The tree suspends WITH the pixels, so this is a freshness hazard and NOT a leak.** Across all 150
  frozen iterations `dTree = 0` exactly — the UIA tree reported `214` and the pixels showed `214`. Both
  are produced by the same suspended renderer, so they cannot drift apart. Masks computed from that
  tree still land correctly on that image.

⚠ **The onset is NOT deterministic and I did not isolate what selects it.** A separate 44-second
occluded interval, sampled every 2 s, never suspended at all — it held ~10 f/s for the entire time and
recovered normally. So the honest statement is: **sustained full occlusion can suspend Chromium
rendering indefinitely, on a trigger this record did not characterise.** Two suspended intervals, one
unsuspended interval of 44 s.

**Scope:** occluded, non-minimized, this configuration. Not evidence about **minimized** windows (which
this design refuses outright via the `IsIconic` guard), background **tabs**, or software compositing.
**Electron did not suspend under the same treatment** — see below.

### Electron, OCCLUDED

⚠ **An earlier draft applied the occlusion result to Electron, which had only ever been tested in the
foreground.** *(AGY-AFTER round 2, open question 4 — the same "what was measured vs what was claimed"
rigour had been applied to the tree-read variable and then dropped for the occlusion variable.)* Rather
than retreat the claim, it was measured. VS Code, fully covered by the same occluder, 60 iterations:

```
RISK 1  ret=True on 60 / 60      all-black: 0 / 60      near-flat: 0 / 60
        mean PrintWindow luminance 28.5 vs screen 53      distinct colours on grid: 49..61
        PrintWindow vs screen-scrape disagreement: 100% on every frame
NON-VACUITY  frames where the target visibly CHANGED: 54 / 59
```

Positive control: the screen scrape read a uniform luminance of **53** — the occluder's solid green — on
every frame, and disagreed with `PrintWindow` on **100%** of grid points. **Electron renders in full
under occlusion, and — unlike Chromium — it did NOT suspend:** 54 of 59 frames differed from their
predecessor while fully covered.

### ▶ VERDICT risk 1: **BOTH** render under `PW_RENDERFULLCONTENT`, occluded included

| Run | Frames | Real content | All-black |
|---|---|---|---|
| Chromium, foreground | 200 | 200 | 0 |
| Chromium, fully occluded | 200 | 200 | 0 |
| Electron, foreground | 100 | 100 | 0 |
| Electron, fully occluded | 60 | 60 | 0 |
| **Total** | **560** | **560** | **0** |

⚠ **An earlier draft claimed "600/600" here. That figure was simply wrong** — the runs it named summed
to 500 — and it appeared twice, including inside the DESIGN GATE block. *(AGY-AFTER round 2, open
question 3.)* The table above is the arithmetic, run by run.

⚠ **THIS VERDICT NOW CARRIES A LIMITATION, and it is a change from the earlier draft's "nothing to
add".** `PrintWindow` returns *a* real frame under occlusion — but for Chromium that frame can be the
last one painted before the renderer suspended, arbitrarily old, and no `PrintWindow` call wakes it.
**Task 22 must say so in the tool description.** The image is not wrong and its masks are not wrong; it
may simply be *old*, with nothing in the response indicating that.

Still untested and therefore unclaimed: RDP sessions, software-compositing fallback, machines with no
GPU, and modern discrete GPUs. Minimized windows are refused by design, so they need no evidence.

### ▶ VERDICT risk 3: **NOT OBSERVED IN 400 TREE-VS-PIXEL RUNS**

⚠ **An earlier draft of this verdict read "NOT OBSERVED IN 300 RUNS (200 Chromium + 100 Electron)". That
figure was not supported, and the correction matters more than the arithmetic.** *(Raised
independently by the panel's State Corruptor and Mechanism Gamer, and by my own State Corruptor.)* The
runs were not measuring the same thing:

| Runs | What was compared | Can it observe risk 3? |
|---|---|---|
| 200 Chromium, foreground | frame number in the `PrintWindow` **pixels** vs the frame number in the **UIA tree** | **Yes** — this is risk 3 as the spec defines it |
| 200 Chromium, occluded | same | **Yes** |
| 100 Electron, foreground | `PrintWindow` **pixels** vs screen-scrape **pixels** | **No** — the UIA tree was never read |

The Electron runs compare two pixel-acquisition APIs against each other. That measures render-path
latency between two capture methods; it is structurally incapable of detecting a race between the visual
surface and the accessibility tree, which is what risk 3 *is*. Counting them toward a staleness total
inflated the sample with 100 runs that had **no ability to observe the hazard**. They are re-scoped to
what they actually support — risk 1 for Electron, plus a secondary freshness signal — and the staleness
figure now counts only the 400 runs that read the tree.

### Staleness under CPU contention

⚠ **400 clean runs on an idle machine bound nothing about a timing race under contention.** *(AGY-AFTER
round 2, Boundary Smuggler — and it goes directly to the privacy risk being accepted, so it was
measured rather than noted.)* Same page, foreground, 150 iterations, with **8 CPU burners against 4
logical processors — 2× oversubscription** for the whole run:

```
pw - treeBefore : min=0  max=6  mean=1.65
pw - screenScrape: min=-1 max=1 mean=0.03    exact agreement: 96 / 150
STALE rows: 0 / 150
```

**Contention widened the gap in the SAFE direction.** The maximum tree-to-pixel distance rose from
**3 (idle) to 6 (loaded)** — the captured frame ran *further ahead* of the tree, never behind it. Zero
stale rows.

⚠ **Two earlier attempts at this measurement were confounded, and are reported rather than buried.** The
first ran while the occluder was still starting (`OCCLUSION PROOF: 0/1200`), so six iterations were
captured against a visible window. The second was fully occluded and **froze**, holding frame `2892` for
all 150 iterations — which makes it **vacuous for staleness**: nothing changed, so nothing could be
observed stale. Only the unoccluded run above isolates the load variable with the target provably
moving. **Staleness under contention *while suspended* is not a meaningful question** — a suspended
renderer's tree and pixels are frozen together.

**This probe is ONE-SIDED, and the record says so in those words: a positive result CONFIRMS the race; a
negative does NOT refute it.** This is a timing race between an asynchronous compositor and a separate
tree walk, so a finite number of clean runs means **"not observed at these timings"**, never "cannot
happen". **Risk 3 is NOT deleted from the spec.** It ships documented and unmitigated, and §2.5's bookend
walk still does not cover it.

---

## Scope limits — what these numbers do NOT cover

Written down because a measurement record's worst failure is being read as broader than it is.

1. **One hang mechanism was tested.** The fixture blocks its UI thread in `Thread.Sleep(120000)`. Both
   the `PrintWindow`-blocks result and the `IsHungAppWindow`-is-safe result rest on that single shape.
   *(Raised by the AGY-AFTER panel, round 1, Axiom Breaker.)* The specific case worth naming: a thread
   inside a **synchronous cross-apartment COM call** still pumps some messages, so `IsHungAppWindow`
   could report `False` for a window that is nonetheless unable to service `WM_PRINT` — the breaker
   would not divert, and the capture would block anyway. **Not measured.** The containments still work
   (the timeout fires regardless of *why* the call blocked); what degrades is only the breaker's ability
   to recognise the window *early*, so this is a latency question, not a leak question.
2. **One machine, one compositing path.** See the environment table. Untested: RDP, software
   compositing, no-GPU, and modern discrete GPUs.
3. **Not minimized.** The design refuses minimized windows outright (`IsIconic`), so no evidence is
   owed — but none exists either.
4. **The staleness probe is one-sided** — restated here because it is the easiest limit to forget:
   400 clean tree-vs-pixel runs mean "not observed at these timings", never "cannot happen".

---

## DESIGN GATE (Task 3)

```
1. Does PrintWindow BLOCK on a non-pumping window?              BLOCKS
   -> Tasks 15b and 19b ARE BUILT: the timeout, the dedicated capture thread,
      and the circuit breaker. 110836 ms vs 31 ms, same window, ~3600x.

2. Is the stale composition CONFIRMED?                          NOT OBSERVED IN 400 RUNS
   -> 200 Chromium foreground + 200 Chromium OCCLUDED, both tree-vs-pixels.
      The 100 Electron runs are NOT counted here: they never read the tree,
      so they could not observe this hazard at all.
      Does NOT refute the race (one-sided probe). No escalation is triggered.
      Risk 3 ships documented and unmitigated.

3. Do Chromium AND Electron render under PW_RENDERFULLCONTENT?  BOTH
   -> Both engines, foreground AND fully occluded: 560/560 frames real
      content, 0 all-black.
   -> BUT there IS a limitation to add to Task 22, and it was found in
      round 2 of the panel, not in the original measurement:
      a fully occluded CHROMIUM window can SUSPEND rendering, after which
      PrintWindow returns the last painted frame forever. 150 calls did not
      wake it. The image and its masks are correct; they may be OLD, and
      nothing in the response says so. Electron did NOT suspend.
      This is a FRESHNESS limit, not a leak: the UIA tree freezes with the
      pixels (dTree = 0 on all 150 frozen iterations).
   -> RDP / software-compositing / no-GPU remain untested and unclaimed.
```

⚠ **Answer 3 changed after the operator first saw it.** The original record said "no limitation to add
to the tool description". That was wrong: the occlusion case had never been measured, and measuring it
found a real contract limitation. Answers 1 and 2 are unchanged.

### ▶ GATE CLEARED — operator decision, 2026-08-21

The operator was shown answers 1–3 twice: once as first recorded, and again after the AGY-AFTER panel
changed answer 3. **The gate is cleared and Phase 1 may begin.** Disposition:

- **Risk 3 is accepted unmitigated**, on 400 tree-vs-pixel runs that did not observe it, with the
  one-sidedness of that evidence explicitly on the record.
- **The occluded-Chromium freshness limit is accepted as a DOCUMENTATION item, not a mitigation.**

⚠ **TASK 22 OWES A TOOL-DESCRIPTION CHANGE, and it is the only carried obligation from Phase 0.** The
description must state that a window which has been occluded for some time may return the last frame its
renderer painted, which can be arbitrarily old, and that the capture cannot force it to refresh. Do not
promise freshness. The masks and geometry are unaffected — this is about the image's age, nothing else.
Chromium-family targets only; Electron did not suspend under the same treatment.

**Two further carried obligations, both from the panel and both cheap:**

- **Task 19** replaces its `CaptureCircuitBreaker` "permanently" sentence with the verbatim text given
  in the Risk 2b section above.
- **ROADMAP 17** records that the per-HWND breaker leaves the cross-window total unbounded.

### Two answers Phase 0 produced that the gate did not ask for

**A. `IsHungAppWindow` is safe** — `True` in 13 ms on the hung window. This closes the single
above-floor unverified assumption that plan-panel round 13 named in its own green verdict. The circuit
breaker keeps its `_isHung` recovery probe; Step 5b's plain-cooldown fallback is not taken.

**B. An abandoned `PrintWindow` leaks for exactly as long as the target process lives.** Three GDI
objects plus a window-sized bitmap per *outstanding* abandoned call, accumulating linearly with no
ceiling while the window stays hung — then released on **either** exit: the window resuming (every
thread returned at that instant) **or** the target being killed (every thread returned within **130 ms**,
GDI back to `0`). Permanent only while the target stays alive *and* wedged.

⚠ **And the containment bounds less than the plan's prose implies:** the breaker is **per-HWND**, so it
bounds N captures of *one* hung window, not M concurrent leaks across M hung windows. **ROADMAP 17
should say so.** Task 19's own comment calling the leak "permanent" also needs softening — this record
contradicts it.

⚠ **This is a constraint on Task 15, not just an observation.** Reclamation depends on all three of:

1. the capture thread is a **background** thread that is *abandoned*, never `Abort`ed or killed;
2. its resource release sits in that thread's **own `finally`**, so it runs on the late return, after
   the caller has already given up;
3. the caller never reuses the handoff buffer — the abandoned thread writes into its own bitmap at an
   arbitrary later time.

If Task 15 breaks any of the three, this measurement does not transfer and the leak becomes permanent.
**Check Task 15 against this list before Phase 4 is called done.**

## Risk 5 — the detector's sampling

**Strategy:** a fixed 64×64 sparse grid, plus an explicit sweep of the far right column and bottom row
(a stride that does not divide the dimension would otherwise never sample them). Cost is O(64²)
regardless of resolution, which is what makes it free next to the PNG encode that follows it.

**The read path changed after measurement.** An earlier `Bitmap.GetPixel`-based scan measured
**110 / 114 / 177 ms** for 20 detections on a 3840×2160 bitmap — over the <100 ms budget on every run.
Benchmarked head-to-head against a `LockBits` + `Marshal.ReadInt32` scan at the same sample points, after
first confirming both paths return the identical answer on a uniform bitmap and on one with content,
`LockBits` measured **29 / 30 / 30 ms** — the sampling strategy was never the problem; `GetPixel` was the
wrong instrument. The implementation now takes a `LockBits` fast path guarded by
`Image.GetPixelFormatSize(bmp.PixelFormat) == 32`, falling back to `GetPixel` for a non-32bpp or
bottom-up (negative-stride) bitmap.

**Measured:** 20 detections on a 3840×2160 bitmap took **4ms** (three runs: 3ms, 4ms, 5ms, 4ms) against
the <100ms budget, on the machine described in the environment table above — well inside the LockBits
range recorded above, confirming the fast path is the one taken.

**Criteria 1 and 2 are enforced by TESTS, not by inspection:**
- criterion 1 (a blank render is uniform) — `A_uniform_bitmap_is_detected`, three colours including
  white and mid-grey, because the property is uniformity and not darkness.
- criterion 2 (the F4 dark-themed pixel-perfect render is NOT uniform) —
  `The_F4_dark_themed_real_render_is_not_uniform`. This is the case that rules out every darkness-based
  predicate: F4's non-black fraction is 0.044 *because it is a dark theme*, and a luminance test would
  have discarded a flawless capture.
- the fallback path (non-32bpp bitmaps) is exercised by `A_non_32bpp_uniform_bitmap_is_detected_through_the_fallback`
  and `A_non_32bpp_bitmap_with_content_is_not_uniform_through_the_fallback` — without them the format
  guard's else-branch would be dead code that still ships.

## Risk 4 — GDI handle counts (Task 23)

`test/FlaUI.Mcp.Tests/Capture/GdiHandleLeakTests.cs`, Desktop category, run on a physical console against
the WPF TestApp. `GetGuiResources(GetCurrentProcess(), 0|1)` before and after, with a `GC.Collect()` /
`WaitForPendingFinalizers()` / `GC.Collect()` between so a finalizable handle cannot masquerade as flat.

| test | iterations | result |
|---|---|---|
| `Repeated_successful_captures_leave_handle_counts_flat` | 3 warm-up + 30 measured | **FLAT** |
| `An_empty_crop_refusal_leaves_handle_counts_flat` | 3 warm-up + 20 measured | **FLAT** |

**NON-VACUITY, MEASURED.** Deleting `if (hbm != IntPtr.Zero) DeleteObject(hbm);` from
`PrintWindowImageSource.Render`'s `finally` turns **both** red, each by exactly its iteration count:
`GDI objects grew 29 -> 59` (+30) and `GDI objects grew 6 -> 26` (+20). The assembly still builds, because
`hbm` is read by `Image.FromHbitmap(hbm)` — so the guarded tests actually RUN rather than being skipped by
a broken build.

⚠ **THE EMPTY-CROP TEST NEEDED A WARM-UP AND FAILED WITHOUT ONE — `GDI objects grew 0 -> 3`.** That was
NOT a leak. Three is a ONE-TIME cost: GDI+ initialises on first use, and the baseline was being taken at
`0` before anything in the process had touched it. Two independent facts settle it — a per-iteration leak
across 20 iterations would show ~60, not 3, and the success test (which already warmed up, with the
comment *"the first call allocates caches that are not a leak"*) was flat across 30. Confirmed by
experiment: adding the same 3-iteration warm-up makes it flat, and the mutant above still turns it red,
so the warm-up did not hide anything.

### Coverage — the four exits after canonical step 6b, honestly

| exit | covered? | why |
|---|---|---|
| success (bitmap returned and disposed) | ✅ tested | 30 iterations, flat |
| empty crop refusal | ✅ tested | 20 iterations, flat |
| null GDI handle mid-allocation | ⚠ structural only | every handle is checked and released in `Render`'s `finally`, but forcing a null handle on real GDI means exhausting the process's handle table — not stageable without destabilising the run |
| acquisition timeout | ⛔ **MEASURED NON-GOAL** | leaks 3 GDI per OUTSTANDING abandoned call by design |

⛔ **THE TIMEOUT ROW IS A MEASURED NON-GOAL, NOT AN UNTESTED GAP, and the distinction matters.** Risk 2b
above measured it directly: `GDI 0 -> 3 -> 6 -> 9` across three abandoned calls, released only when the
target's message loop resumes or within **130 ms** of the target process exiting — neither under this
server's control. A blocked Win32 call cannot be cancelled, so `PrintWindowImageSource` CONTAINS the cost
(a dedicated thread; the per-HWND breaker makes N captures of a hung window cost ONE leak instead of N)
and reclaims nothing. **ROADMAP 17** is the out-of-process worker that would actually reclaim.

Task 23's acceptance criterion originally demanded flat handles after "a timeout" as well. That would
have required a test that must fail, and the only way to make it pass is an in-process reclaim of what a
blocked call holds — which is impossible, and is the whole reason ROADMAP 17 exists. Corrected in the
plan before execution.

## The FALLBACK's end-to-end cost (Task 25 Step 4b)

The fallback path grew expensive across panel rounds 6-9 — it now performs a full-desktop redaction walk
(`AllMaskRectsAsync`, a UIA descendant walk per visible window) plus two denylist enumerations, on a
request that has already spent its retry budget — and nobody had timed it. Measured by
`test/FlaUI.Mcp.Tests/Capture/FallbackCostMeasurement.cs`.

| | measured |
|---|---|
| visible windows during the run | **10** |
| `AllMaskRectsAsync` alone | **2864 ms** |
| 2 × `DenylistedWindowsVisibleAsync` | **6 ms** |
| fallback, excluding the timeout wait | **3559 ms** |
| one timeout wait (`CaptureRetryOptions.Default.TimeoutMs`) | 1500 ms |
| **end to end** | **~5059 ms** |

**The desktop walk IS the cost: 2864 of 3559 ms, and the denylist checks are noise at 6 ms.** The
remaining ~700 ms is the geometry walk plus the scrape and encode.

⚠ **THIS IS A LOWER BOUND, NOT A TYPICAL FIGURE.** The plan asked for "a dozen or more visible windows, at
least one Chromium-family"; the run had **10** and the browser mix was not controlled. `AllMaskRectsAsync`
walks every visible window's descendants, so the number grows with the desktop. Do not quote ~5 s as a
ceiling.

⚠ **METHOD DEVIATION, DISCLOSED.** The plan says to force the timeout with the Task 1 `HangProbe` fixture.
This staged it with a source that reports a timeout immediately instead. What is being measured — the
desktop walk the fallback performs — is identical either way, and a really-hung window would leak a
blocked thread plus 3 GDI objects for the lifetime of the hung process (risk 2b above). The timeout wait
it skips is a known constant and is added back in the table rather than paid.

⚠ **NOTE THE TIMEOUT PATH DOES NOT RETRY.** `CaptureAsync`'s `TimedOut` arm trips the breaker and falls
back immediately; the 3-attempt budget applies to the RESIZE path. So a hung-window fallback is one
timeout wait plus the walk, not `MaxAttempts × TimeoutMs` plus the walk.

**NO PASS/FAIL THRESHOLD, DELIBERATELY.** The walk cannot be bounded without scraping a PARTIAL mask set,
which is the leak it exists to close — the honest options are "pay it" or "refuse". **OPERATOR
DISPOSITION REQUIRED:** whether the tool description must warn about the cost, and whether the fallback
should be gated behind an opt-in on very busy desktops.
