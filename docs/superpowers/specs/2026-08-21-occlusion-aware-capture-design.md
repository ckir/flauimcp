# Occlusion-aware window capture (`PrintWindow`) — design

**ROADMAP item 8.** Second of four in the v1.0.0 batch, after the release-tooling subproject merged
(`128e7d3`).

## The problem

`desktop_screenshot` at window scope captures the window's **screen rectangle**:

```csharp
// ScreenshotTools.cs:58
result = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.Bounds, geo.MaskRects, maxWidth));
```

`CaptureRectangle` acquires with `Capture.Rectangle(absolute, null)` — a scrape of that screen region. So
capturing a background window returns **a photograph of whatever occludes it**, confidently and silently.
Nothing detects it.

There is no focus-first code. The tool description says *"Focus the window first (no occlusion
handling)"* — that is an instruction to the CALLER, not behaviour in the product. An instruction the
caller must remember is the same shape as the README line that told maintainers to disable a plugin id
that no longer existed: it works until someone doesn't.

**Goal:** capture a window's own content regardless of occlusion, without touching focus.

**Success criterion, checkable:** capture a window that is behind another window and get that window's
pixels, with redaction masks still landing on the right regions, and with no case where a wrong image is
returned **without a machine-readable indication that it may be wrong**.

⚠ That last clause is deliberately weaker than "no wrong image is ever returned as if it were correct".
Section 3 chooses not to gate on image content, so a wrong image CAN be returned; what the design owes is
that it never arrives unlabelled. Stating the criterion the other way would contradict the design it is
supposed to judge. *(Panel round 1, AB-1.)*

## Evidence — MEASURED, not assumed

Probe `.clavity/scratch/printwindow-probe/probe.ps1`, run DPI-aware on BOTH an RDP session and the
physical console. "non-black" is the fraction of a 40x40 sample grid that is not near-black.

| Window | Class | flag=0 | flag=2 (`PW_RENDERFULLCONTENT`) |
|---|---|---|---|
| WindowsTerminal | DirectX/Atlas | 0 | 0.951 |
| explorer (small) | plain Win32 | 1 | 1 |
| SecHealthUI, **occluded** | XAML/UWP | 0 | 0.044 |
| PowerToys.QuickAccess | WPF | 0.025 | 0.975 |
| Program Manager | shell | 0 | 0.29–0.34 |

**F1. `PW_RENDERFULLCONTENT` (flag 2) is mandatory.** Flag 0 returns blank for four of five windows.

**F2. The BOOL return is worthless as a success signal.** `ret=True` on every call, including every
all-black one. Whatever failure handling this design chooses, it cannot be driven by the return value.

**F3. Console and RDP agree exactly** — 0.951 / 1 / 0.044 / 0.975 identical in both. The one differing
row is a display-resolution change (1920x1080 → 1366x768), not a transport difference. So the mechanism
does not depend on the session type, and the Desktop-suite console requirement adds nothing here.

**F4. A darkness-based blank detector is UNSOUND, and this was proven rather than reasoned.** The 0.044
row looked like a failure. The PNG was dumped and inspected: it is a pixel-perfect render of the occluded
window — nav rail, toggles, body text, the "Real-time protection is off" warning. 0.044 is a DARK THEME.
A darkness heuristic would have rejected a flawless capture.

**F5. Captures exceed the screen.** That 1920x1020 window was captured in full while the physical display
was 1366x768. `PrintWindow` returns pixels that are on no monitor. This is the fact that forces the
yardstick change below.

**F6. UIA `BoundingRectangle` equals `GetWindowRect` exactly — origin AND size — and does NOT equal the
DWM extended frame bounds.** Probe `.clavity/scratch/item8-panel/rect-probe.ps1`, run DPI-aware, compared
against `desktop_list_windows` at the same instant:

| Window | UIA bounds | `GetWindowRect` | `DWMWA_EXTENDED_FRAME_BOUNDS` |
|---|---|---|---|
| WindowsTerminal (maximized) | `-8,-8` 1936x1036 | `-8,-8` 1936x1036 | `0,0` 1920x1020 |
| PowerToys.QuickAccess (restored, WPF, has a shadow) | `966,204` 400x516 | `966,204` 400x516 | `973,204` 386x509 |
| explorer | `-32000,-32000` 160x28 | `-32000,-32000` 160x28 | 144x28 |

The invisible border is real — 7-8px per side — but UIA reports the **outer** rect, the same one
`GetWindowRect` reports and the same one `PrintWindow` sizes its bitmap from. So the two coordinate
systems agree and there is no shadow-induced mask offset. This measurement exists because the panel
raised the opposite claim; see the decision record.

## The change

### 1. Acquisition

Window scope and window+ref element scope acquire via `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`,
**replacing** the scrape. Full-desktop scope keeps the scrape — `PrintWindow` is inherently per-window and
has no full-desktop analogue.

This introduces the repo's first native interop of this kind: `grep -rn "PrintWindow|BitBlt|GetWindowDC"
src/` currently returns nothing.

#### The invariant that keeps masking correct — state it, do not assume it

`ScreenCapture.Encode` translates mask rects by `clip.X - captureBounds.X` (`ScreenCapture.cs:61-62`),
which is indeed keyed on `captureBounds` and not on the acquisition backend. But it also computes the
downscale factor from **`src.Width`** (`:48`). Both together mean masking is correct only while:

```
src.Size == captureBounds.Size          // and src's origin corresponds to captureBounds.X/Y
```

Under the scrape that invariant is **structural**: `Capture.Rectangle(absolute, null)` returns exactly
that rectangle, so it cannot be violated. Under `PrintWindow` it becomes an **assumption**, and for
element scope it is **false by construction**:

- `PerceptionManager.cs:883` — with a `@ref`, `target` is the resolved ELEMENT, not the window.
- `PerceptionManager.cs:915` — `captureBounds = target.BoundingRectangle`, so it is the ELEMENT's rect.
- `PrintWindow` is per-window: the bitmap is WINDOW-sized.

Hand that pair to `Encode` and the scale factor comes off the wrong width, every mask lands at the wrong
offset, and the returned `W/H` describe a rectangle the PNG is not. **The plan must crop the window
bitmap to `captureBounds` before `Encode`**, restoring the invariant explicitly.

**Two coordinate spaces meet at that crop, and the spec must not leave the conversion implied.**
*(Panel round 2, Fold Auditor.)*

- `captureBounds` is in **absolute screen** coordinates.
- The `PrintWindow` bitmap is a standalone image whose origin is **`(0,0)`**, corresponding to the
  window's top-left — which is `GetWindowRect.left/top`, and by F6 the window's own UIA origin.
- So the crop rectangle is `captureBounds` **offset by the window origin**:
  `(captureBounds.X - windowRect.X, captureBounds.Y - windowRect.Y, captureBounds.Width, captureBounds.Height)`.
- **`Encode` receives an ABSOLUTE rectangle**, not a window-relative one. Its mask arithmetic
  (`clip.X - captureBounds.X`) works on absolute rects and must keep doing so. Translating the value
  passed to `Encode` as well would double-apply the offset.

⚠ **CLAMP ONCE, THEN DERIVE BOTH FROM THE SAME RECTANGLE.** This is the rule, and it is stated this way
because the obvious two-sentence version of it is wrong — an earlier draft of this spec said "intersect
the crop with the bitmap" and "`Encode` still receives the unchanged `captureBounds`" in the same
breath, which silently reintroduces the exact misalignment this subsection exists to prevent. If the
clamp shrinks the crop and `Encode` is still handed the original larger rect, the scale factor comes off
the smaller `src.Width` while masks translate against the larger origin. Same bug, new cause.

So:

```
effective = Intersect(cropRectRelativeToWindow, new Rectangle(0, 0, bitmap.Width, bitmap.Height))
src       = bitmap cropped to `effective`
absolute  = effective offset back by the window origin
Encode(src, absolute, masks, maxWidth)     // src.Size == absolute.Size, by construction
```

`CaptureResult`'s `X/Y/W/H` then honestly describe the region actually captured, which is what a caller
needs when a resize truncated it. *(Panel round 3, Fold Auditor — a defect introduced by round 2's own
fix.)*

⚠ **The window rect is not currently available at that site.** `CaptureGeometry`
(`PerceptionManager.cs:1275`) carries `Bounds` — which for element scope IS the element rect — and no
window rectangle. The plan must plumb the window rect through. `CaptureGeometry` is a positional record,
so the same append-only rule applies as for `CaptureResult`.

⚠ **Why the clamp is required at all.** §2 already documents that a window moving mid-walk leaves live
mask rects against a stale capture rect, and calls that race inherent. Under the scrape a stale rect
degrades the image; under a mandatory crop a stale rect that is now LARGER than the bitmap is an
out-of-range operation.

**MEASURED on this runtime** (`.NET 10`, `System.Drawing`): `Bitmap.Clone(rect, format)` with a rect
outside the source throws **`ExternalException`** — "An object could not be created, possibly due to a
lack of memory, but most likely due to invalid input" — for an oversized rect, an overhanging rect and a
negative-origin rect alike; an exactly-fitting rect does not throw.

That is not the harmless outcome it looks like. `ScreenCapture.cs:40` already catches
`COMException or ExternalException` and maps it to `ToolErrorCode.CaptureUnavailable` with the hint
"reconnect to restore rendering". So without the clamp, **a window being resized mid-capture is reported
to the agent as a disconnected or locked session** — a confident, actionable, and completely wrong
diagnosis. Clamp, and the case never arises. *(Panel round 2, State Corruptor; the exception type
corrected in round 3 after the peer flagged the original claim as unverified — it was, and it was wrong.
The earlier draft said `OutOfMemoryException`, which would have escaped this repo's CRITICAL filters at
`PerceptionManager.cs:891-892`. It does not, on this runtime.)*

For WINDOW scope the invariant holds, and F6 is what establishes it: `captureBounds` is the window's UIA
rect, which equals the `GetWindowRect` the bitmap is sized from.

So "masking is untouched" is true — but only because of a precondition that the scrape guaranteed for
free and this backend must re-establish by hand. *(Panel round 1, AB-2.)*

#### DPI

The server is `PerMonitorV2` DPI-aware (`src/FlaUI.Mcp.Server/app.manifest:5`), so `GetWindowRect` returns
physical pixels that match `geo.Bounds`. The probe reproduced this: run DPI-aware, its sizes matched
`desktop_list_windows` exactly; run DPI-unaware they were scaled to 80%.

### 2. The yardstick correction — a leak if skipped

`PerceptionManager.cs:930-947` computes:

```csharp
var yardstick = System.Drawing.Rectangle.Intersect(captureBounds, ScreenCapture.VirtualScreenBounds());
```

and then early-returns when that intersection is empty, on this stated premise:

> ⚠ A window with NO renderable overlap contributes no pixels to any capture, so there is nothing here to
> withhold and nothing to refuse over.

**F5 falsifies that premise under `PrintWindow`.** Pixels outside the virtual screen ARE captured. Left
unchanged, the early return would drop the entire mask set for a window whose pixels are in the image —
a leak of exactly the class SP4 existed to close.

**Under `PrintWindow` the yardstick is the UNCLIPPED `captureBounds`.** The clipping exists to stop a
maximized window's invisible resize-border bleed from defeating the blacks-out check; that reasoning is
specific to a scrape, where off-screen pixels genuinely cannot appear.

⚠ The two acquisition paths now need DIFFERENT yardsticks — clipped for the full-desktop scrape,
unclipped for `PrintWindow`. The method cannot infer which caller it serves; that is already true of the
`skipIfNoRenderableOverlap` parameter beside it, and the plan follows that existing precedent rather than
inventing a new mechanism.

⚠ **The new parameter's default MUST be the mask-preserving one (unclipped).** This is not a style
preference — it is inherited from the precedent being cited. `skipIfNoRenderableOverlap` already defaults
to `false`, and `PerceptionManager.cs:958` says why: the window-scoped value is the safe one, so a
forgotten call site fails safe. A parameter defaulting the other way would let a missed call site silently
reinstate the leak this whole section exists to close. *(Panel round 1, AB-3.)*

### 3. Failure policy — no gate, a diagnostic with recourse

Given F2 (the API cannot report failure) and F4 (the obvious detector is unsound), the design does **not**
attempt to gate on image content.

- Return whatever `PrintWindow` produced.
- Emit a **diagnostic** when the full window bitmap is effectively a single colour.
- **Never refuse** on that signal.

**The diagnostic is ALWAYS PRESENT, not conditional.** There is no warning channel in any tool response
today — `ToolResponse.Image(png, metadata)` takes an anonymous object of named fields — so this follows
the AB-9 precedent already in this same method:

> ⚠ AB-9: ALWAYS present, empty when nothing was skipped … a diagnostic that appears only on failure
> teaches consumers to ignore its absence.

**But a bare boolean is not enough, and the spec previously contradicted itself on this.** Section 3 said
"a boolean, not a sentence"; the rationale below said the warning's value is "telling the agent to fall
back to the tree is guidance it would otherwise lack". A silent `true` conveys no such instruction. A flag
whose meaning is never stated is a flag the consumer ignores — which is the same failure mode AB-9 exists
to prevent, arriving by a different route.

**So the diagnostic carries its RECOURSE, not just its state.** The repo already does this everywhere it
matters: every `ToolException` carries an actionable hint string, and the installer's
`ManualEnableRecourse` is literally a recourse sentence. The metadata field follows that convention.
*(Panel round 1, MG-1 — raised by the agy seat, and it is a genuine internal contradiction.)*

⚠ **The tool description must ENUMERATE the new fields.** `ScreenshotTools.cs:17` lists the metadata
contract explicitly — `{bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses}`.
A field in the payload but absent from that list is one the model has no reason to read, and the whole
diagnostic then costs effort and changes nothing. *(Panel round 1, PP-2.)*

⚠ **The exact predicate is the PLAN's to settle, by measurement** — but its PURPOSE is settled here, or
the plan has nothing to measure against: the detector exists to tell an agent that this image may not be
usable and that the UIA tree is the fallback. It is not a correctness gate and must not grow into one.
*(Panel round 1, LI-1.)*

⚠ **And its ACCEPTANCE CRITERIA are settled here too, or "measure it" is an observation rather than a
test.** The predicate and its sampling strategy pass if, and only if:

- it classifies the F1 flag-0 blank renders as **true**, and
- it classifies the F4 row — the 0.044 non-black, dark-themed, pixel-perfect capture — as **false**, and
- it does not measurably change capture latency. It runs over a bitmap that is already allocated and
  already being encoded; a detector that costs real time has chosen the wrong sampling strategy.

Those three are checkable against evidence this spec already holds, so the plan can pass or fail against
them rather than merely reporting a number. *(Panel round 2, direct question 3.)*

⚠ **"Always present" is scoped to image responses.** A hard failure (§4) returns a `ToolException` and no
image at all, so there is no metadata object to carry the field. The field is always present whenever an
image is returned — it is never conditional on the image being GOOD. *(Panel round 2 raised this as an
unsure finding; it is a clarification, not a contradiction.)*

**Why not refuse.** The instinct comes from SP4's `RedactionUnmaskable`, which refuses rather than
returning an all-black image (`PerceptionManager.cs:894`). That precedent does **not** transfer, and the
code says why at `:902-905`:

> So a raw COMException escaping here does not fail the capture — it silently drops this window's ENTIRE
> mask set and photographs it in the clear. **That is a leak.**

That refusal protects a **privacy guarantee**. A blank `PrintWindow` render exposes nothing. The hazard
that mandated refusal is absent, so importing its posture would buy nothing and cost a false-refusal mode.

**The asymmetry that decides it.** A blank image is *recoverable* — the agent can fall back to
`desktop_snapshot` for the UIA tree. A false refusal is a *hard block* on capturing that window at all.
An incorrect warning is cheap; an incorrect refusal is not.

⚠ **The detector runs on the FULL window bitmap, BEFORE any element crop.** A legitimate crop of a solid
panel — a spacer, a blank text area, a flat background — is genuinely one colour. Evaluating the signal
post-crop would warn constantly on valid captures.

**The two errors that choice accepts, stated symmetrically:**

- **False POSITIVE:** a window that is genuinely one uniform colour (a colour-calibration app, a black
  loading screen) is warned about incorrectly. Accepted — the consequence is a spurious sentence.
- **False NEGATIVE:** an ELEMENT crop that is entirely black inside a window that rendered fine — a
  hardware-accelerated child viewport that failed to draw — passes with the flag `false`, because the
  detector never saw the crop. Accepted for the same reason the pre-crop choice was made, but it is the
  price of that choice and the spec previously stated only the other half. *(Panel round 1, BA-1, agy
  seat.)*

### 4. Failure mapping — the existing error path does not reach the new backend

`ScreenCapture.cs:40-41` maps `COMException`/`ExternalException` from `Capture.Rectangle` onto
`ToolErrorCode.CaptureUnavailable`, with the actionable hint "reconnect to restore rendering". **GDI does
not throw.** `CreateCompatibleDC`, `CreateCompatibleBitmap` and `SelectObject` return null/zero handles on
failure, so on the new path that catch never fires and nothing else takes its place.

The plan owns an explicit mapping. Null-handle checks are not optional — given ROADMAP item 13 (108 bare
catches) the realistic outcome of skipping them is an NRE surfacing as something unhelpful, or a garbage
bitmap returned as a capture. *(Panel round 1, CA-2.)*

The session-level guard is unaffected and still runs first: `ScreenshotTools.cs:27-28` throws
`CaptureUnavailable` when `IsDesktopRenderable()` is false, which covers the locked/disconnected desktop
before either backend is reached.

### 5. Contract changes

Two fields join the JSON metadata, both ALWAYS present:

| Field | Meaning |
|---|---|
| `captureMethod` | which backend produced the image. Window/element report the `PrintWindow` path; full-desktop reports the scrape |
| the uniform-canvas diagnostic (§3) | `false` on a normal capture; when true, carries its recourse |

`captureMethod` matters because the two scopes now produce images by different mechanisms, and a caller
comparing them needs to know which it holds.

⚠ **Enumerate `captureMethod`'s values in the spec-to-plan handoff.** A contract field with an
unspecified value set cannot be branched on. Two values, spelled out. Casing follows the existing metadata,
which is camelCase throughout (`ScreenshotTools.cs:71-83`). *(Panel round 1, PP-4.)*

Both are carried on `CaptureResult`, which today is:

```csharp
// ScreenCapture.cs:9 — CURRENT shape; the plan adds the two fields above
public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions);
```

⚠ **Exact field names and types are the PLAN's**, but the parameter order is NOT open: `CaptureResult` is
a POSITIONAL record, constructed positionally at `ScreenCapture.cs:69`. **Append only, never insert** — a
field added mid-list silently rebinds arguments wherever the types happen to line up. *(Panel round 1,
PP-3.)*

#### `bounds` stops supporting image→screen coordinate mapping

`ScreenshotTools.cs:60,73` publish `bounds{x,y,w,h}` and a `dpiScale` derived from `result.X, result.Y`.
A consumer maps image coordinates to screen coordinates through those, then clicks.

Under `PrintWindow` that mapping is **wrong for exactly the windows this feature exists to serve**. An
occluded window's pixels are at those screen coordinates only in the sense that something else is drawn
there, so a click computed from the image lands on the OCCLUDER. F5 is the extreme case: the coordinates
may be on no monitor at all.

`captureMethod` is necessary but not sufficient. The contract must say plainly that image-derived
coordinates are not clickable when the method is `PrintWindow` — act through the UIA tree instead.
*(Panel round 1, PP-1.)*

#### Documentation that goes stale with this change

The tool description loses **"Focus the window first (no occlusion handling)"** — it stops being true, and
a stale instruction in a tool description is read by every agent on every call.

The same claim also sits in the class doc at `ScreenCapture.cs:11-14` ("no occlusion handling — callers
focus-first"), and that comment's closing promise — "Headless/disconnected sessions are detected before
capture so we never hand back a black frame" — is exactly what stops being guaranteed. Both are in the
plan's edit list. *(Panel round 1, LI-3.)*

## Out of scope

- **Full-desktop scope.** Keeps the scrape.
- **Minimized windows.** Still `ElementNotActionable`. `PrintWindow` on a minimized window is unreliable,
  and nothing in item 8 requires changing it.
- **A scrape fallback when `PrintWindow` disappoints.** Rejected — **on error-budget grounds, which is a
  different and better reason than the one this spec originally gave.** The original wording said the
  choice "needs the unsound detector F4 rules out"; that is now imprecise, because §3 gives the detector
  explicit acceptance criteria. The exclusion still stands, and here is why: §3's error budget is
  calibrated to a CHEAP consequence — a false positive costs a spurious sentence. Reusing the same signal
  to SELECT A BACKEND re-prices every error in it. A false positive would then silently return the
  scrape, which for an occluded window is a photograph of the occluder — the exact defect item 8 exists
  to fix, reintroduced by the mechanism meant to protect against it. A detector good enough to annotate
  is not thereby good enough to switch on. *(Panel round 3, Scope Auditor: the flaw in the old rationale
  was real and is corrected; the conclusion it implied — that the exclusion should be revisited — is
  rejected.)*
- **ROADMAP item 13** (bare catches) and any redaction-rule change.

## Privacy posture — an unstated widening, named here so it is ratified deliberately

Today a screenshot can only contain pixels that were actually on the physical screen. That is the model
the entire redaction design was built against.

After this change, `desktop_screenshot` can extract the current contents of a window that is **fully
covered, moved off-screen, or on another virtual desktop** — content the human at the console cannot see
and has no cue is being read. That is the feature working as intended, and it is why the feature is worth
building. It is still a posture change.

The existing guards continue to hold: `geo.Denied` blocks denylisted processes (`ScreenshotTools.cs:54`),
and the full-desktop denylist refusal is untouched. So this is not a hole. It is a widening the operator
should accept knowingly rather than inherit silently. *(Panel round 1, BS-1.)*

## Testing

- **Headless** cannot exercise `PrintWindow` — it needs a real window. The seam makes this tractable:
  acquisition becomes injectable, so mask/geometry logic stays headless-testable and only the interop is
  Desktop-category.
- **The yardstick correction needs a headless test** proving an off-screen window's masks SURVIVE under
  the `PrintWindow` yardstick and are dropped under the scrape yardstick. This is the leak-shaped case and
  must not be Desktop-only.
- **The element-crop invariant needs a headless test** proving that a window-sized bitmap cropped to an
  element-sized `captureBounds` puts masks in the right place — the AB-2 case. Also leak-shaped, also not
  Desktop-only.
- **The clamp path needs a headless test** — a `src` bitmap SMALLER than the requested `captureBounds`,
  asserting masks still land correctly and nothing throws. Cheap to stage once acquisition is injectable,
  and it is the regression test for the defect round 2's fix introduced.
- **Desktop-category:** capture an occluded window and assert it is not the occluder's pixels. The probe
  shows this is stageable — a second window over the target, then compare against a known control.
- **Every new gate needs a logic mutant** that turns that specific test red. Repo rule, no exceptions.

⚠ **Three failures in this design have NO test that would go red. Named, because an untested guard is a
guard that silently stops working.** *(Panel round 3, Test Oracle. The seat was asked which test goes RED
for each failure, or NONE — not whether the area was "covered".)*

1. **The yardstick parameter defaulting the wrong way at a forgotten call site.** No test of
   `PerceptionManager`'s yardstick logic can catch a CALLER omission — and §2 makes that default the
   thing standing between this feature and a mask-dropping leak. This needs a **call-site sweep**, which
   is an idiom the repo already owns: `BuildPropertySweepTests` sweeps project files, and item 6 shipped
   JSON-shape tripwires. A sweep asserting every capture-geometry call site passes the parameter
   explicitly turns "someone forgot" from silent into red.
2. **The clamp path.** Covered by the new headless test above; it did not exist before this round.
3. **`captureMethod` or the diagnostic missing from the response.** No contract test asserts the metadata
   projection's shape. The precedent to follow is
   `test/FlaUI.Mcp.Tests/Perception/ListWindowsProjectionShapeTests.cs` — a projection-shape tripwire, so
   a field silently dropped from the anonymous object fails a test rather than reaching an agent as an
   absent field it has no way to notice.

## Risks, and what the plan must verify before relying on them

1. **App classes beyond the five measured.** The probe covered DirectX, Win32, XAML/UWP, WPF and the
   shell. Electron/Chromium was NOT measured — VS Code was not running. **The plan must measure at least
   one Chromium-family window before this ships**, since it is a common agent target.
2. **`PrintWindow` depends on the TARGET's message loop, and nothing here bounds that wait.** It renders
   by sending `WM_PRINT`/`WM_PRINTCLIENT` synchronously to the target window. The scrape has no such
   dependency — it reads the composited desktop and a hung app cannot block it. Consequences: a hung or
   busy target blocks with no timeout and no cancellation; `ScreenshotTools.cs:58` puts that block on a
   THREADPOOL thread which is never returned; and a blocked call never reaches its `using`, so it holds an
   HDC, a GDI bitmap and a managed bitmap permanently. "Occluded" and "not pumping" are correlated in
   practice, so the feature's primary use case is also its worst failure case.
   **UNMEASURED — this is documented Win32 behaviour, not a probe result, and this spec's standard is
   measured-not-assumed.** The plan must stage a window that stops pumping (a fixture with a blocking
   sleep on its UI thread), call `PrintWindow` against it, and time the call. If it blocks, the design
   needs a bounded wait and a decision about what to return on timeout. *(Panel round 1, CA-1 + RV-2.)*
3. **Stale composition.** `PW_RENDERFULLCONTENT` may return the last frame DWM composed rather than
   forcing a fresh render. If the pixels are older than the UIA tree the masks were computed from, masks
   can miss data that IS in the image — a leak with a different shape from the ones above.
   **UNMEASURED, and the experiment is specified:** drive a Chromium-family window to change a sensitive
   region's state (reveal/hide a password field, switch tabs), then call `PrintWindow` and walk the UIA
   tree with zero delay. TRUE if the image shows the old state while the tree reports the new one; FALSE
   if they always agree. This can be folded into risk 1's Chromium measurement — same window, same probe.
   *(Panel round 1, agy Q2. The peer raised it and said plainly it could not determine the answer without
   running the probe, which is the correct answer.)*
4. **`Capture.Rectangle` also handles the DC lifecycle** that the new path must own: `CreateCompatibleDC`,
   `CreateCompatibleBitmap`, `SelectObject`, and their release. A leak here runs inside a long-lived
   server. The plan owns the exact ownership pattern.
5. **The uniform-colour detector's sampling** — full-bitmap versus a grid — is a cost/accuracy tradeoff
   the plan settles, with a measurement, not a guess.
6. **`ScreenCapture.CaptureRectangle` has one other caller** (full-desktop, `ScreenshotTools.cs:49`). The
   plan must confirm the signature change does not alter its behaviour — and must SPECIFY that change,
   which this spec deliberately does not: whether the new path is a parameter, an overload, or a separate
   method is a decomposition decision the plan makes explicitly rather than by implication.
   *(Panel round 1, LI-2.)*
7. **The width ceiling bounds the payload, never the allocation.** `MaxCaptureWidth = 1920`
   (`ScreenCapture.cs:17`) is applied inside `Encode` (`:47-50`), after the source bitmap exists. Under
   the scrape that changed nothing; under `PrintWindow` the allocation is dictated by the window's own
   size, so a 4K-wide window is a ~33 MB managed bitmap plus its GDI twin, per capture, in a long-lived
   server. Not a defect — a property the plan should state rather than discover. *(Panel round 1, RV-1.)*
8. **Popup masks and popup pixels come apart.** This repo grafts masks from popup roots into a window's
   mask set (`PopupFinder.SearchRoots`, pinned by `PopupRootCoverageTests`). A popup is a SEPARATE
   top-level HWND: the scrape includes its pixels and the grafted mask covers them, but `PrintWindow`
   renders one window and its CHILD windows, so the popup's pixels are absent while its mask rect
   survives. The result is a black rectangle over ordinary window content and a `redactions` count that
   corresponds to nothing visible. The direction is fail-safe — over-masking, never under-masking — so
   this is not a leak, but it is a visible behaviour change and "masking is untouched" is not true of it
   at the semantic level. *(Panel round 1, BS-2.)*

## Decision record

**AGY-FIRST consulted at the approaches fork.** agy first recommended refusing on a unique-ARGB-count
signal (Option C). The operator invoked AGY-NEGOTIATE; the peer was asked to read
`PerceptionManager.cs:865-905` and report what the refusal there protects. It concluded the precedent was
leak-specific, withdrew Option C, and proposed the synthesis adopted above — demote the detector from a
gate to a warning, because "an incorrect warning is cheap; an incorrect refusal is fatal".

⚠ **One of its supporting claims was REFUTED by measurement and must not be re-imported.** It argued a
blank capture is cheap because `Encode` "will still overlay the red UIA bounding boxes… a structurally
perfect, clickable wireframe". No such overlay exists: `ScreenCapture.cs` contains exactly one drawing
operation, `g.FillRectangle(black, rel)` — solid black redaction rects, no `DrawRectangle`, no
`DrawString`, no numbering. The conclusion survives on the recoverable-versus-blocked asymmetry instead,
and the refutation strengthens the warning's value: with no free wireframe, telling the agent to fall back
to the tree is guidance it would otherwise lack.

⚠ **agy's unique-colour signal was never verified** — the probe that would have measured it was stopped.
If the plan wants to use unique-colour count as the warning trigger, **it must measure the claim first**:
that a failed render yields exactly one unique ARGB value while a dark-themed success yields hundreds.

### AGY-AFTER adversarial panel — round 1

Solo floor (`.clavity/scratch/item8-panel/solo-round1.md`) plus agy escalation
(`.clavity/scratch/item8-panel/agy-round1.md`). Seats: Axiom Breaker, Cascade Analyst, Boundary Smuggler,
Resource Vampire, Protocol Pedant, Literal Implementer (solo); Blindspot Auditor, Dependency Cynic,
Mechanism Gamer (agy). **Verdict: NOT GREEN.** Fifteen findings folded above, each tagged at its site.

**Rejected by measurement, do NOT re-raise:**

- **The DWM shadow mask-shift leak** — the peer's headline finding, and its answers to three of the four
  open questions rested on it. It argued `GetWindowRect` includes an invisible ~7px border while UIA
  tracks the visible frame, shifting every mask. **F6 refutes it:** UIA reports the OUTER rect, identical
  to `GetWindowRect` in origin and size, on maximized, restored-with-shadow, and minimized-placeholder
  windows alike. The repo's own comment at `PerceptionManager.cs:927-930` says the same thing — the UIA
  rect is the one that bleeds past the monitor. Shown the file and asked what it concluded, the peer read
  it and withdrew the finding.
- **"Two yardsticks means `clip` may be constrained by `VirtualScreenBounds`, misaligning masks."** Wrong
  mechanism: `clip` is `Rectangle.Intersect(r, captureBounds)` computed inside `Encode`
  (`ScreenCapture.cs:60`) and has nothing to do with the yardstick, which only gates the blacks-out and
  escalation decisions in `PerceptionManager`. The real yardstick hazard is the default-direction one
  folded into §2.
- **`PW_RENDERFULLCONTENT` needs Windows 8.1+.** Moot: the TFM is `net10.0-windows10.0.19041.0`.
- **Metadata might need snake_case.** It does not; `ScreenshotTools.cs:71-83` is camelCase throughout.

**Process note worth keeping.** The peer's single most confident finding was false, and its most valuable
one (§3's diagnostic contradiction) was raised almost in passing. Its answer to "name the failure mode of
your own design" was the false one restated. Score the claim and the evidence separately — and give the
peer the FILE rather than your measurement: pointed at `PerceptionManager.cs:927-930` and asked what it
concluded, it reversed itself and cited the line that did it.

### AGY-AFTER adversarial panel — round 2

Seats: State Corruptor (its trigger did not fire until round 1's folds introduced a two-source staleness
relationship), Fold Auditor (aimed only at round 1's eight edits, enumerated), Disposition Challenger
(aimed at the four round-1 rejections rather than at the artifact). Report:
`.clavity/scratch/item8-panel/agy-round2.md`. **Verdict: NOT GREEN.** Three findings folded:

- The element crop's absolute→relative coordinate translation was unstated (§1). Verified and found to be
  worse than reported: `CaptureGeometry` does not carry a window rect at all, so the operand is missing,
  not merely the arithmetic.
- The mandatory crop converts §2's known, accepted movement race from a soft degrade into a hard throw,
  and `Bitmap.Clone`'s out-of-range exception is `OutOfMemoryException`, which this repo's catch filters
  deliberately treat as CRITICAL and pass through (§1).
- The detector's deferral had a purpose but no pass/fail criteria, so the plan could measure without
  being able to conclude (§3).

The Disposition Challenger seat found no wrong rejection and re-derived F6's reasoning independently,
reaching the same conclusion from the file. That is corroboration, but a seat aimed at the driver's own
judgement returning nothing is the weakest signal in the round, not the strongest.

### AGY-AFTER adversarial panel — round 3

Operator waived the round cap: rounds run until one is clean. Seats: Fold Auditor (round 2's three edits,
enumerated), Test Oracle (the Testing section, which nothing had reviewed), Scope Auditor (the Out-of-scope
list, likewise). Report: `.clavity/scratch/item8-panel/agy-round3.md`. **Verdict: NOT GREEN.** Four folds:

- **Round 2's fix reintroduced the defect it was fixing.** "Intersect the crop with the bitmap" and
  "`Encode` still receives the unchanged `captureBounds`" cannot both hold: a clamped crop with an
  unclamped bounds is precisely the `src.Size != captureBounds.Size` misalignment §1 exists to prevent.
  Replaced with a single rule — clamp once, derive both operands from that rectangle — because the
  two-sentence form of this instruction has now been got wrong twice.
- **A bare factual claim of this spec's own was refuted by measurement.** The peer answered the
  "name something you did not verify" question with the `Bitmap.Clone` exception type. It was right to
  flag it: measured on .NET 10 it throws `ExternalException`, not `OutOfMemoryException`. The correction
  makes the hazard sharper rather than softer — see §1.
- **Three failure modes had no test that would go red**, including the yardstick default, which is the
  single thing standing between this design and a mask-dropping leak. All three now named in Testing.
- **The scrape-fallback exclusion's rationale was stale** and is restated on error-budget grounds. The
  exclusion itself stands; the peer's implied conclusion that it should be revisited is rejected.

**This is the third consecutive round in which a fix spawned its own defect.** Rounds 1, 2 and 3 each
found a defect created by the previous round's correction. The fold-auditor seat is doing the work here,
and it is the reason the round cap was worth waiving.
