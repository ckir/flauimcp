# Occlusion-Aware Capture — Phase 0 measurements

Companion to the [implementation plan](2026-08-21-occlusion-aware-capture.md). Phase 0 (Tasks 1–3)
produces no production code; it answers the questions the design turns on.

**Machine:** Windows 11 Pro 10.0.26200, physical console. All probes run DPI-aware
(`SetProcessDPIAware`), matching the server's `PerMonitorV2` manifest.

**Probe scripts** live in `.clavity/scratch/item8-plan/` — **gitignored, so they will not survive.**
Each section below therefore carries the exact command and enough of the mechanism to re-derive it.

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

2. **The leak is NOT permanent. It is reclaimed when the target recovers.** All three abandoned threads
   returned `True` the moment the window resumed pumping, ran their own cleanup, and GDI fell back to
   the exact baseline (`0`), USER to `3`, with zero threads alive. Note all three returned at the same
   wall-clock instant despite being started seconds apart — they were queued on the one message loop and
   released together.

**What this changes.** The hazard is **outstanding concurrent abandoned calls against a still-hung
window**, not a permanent handle leak. That is precisely what a circuit breaker bounds, so the ratified
containment is the right shape and the out-of-process worker (ROADMAP 17) remains correctly deferred.

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
`False` for a plainly-hung window. `elapsedMs=13` is a cold first call including P/Invoke marshalling
setup; the steady-state cost is below the timer's resolution.

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

### ▶ VERDICT risk 1: **BOTH** render under `PW_RENDERFULLCONTENT`

Chromium and Electron both return real content on every frame. No limitation to add to the tool
description in Task 22 on this account.

### ▶ VERDICT risk 3: **NOT OBSERVED IN 300 RUNS** (200 Chromium + 100 Electron)

**This probe is ONE-SIDED, and the record says so in those words: a positive result CONFIRMS the race; a
negative does NOT refute it.** This is a timing race between an asynchronous compositor and a separate
tree walk, so a finite number of clean runs means **"not observed at these timings"**, never "cannot
happen". **Risk 3 is NOT deleted from the spec.** It ships documented and unmitigated, and §2.5's bookend
walk still does not cover it.

---

## DESIGN GATE (Task 3)

```
1. Does PrintWindow BLOCK on a non-pumping window?              BLOCKS
   -> Tasks 15b and 19b ARE BUILT: the timeout, the dedicated capture thread,
      and the circuit breaker. 110836 ms vs 31 ms, same window, ~3600x.

2. Is the stale composition CONFIRMED?                          NOT OBSERVED IN 300 RUNS
   -> 200 Chromium + 100 Electron. Does NOT refute the race (one-sided probe).
      No escalation is triggered. Risk 3 ships documented and unmitigated.

3. Do Chromium AND Electron render under PW_RENDERFULLCONTENT?  BOTH
   -> No limitation to add to the tool description in Task 22.
```

### Two answers Phase 0 produced that the gate did not ask for

**A. `IsHungAppWindow` is safe** — `True` in 13 ms on the hung window. This closes the single
above-floor unverified assumption that plan-panel round 13 named in its own green verdict. The circuit
breaker keeps its `_isHung` recovery probe; Step 5b's plain-cooldown fallback is not taken.

**B. An abandoned `PrintWindow` leaks, but the leak is RECLAIMED when the target recovers.** Three GDI
objects plus a window-sized bitmap per *outstanding* abandoned call, accumulating linearly with no
ceiling while the window stays hung — then every abandoned thread returned the instant the window
resumed, released its own resources, and the process fell back to its exact baseline.

⚠ **This is a constraint on Task 15, not just an observation.** Reclamation depends on all three of:

1. the capture thread is a **background** thread that is *abandoned*, never `Abort`ed or killed;
2. its resource release sits in that thread's **own `finally`**, so it runs on the late return, after
   the caller has already given up;
3. the caller never reuses the handoff buffer — the abandoned thread writes into its own bitmap at an
   arbitrary later time.

If Task 15 breaks any of the three, this measurement does not transfer and the leak becomes permanent.
**Check Task 15 against this list before Phase 4 is called done.**
