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
