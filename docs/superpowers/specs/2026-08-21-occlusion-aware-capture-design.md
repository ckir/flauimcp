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
row is a display-resolution change (1920x1080 → 1366x768), not a transport difference. So the rendering
does not depend on which TRANSPORT is presenting an active session, and the Desktop-suite console
requirement adds nothing here.

⚠ **F3 does NOT establish independence from session STATE.** Both endpoints measured were ACTIVE
sessions; a locked or disconnected desktop was never measured, and transport parity does not generalise
to it. That gap is closed by a guard rather than by evidence: `ScreenshotTools.cs:27-28` refuses with
`CaptureUnavailable` when `IsDesktopRenderable()` is false, before either backend is reached. So the
unmeasured case is unreachable, not assumed away. *(Panel round 4, Evidence Auditor — the only row of the
F1-F6 census that needed more than it had.)*

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

⚠ **Read this subsection as the single source for the crop. It has been rewritten as one block on
purpose.** Six review rounds each corrected it by appending a paragraph, and the appended correction
twice failed to reach the instruction it was correcting — leaving a superseded version sitting above a
fix that a reader working top-to-bottom would implement before ever seeing it. There are no earlier
drafts left here to follow by mistake.

**Notation, used consistently below.** Three observations, and which one you use is the whole game:

| | |
|---|---|
| `W1` | the WINDOW rect as observed during the UIA walk |
| `E` | the element rect from that SAME walk — this is `captureBounds`, absolute screen coordinates |
| `W2` | the `GetWindowRect` taken at capture time, which sizes the bitmap |

The `PrintWindow` bitmap is a standalone image whose `(0,0)` corresponds to `W2`'s top-left — which is
`GetWindowRect.left/top`, and by F6 the window's own UIA origin.

**The algorithm — BOTH SCOPES, one path.** `E` is the element rect for `window+ref`, and **`E = W1` for
window scope**. There is no window-scope special case:

```
relative  = E offset by W1.Location          // (E.X - W1.X, E.Y - W1.Y, E.Width, E.Height)
                                             // window scope: E == W1, so this is (0, 0, W1.W, W1.H)
effective = Intersect(relative, new Rectangle(0, 0, bitmap.Width, bitmap.Height))
if (effective.Width <= 0 || effective.Height <= 0) -> refuse           // see the empty case below
src       = bitmap cropped to `effective`
absolute  = effective offset back by W1.Location
Encode(src, absolute, masks, maxWidth, method, warnings)   // src.Size == absolute.Size, by construction
```

`Encode` constructs the `CaptureResult`, so the two new fields reach it the same way every other field
does — as parameters. `method` is the backend that produced `src`; `warnings` is the list accumulated by
the checks in §1 and §3, empty in the normal case. *(Panel round 15, Fold Auditor: the block still
showed the four-argument call while §5 had already settled a nine-field result, leaving the bridge
between the detector logic and the constructor unstated.)*

⚠ **Window scope is cropped too, and that crop is load-bearing rather than a no-op.** Earlier drafts
exempted window scope on the grounds that the bitmap already IS the window. That is true only while
`W1.Size == W2.Size`, and the exemption caused three separate defects across rounds 8, 9 and 10 — each
fixed by adding another window-scope-only rule beside the general one. Running both scopes through the
same six lines removes the class:

- **A window that GREW between the two reads no longer leaks.** `W2` is larger than `W1`, so the bitmap
  contains a region the UIA walk never inspected — pixels no mask was ever computed for, which could be
  anything. `Intersect` discards exactly that region, because `relative` is `W1`-sized. Without the crop
  the design would return unscanned pixels and call it a success. *(Panel round 10, Fold Auditor: this
  leak was invisible while window scope had its own rule.)*
- **A window that SHRANK is clamped**, as element scope already was, so nothing reads past the bitmap.
- **A pure MOVE stays harmless**, because `W1` anchors both translations for both scopes.
- **`src.Size == absolute.Size` holds by construction on every path**, which is the invariant the whole
  subsection exists to protect — now with no branch that can violate it.

⚠ **A DEGENERATE WINDOW is a separate case, applies to BOTH scopes, and must be checked at TWO points.**

- **On `W2`, where the bitmap is created.** Zero or negative extents mean there is nothing to allocate —
  `PrintWindow` has nothing to render into — so the capture cannot proceed. Refuse.
- ⚠ **On `W1`, at the GEOMETRY stage, BEFORE the yardstick is computed.** This one is a leak, not a
  crash. §2 makes the yardstick the unclipped `captureBounds` under this backend; if `W1` is itself
  degenerate then the yardstick is degenerate, and `PerceptionManager.cs:947`'s window-scoped branch
  falls back to `yardstick = captureBounds` — still degenerate. Every mask is then judged against a rect
  nothing intersects, the whole set is dropped, and the repo's own comment at `:942-946` names the
  outcome: *"every mask dropped, capture returned unmasked. A guard producing a leak."* If the window
  then returns to a valid size before capture, `W2` is fine, the `W2` guard passes, and an **unmasked
  image is returned as a success**. Ordering is the whole defence: reject a degenerate `W1` before the
  yardstick can turn it into an empty mask set. *(Panel round 9, Fold Auditor.)*

  ⚠ **This guard is DEFENCE IN DEPTH, and it is kept knowingly rather than because nothing else covers
  the case.** Traced against the rest of the design, every path from a degenerate `W1` does already end in
  a refusal: window scope's crop goes empty (`E = W1`), and element scope hits the size-mismatch rule
  because a degenerate `W1` cannot equal a valid `W2`. So it is redundant, and an earlier draft's
  justification for it — that element scope had an uncovered path — was wrong. *(Panel rounds 11 and 13,
  which traced it correctly.)*

  It stays anyway, for one reason: **what it guarantees is that a DROPPED MASK SET never reaches a
  caller, and that guarantee should not rest on a three-rule interaction.** Those three rules have each
  been rewritten during this review, one of them three times. A single check at the source costs one
  comparison and cannot be invalidated by a later change to the retry, fallback or crop logic — all of
  which sit downstream of it and all of which have changed since it was added. That is the correct trade
  for a leak-class guarantee, and it is a judgement call rather than a necessity, which is why it is
  labelled as one.

⚠ **A MINIMIZED window is NOT caught by an extents check, and must be re-tested at capture time.** F6
measured the placeholder rect Windows gives a minimized window: `-32000,-32000` with extents `160x28` —
**positive**. `ScreenshotTools.cs:55` already refuses `geo.Minimized` before capture, but that test
happens at geometry time; a window minimized in the interval passes the extents guard and yields a
160x28 placeholder that is not the window's content at all. Re-check the minimized state where `W2` is
taken, and refuse there too. *(Panel round 9, Adversary of the Reviewer — asked which fold was most
likely wrong, it named round 8's degenerate guard and produced the repository's own F6 row as the
counter-example. It was right.)*

That refusal is **not** an exception to "window scope captures and warns on a resize" below. That rule
governs a window that resized to a different VALID size, where the pixels are real and merely newer than
expected. A window with no extents has no pixels at all, and the two cases must not be conflated.
*(Panel round 8, Integration Auditor: it read the algorithm's refusal as contradicting the window-scope
policy. The refusal is element-scoped, which the block now says — but tracing that surfaced a real gap,
because nothing guarded the degenerate window on either path.)*

**Three rules that block is enforcing, each of which was a defect in an earlier draft:**

1. **`W1` on BOTH translations, never `W2`.** The element rect and the origin that translates it must
   come from the same observation, going in and coming back out. Use `W2` on the way in and a pure MOVE
   misaligns the crop by the movement delta; use `W2` on the way back and the masks land off-target by
   the same amount, because `Encode`'s arithmetic is absolute and the masks were sampled alongside `E`.
   *(Panel rounds 5-7. Round 7's fold auditor traced it: window at `(-8,-8,1936,1036)` moving to
   `(92,92,...)` with an element at `(100,200,300,50)` gives `absolute = (200,300)` under `W2` against
   masks at `(100,200)` — every mask 100px off.)*
2. **`Encode` gets an ABSOLUTE rectangle**, not a window-relative one, because its mask arithmetic
   (`clip.X - captureBounds.X`) is absolute and the masks arrive as absolute screen rects.
3. **Clamp once, then derive both operands from that one rectangle.** An earlier draft said "intersect
   the crop with the bitmap" and "`Encode` still receives the unchanged `captureBounds`" in the same
   breath: the scale factor then comes off the clamped `src.Width` while masks translate against the
   unclamped origin — the very misalignment this subsection exists to prevent, reintroduced by its own
   fix. `absolute` is derived FROM `effective`, so it cannot drift from it.

`CaptureResult`'s `X/Y/W/H` then describe the region actually captured rather than the region that was
requested. *(Panel round 3, Fold Auditor — a defect introduced by round 2's own fix.)*

⚠ **Why absolute coordinates are load-bearing here, since it is NOT that the caller clicks them.**
`Encode`'s mask arithmetic is absolute (`clip.X - captureBounds.X`) and the masks arrive as absolute
screen rects, so the rectangle handed to `Encode` must be absolute or every mask is wrong. That is the
requirement. The reported `bounds` must then match it because a `bounds` that describes a region the
pixels are not is simply a false statement in the response. **Neither reason is "so the caller can map
image coordinates back to the screen" — §5 explains why the caller must not do that at all under this
backend.** *(Panel round 5, Contradiction Hunter: §1 previously justified this math by a caller need that
§5 forbids. The math stays; the justification was wrong.)*

⚠ **`effective` can be EMPTY, and that case needs a defined outcome — not a crash.** If the window shrinks
enough between the UIA read and the capture, a stale element rect can fall entirely outside the new
bitmap. **MEASURED on this runtime:** `Bitmap.Clone` with a zero-width, zero-height, or zero-both
rectangle throws **`ArgumentException`**, which `ScreenCapture.cs:40` does NOT catch — its filter is
`COMException or ExternalException`. So the unguarded case escapes as a raw, unmapped exception.

**The requirement is behavioural: the guard must reject a rectangle with no AREA, including one whose
coordinates are non-zero.** The concrete form follows, and it is named rather than left open because this
repo has already shipped the wrong form of this exact guard once.

**Guard on EXTENTS, not `IsEmpty`.** This repo has already been bitten by exactly that distinction and
documents it at `PerceptionManager.cs:942-946`: `Rectangle.Intersect` yields a zero-extent rect at
NON-ZERO coordinates for rects that merely touch along an edge, and `IsEmpty` is false there. The
existing yardstick guard at `:947` tests `Width <= 0 || Height <= 0` for that reason; this one does the
same. (Measured: fully disjoint rects do give `IsEmpty=true` at `0,0` — which is precisely why testing
`IsEmpty` looks correct until the touching case arrives.)

**The outcome on empty is a defined refusal, not a degraded image.** The named element is not in the
pixels that were captured, so there is nothing honest to return. A `1x1` placeholder or a silently
substituted window capture would both be graceful-looking wrong answers, which this project rejects on
principle. Refuse with a retry hint naming the cause.
*(Panel round 4, Fold Auditor — a defect introduced by round 3's own fix.)*

⚠⚠ **THE CLAMP PREVENTS A CRASH. IT DOES NOT DETECT A RESIZE — and must not be read as though it did.**
The empty case fires only when a stale element rect falls ENTIRELY outside the new bitmap. The far more
likely resize leaves the stale rect comfortably in bounds while the window's internal layout has moved
underneath it: the crop then extracts the wrong pixels, the masks land on them consistently, and the
result is a flawless-looking PNG of the wrong region, returned as success. That is the worst outcome in
this whole design — a wrong answer that reads as right. *(Panel round 5, Fold Auditor — a gap in round
4's own fix, the fifth consecutive round in which this has happened.)*

**But this backend makes the resize DETECTABLE, which the scrape did not** — and the `W1` rule above has
already made a pure MOVE harmless rather than merely undetected.

**Compare `W1.Size` against `W2.Size`.** Sizes, not rectangles: a pure move changes only the origin, and
flagging it would be a false positive on a case the algorithm already handles correctly. *(Panel round 6,
Fold Auditor: an earlier draft said "any difference", which would have flagged every moved window.)*

Movement and internal relayout without a size change remain undetectable and are accepted as such, per
§2. A size change does not have to be.

**What a detected size change DOES, settled here rather than deferred** — because it is the tool's
behavioural contract, not an implementation detail, and the two scopes need different answers:

- **Window scope: refuse if there are masks; capture and warn only if there are none.** An earlier
  version said "capture and warn" unconditionally, on the grounds that the pixels are the window's own
  current content and merely newer than expected. **That was wrong, and it was a leak.** The mask rects
  were computed against the PRE-resize layout. A resize reflows content, so the masks no longer
  necessarily cover what they were sampled to cover, and the image comes back with sensitive regions
  partly or wholly unredacted — the failure class SP4 existed to close, arriving through the door this
  design opened. "Newer than expected" describes the pixels; it does not describe the masks. So:
  - **mask set NON-EMPTY → REFUSE.** There is no honest image to return.
  - **mask set EMPTY → capture, and warn `windowResized`.** Nothing was going to be redacted, so no
    misalignment is possible.

  ⚠ **The false-refusal cost is real and is accepted with its eyes open.** A window that grew from its
  right edge may not have reflowed at all, leaving every mask perfectly placed — and this rule refuses
  that capture anyway. The reason is that *the two cases are indistinguishable from here*: a size change
  is observable, a reflow is not, and nothing in the UIA data taken at `W1` can say whether the controls
  moved. Guessing "probably fine" on a redaction question is the guess this project does not make.
  *(Panel round 10 argued the refusal overshoots. It does, in the non-reflowing case; the overshoot is
  the price of not being able to tell, and it is paid in a refusal rather than in a leak.)*

  ⚠ **This IS a regression against today, and the apparent inconsistency is deliberate.** Today the tool
  captures that window and returns an image; after this change it refuses. And §2 openly accepts the
  general movement/relayout race as inherent and undetectable — so the design tolerates stale masks it
  cannot see while refusing stale masks it can. That is not incoherent: **accepting an undetected risk
  and shipping a known-bad image are different acts.** The first is a limit on what the tool can know;
  the second is a decision to hand over an image after learning it may be under-redacted. The whole
  reason this backend is worth having is that it can detect things the scrape could not, and detection is
  only worth anything if it changes behaviour. *(Panel round 11, Regression Auditor, listed this as an
  unjustified loss. It is a loss, and it is justified — but it belongs in the operator's ratification
  alongside the privacy-posture widening, not buried as an implementation detail.)*

  ⚠ **The unscanned-pixel leak this rule does NOT cover is closed by the crop, not here.** A window that
  GREW exposes area the UIA walk never inspected, and an empty mask set says only that the OLD bounds
  were clean. §1's crop discards that region on both scopes, so "mask set empty → capture" is safe.
  *(Panel round 10, Fold Auditor: the leak was real; its proposed fix — crop window scope too — was
  right and is adopted. Its further conclusion, that the crop makes the refusal unnecessary, is not: the
  crop solves unscanned PIXELS, not reflowed MASKS.)*
- **Element scope: RETRY, then fall back to the scrape. Never return the crop.** The element's rect is
  from before the resize, so its offset within the window may no longer locate it. The crop can land on
  the wrong content and be masked consistently — the "flawless-looking PNG of the wrong region" this
  subsection exists to prevent. This is the case where §3's usual asymmetry inverts: there is no cheap
  warning available, because the wrong answer is indistinguishable from the right one.

  ⚠ **Stated as ONE flow, because three rounds each amended a piece of it and the pieces stopped
  agreeing.** An earlier draft said "REFUSE, with a retry hint" and then, further down, "on exhaustion
  fall back to the scrape" — two different terminal outcomes for one condition. *(Panel round 15,
  Failure-Mode Cartographer: its table listed the detection and the exhaustion as separate rows, which is
  what made the disagreement visible.)* The flow is:

  1. Detect `W1.Size != W2.Size`. Do **not** return the crop and do **not** refuse yet.
  2. Re-take the geometry-and-capture pair, a bounded number of times.
  3. If a consistent pair is obtained, proceed normally with the fresh `W1`/`E` — no warning.
  4. On exhaustion, fall back to the SCRAPE using the LAST attempt's geometry, reporting
     `captureMethod: "screenScrape"` and `scrapeFallbackTargetChanging`.

  There is no terminal refusal on this path. The tool refuses only where the target has no pixels to
  give (see the degenerate, minimized and empty-crop cases).

  ⚠ **"Retry" must be BOUNDED, or a continuously-changing window is a livelock.** A window that animates
  — a progress dialog, a resizing splash, anything mid-transition — never satisfies
  `W1.Size == W2.Size`, so an unconditional retry hint tells the agent to loop forever on a capture that
  can never succeed. Re-take the geometry-and-capture pair a small bounded number of times.
  *(Panel round 10, Consumer Advocate.)*

  ⚠ **On exhaustion, FALL BACK TO THE SCRAPE — do not refuse.** An earlier draft refused here, and that
  was a REGRESSION: spinners, progress dialogs and expanding windows are captured perfectly well today,
  and a design that returns nothing for all of them is strictly worse than the one it replaces. This is
  the same mechanism-failure case as the timeout (risk 2): the window is on screen with real pixels, and
  what we cannot obtain is a consistent geometry-and-capture pair. The scrape is what the tool does today
  for this window, so the fallback restores current behaviour instead of returning nothing.

  ⚠ **The scrape is NOT synchronized with the geometry, and this spec must not claim it is.** An earlier
  draft said the scrape "takes both at one instant". It does not: it takes PIXELS at one instant, while
  `E` still comes from a UIA walk that happened earlier, so an animating window is captured with
  coordinates that are already slightly stale. That staleness is the same inherent race §2 documents and
  accepts — it is today's behaviour, not a new defect — but the fallback is justified by "better than
  nothing", not by synchronisation it does not have. *(Panel round 13, Fold Auditor: the claim was
  overstated. Its conclusion — that the fallback therefore fails the design's own tests — is rejected:
  test (2) asks whether `PrintWindow` produced a usable image, and it did not.)*

  ⚠ **The scrape must use the geometry from the LAST retry attempt, not the first.** Each attempt
  re-takes the geometry-and-capture pair, so the freshest pair is already in hand; reusing the original
  `E` would hand the scrape coordinates that are staler by the entire duration of the retry loop — the
  loop would be actively degrading the fallback it exists to reach. *(Panel round 12, Regression
  Auditor.)*
  *(Panel round 11, Regression Auditor.)*

  ⚠ **The bound and the interval are the PLAN's, with an acceptance criterion**: the whole retry sequence
  must terminate within a stated wall-clock budget small enough that a caller does not experience it as a
  hang, and the budget must be documented in the tool description alongside the timeout. A retry loop
  whose worst case is unbounded in time is the livelock in a different costume.

*(Panel round 6, Altitude Auditor and direct question 1: the peer stuck at this exact abdication twice,
and it was right to. "The plan owns what to do with that signal" was the spec declining to specify its
own contract.)*

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

**The REQUIRED end state, written out** — the prose below demands a caller-supplied switch, and a block
showing only the "before" is a paste trap this document has already fallen into twice. *(Panel round 15,
Pattern Hunter — third instance of that pattern.)*

```csharp
// clipToVirtualScreen defaults to FALSE: the mask-preserving value, so a forgotten call site fails safe.
// Full-desktop (the scrape) passes true; window and element scope take the default.
var yardstick = clipToVirtualScreen
    ? System.Drawing.Rectangle.Intersect(captureBounds, ScreenCapture.VirtualScreenBounds())
    : captureBounds;
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

⚠ **The two-stage detector applies ONLY when `captureMethod` is `printWindow`.** It exists to catch a
`PrintWindow` render failure, and the two-stage split is incoherent on a scrape image: that image is
already sized to `captureBounds`, so there is no "full window bitmap" distinct from the crop, and a
legitimately solid element would fire `uniformCanvas` — telling the agent the whole WINDOW was blank when
the tool never rendered a window bitmap at all. *(Panel round 12, Fold Auditor: the two-stage detector
silently assumed a two-stage pipeline that the fallback path does not have.)*

⚠ **BUT `uniformCanvas` DOES run on a FULL-DESKTOP scrape, and closing that gap is deliberate.** A
full-desktop capture can come back entirely black for reasons `IsDesktopRenderable()` does not catch — a
window using `SetWindowDisplayAffinity` to exclude itself from capture, DRM-protected content, a session
in transition. Today that returns a black image silently, and an agent cannot tell it from a dark screen.
The detector now exists, the check is nearly free, and a genuinely uniform whole desktop is not a real
case, so the false-positive cost is close to zero.

This is a small, deliberate widening of item 8's scope, recorded rather than done quietly: full-desktop is
otherwise out of scope and keeps the scrape. It is folded here rather than deferred because this project
does not defer a defect for being pre-existing — the blind spot is real, and the mechanism that closes it
is already being built. *(Panel round 13, Fold Auditor: disabling the detector on every scrape path was
the right call for the two-stage element/window split and the wrong call for the whole-image check.)*

**The two errors that choice accepts, stated symmetrically:**

- **False POSITIVE:** a window that is genuinely one uniform colour (a colour-calibration app, a black
  loading screen) is warned about incorrectly. Accepted — the consequence is a spurious sentence.
- **False NEGATIVE:** an ELEMENT crop that is entirely black inside a window that rendered fine — a
  hardware-accelerated child viewport that failed to draw — passes the WINDOW-level detector with `false`,
  because that detector never saw the crop. *(Panel round 1, BA-1, agy seat.)*

  ⚠ **This one is no longer merely accepted — it is a REGRESSION against today's behaviour, and it gets
  its own signal.** The scrape composites that viewport correctly today, so an agent that sees the
  control now would receive a silently black image after this change, with nothing saying so. Run the
  same predicate a SECOND time on the cropped region, and emit `elementCanvasUniform` when the crop is
  uniform but the full window bitmap was not. That difference is what makes it worth reporting: a
  uniformly-coloured crop inside a uniformly-coloured window is just a solid window, already covered by
  `uniformCanvas`. The extra false-positive rate — a genuinely blank panel, a flat background — is
  accepted on the same grounds as every other warning here: the cost is a spurious sentence, and the
  alternative is silent corruption. *(Panel round 11, Regression Auditor. §3's original argument for
  evaluating PRE-crop stands — that is why this is an ADDITIONAL check with its own code, not a
  relocation of the first one.)*

### 4. Failure mapping — the existing error path does not reach the new backend

`ScreenCapture.cs:40-41` maps `COMException`/`ExternalException` from `Capture.Rectangle` onto
`ToolErrorCode.CaptureUnavailable`, with the actionable hint "reconnect to restore rendering". **GDI does
not throw.** `CreateCompatibleDC`, `CreateCompatibleBitmap` and `SelectObject` return null/zero handles on
failure, so on the new path that catch never fires and nothing else takes its place.

Null-handle checks are not optional — given ROADMAP item 13 (108 bare catches) the realistic outcome of
skipping them is an NRE surfacing as something unhelpful, or a garbage bitmap returned as a capture.
*(Panel round 1, CA-2.)*

**The mapping is settled here, not left to the plan:** a null or zero handle from `CreateCompatibleDC`,
`CreateCompatibleBitmap` or `SelectObject` becomes **`ToolErrorCode.CaptureUnavailable`**, with a hint
naming GDI resource exhaustion and suggesting the caller retry after closing windows. It is the same
error class the scrape path already uses for an environmental capture failure
(`ScreenCapture.cs:40-41`), so a caller that already branches on `CaptureUnavailable` needs no change.
What the plan owns is the hint's exact wording. *(Panel round 14, direct question 1: the spec said "the
plan owns an explicit mapping" without naming the code, which is an abdication of contract dressed as a
deferral — the same shape §5 was corrected for in round 6.)*

**Every refusal this design introduces, with its error code.** §4 previously mapped only the GDI case,
leaving the §1 refusals unmapped — an implementer would have invented a code per site.
*(Panel round 15, Failure-Mode Cartographer: a failure mode with no row.)*

| Refusal | Code | Why that code |
|---|---|---|
| `W1` or `W2` has zero/negative extents | `ElementNotActionable` | the window has no renderable area; the same class as the existing minimized refusal |
| window is minimized at capture time | `ElementNotActionable` | matches the pre-existing check at `ScreenshotTools.cs:55`, which uses exactly this code |
| element crop is empty (`effective` degenerate) | `ElementNotActionable` | the named element is not inside the pixels that were captured, so it cannot be acted on from this image |
| window-scope resize with a NON-EMPTY mask set | `RedactionUnmaskable` | this is precisely that code's meaning — the redacted regions cannot be reliably located — and it is the code SP4 established for "refuse rather than return an under-masked image" |
| null/zero GDI handle | `CaptureUnavailable` | environmental capture failure, matching the scrape path (`ScreenCapture.cs:40-41`) |
| desktop not renderable | `CaptureUnavailable` | unchanged; already thrown at `ScreenshotTools.cs:27-28` |

No new `ToolErrorCode` values are introduced. Every refusal reuses a code the agent contract already
documents, so a caller that branches on today's codes needs no change to handle this feature.

The session-level guard is unaffected and still runs first: `ScreenshotTools.cs:27-28` throws
`CaptureUnavailable` when `IsDesktopRenderable()` is false, which covers the locked/disconnected desktop
before either backend is reached.

### 5. Contract changes

Two fields join the JSON metadata, both ALWAYS present. **Their names and shapes are settled here, not
deferred** — they are a wire contract every consuming agent reads, which makes them this spec's business
rather than the plan's. *(Panel round 6, Altitude Auditor: an API design that delegates its own schema is
structurally incomplete, and that criticism was correct.)*

| Field | Type | Meaning |
|---|---|---|
| `captureMethod` | string, exactly `"printWindow"` or `"screenScrape"` | which backend produced the image. Window and element scope report `printWindow`; full-desktop reports `screenScrape` |
| `captureWarnings` | array of `{code, recourse}` objects, **empty in the normal case** | one entry per condition that may make this image unusable. `code` is a stable identifier to branch on; `recourse` is the sentence telling an agent what to do instead |

`captureMethod` matters because the two scopes now produce images by different mechanisms, and a caller
comparing them needs to know which it holds. Casing follows the existing metadata, which is camelCase
throughout (`ScreenshotTools.cs:71-83`).

**`captureWarnings` is deliberately shaped like `unmaskedProcesses`, not like a boolean.** That field is
already an always-present list which is empty when nothing is wrong and actionable when it is not, and
this repo's AB-9 reasoning is why. A list solves three problems a `bool` did not: it carries its recourse
alongside its state rather than needing a second field, it does not force a new field per future
condition, and "empty" reads as an answer rather than as an absence.

**The two `code` values it ships with, spelled out** — naming them by their section number left the wire
contract undefined, which is the same abdication §5 exists to close:

| `code` | fires when | `recourse` says, in substance |
|---|---|---|
| `uniformCanvas` | §3's detector finds the full WINDOW bitmap effectively one colour (`printWindow` only) | the image may not be usable; read the UIA tree via `desktop_snapshot` instead |
| `desktopCanvasUniform` | §3's detector finds a FULL-DESKTOP scrape effectively one colour | the whole desktop came back a single colour. The usual causes are a secure desktop (a UAC prompt), DRM-protected content, or a session in transition — **UIA is normally blocked in those states too, so `desktop_snapshot` will not help.** Wait for the condition to clear and re-capture |
| `windowResized` | §1's `W1.Size != W2.Size` check fires on a WINDOW-scope capture with an empty mask set | the window changed size mid-capture. The image itself is sound — it was cropped back to the region you asked for — but the layout inside it **may** have reflowed, so any UIA tree or element ref you hold for this window may be geometrically stale. Re-snapshot before acting on cached coordinates |
| `popupsNotRendered` | this window had one or more popup roots at geometry time (`PopupFinder.SearchRoots` returned more than the window itself). **Fires on BOTH backends** | an open menu, dropdown or tooltip belonging to this window is a separate top-level window and may be **missing from this image** — structurally absent under `printWindow`, and cropped off under `screenScrape` wherever it extends beyond the window's rect. Its absence is not evidence it failed to open — read the UIA tree to see it |
| `elementCanvasUniform` | element scope only: the CROPPED region is effectively one colour while the full window bitmap was not | this element's pixels may have failed to render even though the window as a whole did — a hardware-accelerated child viewport is the usual cause. Verify through the UIA tree before concluding the control is blank |
| `scrapeFallbackTargetUnresponsive` | `PrintWindow` timed out because the target's message loop is blocked, and the scrape produced this image instead (risk 2) | this image is a screen scrape, so anything overlapping the window is in it — treat occlusion as possible. **The target is not pumping messages**: it will not respond to input either, so do not queue clicks against it |
| `scrapeFallbackTargetChanging` | element-scope retries were exhausted because the window kept resizing, and the scrape produced this image instead (§1) | this image is a screen scrape, so treat occlusion as possible. **The target is changing continuously** — it is alive and busy, not stuck; waiting and re-capturing may succeed |

Codes are camelCase, matching every other field in this response. *(Panel round 8, Fold Auditor: round
7 settled the SHAPE and dropped the VALUES.)*

⚠ **Entries are `{code, recourse}` objects, NOT bare sentences — the analogy to `unmaskedProcesses` holds
only if they are.** That field carries stable programmatic identifiers (process names); an agent branches
on them. A list of English prose would force a consumer to regex wording that will drift, which is a
worse contract than the boolean it replaced, not a better one. `code` is what a caller branches on;
`recourse` is what §3 requires so the signal arrives with its instruction. *(Panel round 7, Contract
Integrity — the seat was right that the analogy was being claimed on the axis where it broke.)*

⚠ **Codes are SCOPE-SPECIFIC, and some combinations are therefore unreachable.** `uniformCanvas` and
`windowResized` are window-scope; `elementCanvasUniform` is element-scope; `desktopCanvasUniform` is
full-desktop only. So `elementCanvasUniform` can never co-occur with `windowResized`. A consumer does not
need to reason about that — it branches on the codes it receives — but the scoping is stated so nobody
writes a handler for a pair that cannot arrive. *(Panel round 15, Failure-Mode Cartographer.)*

⚠ **A warning is never a substitute for a refusal where §1 or §4 specifies one.** `captureWarnings`
annotates an image that was returned; it does not downgrade a case the design decided to refuse.

Both are carried on `CaptureResult`, which today is:

```csharp
// ScreenCapture.cs:9 — CURRENT shape
public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions);
```

**The REQUIRED end state, written out rather than described** — the same reason §5's tool-description
string is written out: a spec that quotes only the "before" and tells you to extend it is a paragraph an
implementer can satisfy by pasting the before. *(Panel round 14, below-floor item promoted — it is the
defect class round 8 already found once in this document.)*

```csharp
public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions,
                                   string CaptureMethod, IReadOnlyList<CaptureWarning> CaptureWarnings);

public sealed record CaptureWarning(string Code, string Recourse);
```

Appended, never inserted — see below. `CaptureWarnings` is never null; empty is the normal case.

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

⚠ **The tool description's metadata ENUMERATION must be extended too — this is the third required edit to
that same string, and it is repeated here deliberately.** `ScreenshotTools.cs:17` currently lists the
contract as `{bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses}`. A
field present in the payload but missing from that list is one the model has no reason to read.

**The REQUIRED end state, so nobody copies the line above by mistake:**

```
{bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated,unmaskedProcesses,captureMethod,captureWarnings}
```

The requirement is argued in §3, where it belongs alongside the diagnostic's rationale — but an engineer
working through the contract changes will be reading THIS section, not the failure policy, so the
obligation is recorded in both. *(Panel round 5, Disposition Challenger: a correct fold placed where the
person who has to act on it would not look. Panel round 8, First Reader: the paragraph then quoted the
OLD string as its only concrete artifact, so a reader following it literally would paste back exactly the
omission it warns against.)*

## Out of scope

- **Full-desktop scope.** Keeps the scrape.
- **Minimized windows.** Still `ElementNotActionable`. `PrintWindow` on a minimized window is unreliable,
  and nothing in item 8 requires changing it.
- **A scrape fallback when `PrintWindow` disappoints — with ONE exception, the timeout (risk 2).**
  Rejected everywhere else — **on error-budget grounds, which is a different and better reason than the
  one this spec originally gave.** The original wording said the
  choice "needs the unsound detector F4 rules out"; that is now imprecise, because §3 gives the detector
  explicit acceptance criteria. The exclusion still stands, and here is why: §3's error budget is
  calibrated to a CHEAP consequence — a false positive costs a spurious sentence. Reusing the same signal
  to SELECT A BACKEND re-prices every error in it. A false positive would then silently return the
  scrape, which for an occluded window is a photograph of the occluder — the exact defect item 8 exists
  to fix, reintroduced by the mechanism meant to protect against it. A detector good enough to annotate
  is not thereby good enough to switch on. *(Panel round 3, Scope Auditor: the flaw in the old rationale
  was real and is corrected; the conclusion it implied — that the exclusion should be revisited — is
  rejected.)*

  **The timeout exception falls outside that reasoning rather than weakening it.** The ban is about
  switching backends on an UNSOUND signal. A timeout has no false-positive mode to re-price: the call
  either returned or it did not, and `captureMethod` plus a `scrapeFallback*` code make the switch
  visible. Risk 2 carries the full argument, including why refusing there would have been a regression
  against today's behaviour. *(Panel round 10, Adversary of the Reviewer.)*
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
   one Chromium-family window before this ships**, since it is a common agent target. ⚠ **Measure a
   browser AND an Electron app, not one as a proxy for the other** — Electron embeds Chromium but drives
   its own compositor and window chrome, so a result from one does not transfer to the other.
   *(Panel round 7, below-floor item promoted.)*
2. **`PrintWindow` depends on the TARGET's message loop, and nothing here bounds that wait.** It renders
   by sending `WM_PRINT`/`WM_PRINTCLIENT` synchronously to the target window. The scrape has no such
   dependency — it reads the composited desktop and a hung app cannot block it. Consequences: a hung or
   busy target blocks with no timeout and no cancellation; `ScreenshotTools.cs:58` puts that block on a
   THREADPOOL thread which is never returned; and a blocked call never reaches its `using`, so it holds an
   HDC, a GDI bitmap and a managed bitmap permanently. "Occluded" and "not pumping" are correlated in
   practice, so the feature's primary use case is also its worst failure case.
   **UNMEASURED — this is documented Win32 behaviour, not a probe result, and this spec's standard is
   measured-not-assumed.** The plan must stage a window that stops pumping (a fixture with a blocking
   sleep on its UI thread), call `PrintWindow` against it, and time the call. *(Panel round 1, CA-1 +
   RV-2.)*

   ⚠ **The timeout CONTRACT is settled here, so only the bound is a measurement.** If the call can block,
   the plan bounds it — and **on expiry the capture FALLS BACK TO THE SCRAPE**, reporting
   `captureMethod: "screenScrape"` and `scrapeFallbackTargetUnresponsive`. It never returns a blank or partial
   `PrintWindow` image.

   **Why the scrape fallback is allowed here**, when Out-of-scope bans it everywhere else. The
   distinguishing property is **NOT** that a timeout is "definite and mechanical" — an earlier draft said
   that, and it was wrong, because every other refusal in this design is equally definite and mechanical
   (a degenerate window, a minimized window, an empty crop). That rationale licensed the fallback
   everywhere and collapsed the exclusion it was meant to carve an exception out of.
   *(Panel round 11, Fold Auditor.)*

   The property that actually separates them:

   > **The fallback is allowed only where BOTH hold:**
   > **(1) the TARGET has real, current, on-screen pixels — otherwise the scrape has nothing better to
   > offer; and**
   > **(2) the `PrintWindow` path produced NO USABLE IMAGE AT ALL — otherwise switching trades an image
   > that is merely incomplete for one that reintroduces occlusion, which is the defect this feature
   > exists to remove.**

   ⚠ Test (2) is what stops the rule licensing the fallback for a partial render. *(Panel round 12, Fold
   Auditor: with test (1) alone, a black hardware-accelerated viewport and a missing popup both classify
   as mechanism failures — the target really is on screen, and `PrintWindow` really did fail to get it —
   so the rule as written demanded a fallback the design does not do.)* The peer's classification was
   correct and the rule was incomplete. But its conclusion — that those cases MUST fall back — is
   rejected: in both, `PrintWindow` returned a usable, occlusion-free image that is missing a region. The
   scrape would supply that region and re-import everything overlapping the window. **A partial image
   that is honest about being partial beats a complete image that may be a photograph of something
   else** — which is the whole thesis of item 8. So those cases keep the image and carry a warning
   (`elementCanvasUniform`, `popupsNotRendered`).

   - **Timeout — mechanism failure.** The window exists, is on screen, and is rendering to the desktop;
     `PrintWindow` simply could not get a copy because the target's message loop is blocked. The scrape
     reads the composited desktop and is unaffected. It genuinely has a better answer than nothing.
   - **Retry exhaustion on a continuously-changing window — mechanism failure, same shape.** The window
     is on screen with real pixels; what we cannot obtain is a CONSISTENT geometry-and-capture pair. The
     scrape is what the tool does for this window today, so the fallback restores current behaviour
     rather than returning nothing. It is NOT synchronised with the geometry — see §1, which retracts
     that claim — and it is not justified as though it were. See the element-scope retry rule in §1. *(Panel round 11, Regression Auditor: refusing here was a total outage for spinners, progress
     dialogs and expanding windows, which work today.)*
   - **Degenerate, minimized, or empty-crop — TARGET failure.** There are no pixels to get. A minimized
     window has none on screen; a zero-extent window has none at all; an element outside the captured
     region is not on screen where the crop looked. Falling back would photograph whatever happens to
     occupy those coordinates, which is precisely the defect item 8 exists to fix.

   And the switch is never silent: `captureMethod` names the backend and the `scrapeFallback*` code states the
   consequence.

   ⚠ **Refusing here would have been a REGRESSION, which is what makes this worth the exception.** A
   hung window is precisely where the scrape still works, because it composites independently of the
   target's message loop. Today, before this feature exists, an agent can photograph a hung window
   perfectly well. A design that times out and then refuses would take that away and hand back nothing —
   strictly worse than the behaviour it replaced, in the one scenario the feature was most expected to
   help with. *(Panel round 9 settled this as "refuse on expiry". Panel round 10's Adversary seat, asked
   which round-9 fold was most likely wrong, named exactly this one and gave the regression argument. It
   was right, and the contract is inverted here.)*
3. **Stale composition.** `PW_RENDERFULLCONTENT` may return the last frame DWM composed rather than
   forcing a fresh render. If the pixels are older than the UIA tree the masks were computed from, masks
   can miss data that IS in the image — a leak with a different shape from the ones above.
   **UNMEASURED, and the experiment is specified:** drive a Chromium-family window to change a sensitive
   region's state (reveal/hide a password field, switch tabs), then call `PrintWindow` and walk the UIA
   tree with zero delay. This can be folded into risk 1's Chromium measurement — same window, same probe.
   *(Panel round 1, agy Q2. The peer raised it and said plainly it could not determine the answer without
   running the probe, which is the correct answer.)*

   ⚠ **The probe is one-sided, and the plan must not read it as two-sided.** A positive result — image
   showing the old state while the tree reports the new — CONFIRMS the race. A negative result does NOT
   refute it: this is a timing race between an asynchronous compositor and a separate tree walk, so a
   finite number of runs failing to catch it means "not observed at these timings", never "cannot
   happen". **A negative therefore licenses shipping with the risk documented and unmitigated; it does
   not license deleting this risk entry.** Anyone reading a clean probe as proof of safety has drawn the
   one conclusion the experiment cannot support. *(Panel round 4, Privacy Auditor.)*

   ⚠ **And "ship with it documented" is the OPERATOR's call, not the plan's.** This is an unmitigated
   privacy risk being accepted on the strength of a test that cannot return a refutation. The plan runs
   the probe and reports; it does not get to authorise the acceptance on a negative. *(Panel round 5 —
   the peer filed this below its own severity floor as "a policy decision rather than a mechanical
   defect". In this project an unratified privacy acceptance is above the floor, and the below-floor list
   has now yielded a real item in several reviews.)*
4. **`Capture.Rectangle` also handles the DC lifecycle** that the new path must own: `CreateCompatibleDC`,
   `CreateCompatibleBitmap`, `SelectObject`, and their release. A leak here runs inside a long-lived
   server. The plan owns the exact ownership pattern.

   ⚠ **With a checkable acceptance criterion, not just an instruction to be careful:** GDI handle count
   and user-object count for the server process must return to their starting values after a run of
   repeated captures, including runs that hit each refusal path above (degenerate window, minimized
   mid-capture, empty crop, timeout). Those are the paths where a handle leaks, because they exit early.
   Measure with the process's handle counters before and after; flat is the pass condition.
   *(Panel round 9, Completeness Critic: the second of two items marked NEITHER.)*
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
   mask set (`PopupFinder.SearchRoots`, pinned by `PopupRootCoverageTests` — VERIFIED: that test's own
   doc comment at `:9` states it routes find through `PopupFinder.SearchRoots` so a window-child popup is
   reachable, and `SearchRoots` is called from TWELVE sites in `PerceptionManager.cs` — lines 75, 89, 107,
   126, 262, 283, 305, 539, 540, 740, 883 and 998. *(An earlier draft said nine. Panel round 11 filed the
   count as unverified, correctly; it was wrong.)* A popup is a SEPARATE
   top-level HWND: the scrape includes its pixels **only where the popup overlaps the window's own rect**,
   since a window-scoped scrape captures `captureBounds` and nothing outside it — an earlier draft said
   the scrape simply "includes its pixels", which §5's `popupsNotRendered` recourse already contradicts.
   *(Panel round 15, Pattern Hunter — third instance of a retracted claim left live elsewhere.)* Within
   that overlap the grafted mask covers them, but `PrintWindow`
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

### AGY-AFTER adversarial panel — round 4

Seats: Fold Auditor (round 3's four edits, enumerated, with an instruction to trace the new pseudocode on
concrete numbers), Evidence Auditor (a census of F1-F6 against every claim resting on each — the Evidence
section had never been audited as evidence), Privacy Auditor (the Privacy-posture section and risk 3,
neither previously reviewed). Report: `.clavity/scratch/item8-panel/agy-round4.md`. **Verdict: NOT
GREEN.** Three folds:

- **Round 3's pseudocode crashes on an empty intersection** — the fourth consecutive round in which the
  previous round's fix carried its own defect. Measured: zero-extent `Bitmap.Clone` throws
  `ArgumentException`, which `ScreenCapture.cs:40`'s filter does not catch. Now guarded on extents, with
  the repo's own touching-rects hazard cited, and given a defined refusal.
- **F3 was generalised past its evidence.** It measured two ACTIVE transports and was being read as
  session-STATE independence. Narrowed to what it measured; the unmeasured locked/disconnected case is
  closed by the `IsDesktopRenderable()` guard rather than by the data.
- **Risk 3's experiment is one-sided** and was written as though it were two-sided. A negative result
  cannot refute a timing race. The entry now says what a negative licenses and what it does not.

The Evidence Auditor's census returned five of six rows solid with specific claim-to-evidence links, and
flagged exactly one. A census produces a checkable answer where "does the evidence hold?" would have
produced an adjective.

### AGY-AFTER adversarial panel — round 5

Seats: Fold Auditor (round 4's edits, traced on concrete numbers), Contradiction Hunter (pairs of
statements in DISTANT sections that cannot both be true — the failure mode of a document accreted over
five rounds), Disposition Challenger second pass (aimed at the STRENGTH and PLACEMENT of rounds 2-4's
folds rather than their truth). Report: `.clavity/scratch/item8-panel/agy-round5.md`. **Verdict: NOT
GREEN.** Four folds:

- **The empty-intersection guard was reading as resize-handling when it only prevents a crash** — fifth
  consecutive round in which the previous round's fix carried a gap. The common resize leaves the stale
  rect in bounds and silently crops the wrong pixels. Folded with a stronger remedy than was proposed:
  under this backend a size change is DETECTABLE for free, because the plan already calls `GetWindowRect`
  to size the bitmap and can compare it against the rect the UIA walk implied.
- **§1 justified its coordinate math by a caller need §5 forbids.** The math is right; the reason was
  wrong. Restated on the two reasons that actually hold — `Encode`'s arithmetic is absolute, and a
  `bounds` that does not match the pixels is a false statement.
- **A correct fold sat where nobody would act on it** — the tool-description enumeration requirement was
  argued in the failure-policy section, while the engineer changing the contract reads §5. Recorded in
  both.
- **An item the peer filed BELOW its own floor was above this project's** — accepting an unmitigated
  privacy risk on a test that cannot refute is the operator's ratification, not the plan's. The
  below-floor list has now produced a real finding in several reviews across this project; it is worth
  reading first, not last.

### AGY-AFTER adversarial panel — round 6

Seats: Fold Auditor (round 5's edits), **Goal Auditor** (does the design still achieve its goal after
five rounds of hardening — count the refusal paths), **Altitude Auditor** (is a 693-line spec now doing
the plan's job, and conversely what has it left open that it should not have). Report:
`.clavity/scratch/item8-panel/agy-round6.md`. **Verdict: NOT GREEN.** Three folds, one rejection, and one
seat clean:

- **Round 5's resize detection said "any difference", which would flag every MOVED window.** Correct as
  far as it went — and tracing it revealed something worse that neither the peer nor the previous round
  had: translating the element rect by the CAPTURE-TIME window origin misaligns the crop by exactly the
  movement delta. Fixed by a rule that makes movement harmless rather than merely undetected: the element
  rect and the origin that translates it must come from the same observation. Sixth consecutive round in
  which the previous round's fix carried a defect, and the fourth of those in this one subsection.
- **The spec was abdicating its own wire contract.** Field names, and the refuse-versus-flag branch for a
  detected resize, were both left to the plan. The peer stuck at that branch in two successive rounds'
  "furthest point" answers. Both are now settled: §1 gives window scope a warning and element scope a
  refusal, and §5 names `captureMethod` and `captureWarnings` with their exact shapes.
- **`captureWarnings` replaced the planned boolean**, reusing `unmaskedProcesses`' always-present-list
  idiom. A list carries its own recourse text, absorbs the resize warning without a new field, and makes
  "empty" an answer rather than an absence.

**Rejected:** that naming the extents guard (`Width <= 0 || Height <= 0`) over-constrains the plan. The
form is named because the repo has already shipped the wrong form of this exact guard and documents the
resulting leak at `PerceptionManager.cs:942-946`. A known-recurrence is worth one line of prescription.
The behavioural requirement is now stated first, with the concrete form as its consequence.

**The Goal Auditor seat came back CLEAN, and that is the most reassuring result of the review.** It
enumerated all five paths on which a capture now refuses or degrades, quoted each, and confirmed every
one is an edge case — the ordinary target, a static window simply sitting behind another, still takes a
path with no clamping and no refusal and returns that window's pixels with masks aligned. Five rounds of
hardening did not make the common case fragile, which was the specific risk of hardening this hard.

### AGY-AFTER adversarial panel — round 7

Seats: Fold Auditor (round 6's edits, with a three-case numeric trace to run), Contract Integrity (the
brand-new `captureWarnings` field), First Reader (the whole document read top-to-bottom by someone with
no memory of the review). Report: `.clavity/scratch/item8-panel/agy-round7.md`. **Verdict: NOT GREEN.**
Three folds:

- **The fix was appended below the text it was correcting, so the superseded version was still the live
  instruction.** Two seats found this independently: round 6 introduced the `W1`/`W2` rule at the bottom
  of §1 while the pseudocode block sixty lines above still said "offset by the window origin" without
  saying which — and an engineer reading in order implements the wrong one before reaching the fix. The
  numeric trace was correct and verified by hand: window `(-8,-8,1936,1036)` moving to `(92,92,…)` with
  an element at `(100,200,300,50)` yields `absolute = (200,300)` against masks at `(100,200)`, every mask
  100px off. **§1's crop subsection has been REWRITTEN as one block** with the notation defined up front
  and used throughout, and its three rules stated as consequences. Appending a seventh correction would
  have repeated the failure.
- **`captureWarnings` claimed the `unmaskedProcesses` analogy on the axis where it broke.** That field
  carries stable identifiers an agent branches on; a list of English sentences would force consumers to
  regex prose that drifts — a worse contract than the boolean it replaced. Entries are now
  `{code, recourse}`: `code` to branch on, `recourse` to carry §3's required instruction.
- **Promoted from below the floor:** Electron is not a proxy for a browser. Risk 1 now requires both.

**The First Reader seat is worth keeping in the rotation.** Every other seat reviewed this document
knowing its history; that one read it as an executing engineer would, and it caught a correction that had
never reached the thing it corrected — a failure invisible to anyone who already knew what the fix said.

### AGY-AFTER adversarial panel — round 8

Seats: First Reader second pass (on the rewritten text), Fold Auditor (round 7's rewrite, with the
three-case numeric trace re-run against the NEW wording), Integration Auditor (how the design's pieces
interact with EACH OTHER rather than whether each is individually right). Report:
`.clavity/scratch/item8-panel/agy-round8.md`. **Verdict: NOT GREEN.** Three folds:

- **A degenerate window was unguarded on BOTH paths.** The Integration Auditor read §1's empty-crop
  refusal as contradicting "window scope captures and warns on a resize". It does not — that refusal is
  element-scoped, which the algorithm block now says explicitly. But tracing the supposed conflict
  surfaced a real gap: a window whose rect has zero or negative extents has no bitmap to allocate, and
  nothing guarded it on either scope. A finding whose framing was wrong and whose direction was right.
- **Round 7 settled `captureWarnings`' SHAPE and dropped its VALUES.** `uniformCanvas` and
  `windowResized` are now named, with what each `recourse` must convey.
- **The tool-description paragraph was a copy-paste trap.** It told the implementer to extend the
  enumeration while quoting the OLD string as its only concrete artifact — so a reader following it
  literally reproduces the omission it warns against. The required end state is now written out in full.

**The Fold Auditor re-ran the numeric trace against the rewritten algorithm and it came back correct in
all three cases** — move, resize, and both — with `absolute` landing exactly on `E` in the move case,
which is the property that makes movement harmless. The rewrite dropped no constraint. That was the check
that mattered: consolidating six rounds of scattered corrections into one block is exactly where a
constraint goes missing.

**Also settled this round:** Risk 8's claim that `PopupRootCoverageTests` pins `PopupFinder.SearchRoots`
had been flagged as unverified in three consecutive rounds' "what did you not check" answers. Verified and
annotated in place.

### AGY-AFTER adversarial panel — round 9

Seats: Fold Auditor (round 8's edits), **Completeness Critic** (enumerate everything an implementer must
DECIDE OR BUILD and mark each SPECIFIED / DEFERRED-WITH-A-CRITERION / NEITHER — the third bucket is the
finding), **Adversary of the Reviewer** (aimed at the DRIVER: which of eight rounds' folds is most likely
WRONG). Report: `.clavity/scratch/item8-panel/agy-round9.md`. **Verdict: NOT GREEN, and this was the
round that most justified continuing.** Four folds, one of them a design correction rather than polish:

- **The window-scope resize policy was WRONG, and it was a leak.** Rounds 6-8 said window scope should
  "capture and warn" on a detected resize because the pixels are merely newer than expected. True of the
  pixels; false of the MASKS — they were sampled against the pre-resize layout, so after a resize they
  no longer cover what they were computed to cover, and the image returns with sensitive regions
  unredacted. Now: refuse if the mask set is non-empty, capture and warn only if it is empty.
- **Round 8's "ELEMENT SCOPE ONLY" label created that hole's mechanism.** Exempting window scope from the
  algorithm left it handing `Encode` a `W1`-sized `captureBounds` beside a `W2`-sized bitmap. Window
  scope's reconciliation is now stated explicitly: `absolute = W1` when the sizes match, and the size
  mismatch is the resize case above. Ninth consecutive round finding a defect in the previous fix.
- **A degenerate `W1` is a LEAK, and ordering is the whole defence.** Verified against source: with the
  yardstick unclipped under this backend, a degenerate `W1` produces a degenerate yardstick, and
  `PerceptionManager.cs:947`'s window-scoped fallback keeps it degenerate — dropping the entire mask set,
  which the repo's own comment at `:942-946` calls "a guard producing a leak". If the window then returns
  to a valid size, the `W2` guard passes and an UNMASKED image is returned as a success. The degenerate
  check must run on `W1` before the yardstick, not only on `W2` at the bitmap.
- **Minimized is not degenerate.** Asked which fold was most likely wrong, the Adversary seat named round
  8's degenerate guard and produced this spec's OWN F6 row as the counter-example: a minimized window is
  `-32000,-32000` with extents `160x28` — positive, so an extents check never fires. A window minimized
  after the geometry read yields a 160x28 placeholder. The minimized state is now re-tested at capture.
- **Two items were deferred with no criterion at all** — neither specified nor measurable. The hung-target
  timeout now has its CONTRACT settled here (refuse on expiry; only the bound is a measurement), and the
  DC lifecycle now has a pass condition (handle counts flat across repeated captures, including every
  early-exit refusal path).

**The seat aimed at the reviewer's own judgement paid for itself.** Eight rounds of folds had been checked
independently only once. Pointed at them and asked which was WRONG rather than whether they were fine, it
found one that was — using evidence already in the document.

### AGY-AFTER adversarial panel — round 10

Seats: Fold Auditor (round 9's edits, with the policy INVERSION as its stated target because inversions
overshoot), Adversary of the Reviewer second pass (round 9's folds — made fast, under the persuasion of
correct findings, and the least-scrutinised text in the document), **Consumer Advocate** (the design from
the side of the AGENT that calls the tool and must act on the response — a side nothing had audited).
Report: `.clavity/scratch/item8-panel/agy-round10.md`. **Verdict: NOT GREEN.** Four folds, and the best
of them DELETED a special case rather than adding a guard:

- **Window scope is no longer exempt from the crop, and the exemption was leaking.** A window that GREW
  between the UIA walk and the capture put pixels in the bitmap that the walk never inspected — an empty
  mask set proves only that the OLD bounds were clean. `Intersect` against a `W1`-sized `relative`
  discards exactly that region. Setting `E = W1` makes window scope run the SAME six lines as element
  scope, which removes the branch that produced a distinct defect in each of rounds 8, 9 and 10.
- **The timeout contract from round 9 was a functional REGRESSION, and is inverted.** A hung window is
  precisely where the scrape still works, because it composites independently of the target's message
  loop — so "refuse on expiry" would hand back nothing in the one scenario an agent most needs this
  feature, and strictly less than today's behaviour. The scrape fallback is allowed here and only here:
  the Out-of-scope ban rests on the fallback needing an UNSOUND trigger, and a timeout has no
  false-positive mode to re-price. `captureMethod` and a `scrapeFallback*` code keep the switch visible.
- **The `windowResized` recourse was actively harmful.** It implied retrying was optional, while §5 tells
  agents to act through the UIA tree — the tree the server has just detected is geometrically stale. An
  agent following both would click where a control used to be. The recourse now says the tree must be
  re-snapshotted and cached coordinates must not be used.
- **Element scope's retry hint could not terminate.** A continuously animating window never satisfies
  `W1.Size == W2.Size`, so "refuse, retry" is a livelock. Retries are now bounded, and exhaustion gives a
  DIFFERENT terminal message that does not suggest retrying.

**Rejected, with the reason recorded:** that cropping window scope makes the resize refusal unnecessary.
The crop solves unscanned PIXELS; it does not solve reflowed MASKS. A size change is observable and a
reflow is not, so the two cases are indistinguishable from here — the refusal's false-positive cost is
real, is now stated in the text, and is paid in a refusal rather than in a leak.

**The Consumer Advocate seat found two defects on its first outing**, both invisible from the
implementer's side: every prior seat had asked whether the design is correct, and neither of these is a
correctness defect — they are a contract that misinforms the caller and one that cannot terminate.

### AGY-AFTER adversarial panel — round 11

Seats: Fold Auditor (round 10's edits, with the timeout INVERSION as its hardest target), Consumer
Advocate second pass (against the changed contract), **Regression Auditor** (enumerate every case where
an agent that succeeds TODAY fails or gets less after this change — a lens round 10's own finding
justified). Report: `.clavity/scratch/item8-panel/agy-round11.md`. **Verdict: NOT GREEN.** Six folds:

- **The timeout exception's RATIONALE over-licensed and collapsed the ban it was an exception to.** It
  rested on a timeout being "definite and mechanical" — but a degenerate window, a minimized window and
  an empty crop are all equally definite and mechanical, so the reasoning licensed the fallback
  everywhere. Replaced with the property that actually separates them: **the fallback is allowed where
  the TARGET has real on-screen pixels and only OUR BACKEND failed to get them, and banned where the
  failure is in the target itself.**
- **Refusing on retry exhaustion was a second regression**, and the corrected rationale resolves it: an
  animating window is on screen with real pixels, so it is a mechanism failure and falls back to the
  scrape rather than refusing. Spinners and progress dialogs work today; a total outage for them was not
  a defensible trade.
- **Two outcomes gave the calling agent no way to know what it was looking at.** `popupsNotRendered`
  (an open menu belongs to a separate HWND and is structurally absent from a `PrintWindow` render — an
  agent would conclude the menu failed to open) and `elementCanvasUniform` (a child viewport that failed
  to draw inside a window that rendered fine — silently black today, with the window-level detector
  structurally unable to see it).
- **The `windowResized` recourse overstated.** Since the crop returns the pre-resize region, the image is
  sound; it is the LAYOUT inside it that may have reflowed. Softened from "is stale" to "may be stale".
- **A count in the text was wrong.** `SearchRoots` is called from TWELVE sites, not nine. The peer filed
  it as unverified rather than asserting a number, which is the correct move; measuring it refuted the
  spec.
- **The refuse-on-resize rule is a REGRESSION and the text now says so**, alongside why the apparent
  inconsistency — tolerating undetected staleness while refusing detected staleness — is deliberate.
  Accepting a risk you cannot see and handing over an image you have just learned may be under-redacted
  are different acts. It is now flagged for the operator's ratification rather than left implicit.

**Rejected, with the reason recorded:** that round 9's degenerate-`W1` guard is now dead code. True for
WINDOW scope, where `E = W1` makes the crop catch it. False for ELEMENT scope, where `E` can be a valid
element rect inside a degenerate window rect — UIA reports exactly that kind of inconsistency, which is
why the mask-escalation machinery exists. There the crop succeeds and the image ships unmasked. The guard
stays. *(A correct trace of one scope, generalised to both.)*

### AGY-AFTER adversarial panel — round 12

Seats: Fold Auditor (round 11's edits, with the replacement fallback rationale as its hardest target),
Regression Auditor second pass, **Contract Surface Auditor** (the five warning codes plus two new fields
audited as ONE surface, which nothing had done — each piece had only been reviewed as it was added).
Report: `.clavity/scratch/item8-panel/agy-round12.md`. **Verdict: NOT GREEN.** Four folds:

- **The mechanism-vs-target rule was INCOMPLETE and its own examples broke it.** A black
  hardware-accelerated viewport and a missing popup both satisfy "the target has on-screen pixels and our
  backend failed to get them" — so the rule as written demanded a scrape fallback the design does not do.
  A second test now carries the weight: the fallback also requires that `PrintWindow` produced **no
  usable image at all**. A partial image that is honest about being partial beats a complete image that
  may be a photograph of something else. That is the thesis of item 8, and it is what test (2) encodes.
- **The dual detector assumed a pipeline the fallback path does not have.** A `screenScrape` image is
  already sized to `captureBounds`, so there is no "full window bitmap" distinct from the crop — a
  legitimately solid element would have fired `uniformCanvas` and told the agent the whole window was
  blank. Both detectors now apply only when `captureMethod` is `printWindow`, which is also the only
  backend whose render can fail in the way they exist to catch.
- **The retry loop was degrading the fallback it exists to reach.** On exhaustion the scrape must use the
  geometry from the LAST attempt, not the first; each attempt already re-takes the pair, so the freshest
  one is in hand and the loop's duration stops mattering.
- **Two opposite target states produced identical metadata.** A HUNG application and a HYPER-ACTIVE one
  both fell back to the scrape with the same single code, leaving the caller unable to tell "this app is
  frozen, stop queuing input" from "this app is busy, wait and retry". Split into
  `scrapeFallbackTargetUnresponsive` and `scrapeFallbackTargetChanging`, each carrying the recourse its
  own case needs.

**Already covered, noted so it is not re-raised:** the Regression Auditor's "gain as a hazard" — that
`PrintWindow` can capture a window on another virtual desktop, which the scrape cannot — is the
privacy-posture widening, already named and already routed to the operator for ratification.

**The Contract Surface Auditor found its defect by looking at the codes TOGETHER.** Every one of the five
had been reviewed in the round that introduced it, and each was individually correct; the defect existed
only in the relationship between two of them. A surface assembled one piece at a time needs a pass that
looks at the assembled surface.

### AGY-AFTER adversarial panel — round 13

Seats: Fold Auditor (round 12's edits, with the fallback rule — now on its THIRD form — as its hardest
target), Contract Surface Auditor second pass (six codes), **Simplicity Auditor** (find what can be
REMOVED without losing a guarantee, since round 10's best outcome was a deletion). Report:
`.clavity/scratch/item8-panel/agy-round13.md`. **Verdict: NOT GREEN.** Three folds, one rejection:

- **A claim in the fallback rationale was overstated.** "The scrape takes both at one instant" is false:
  it takes PIXELS at one instant while `E` still comes from an earlier walk. Corrected — the fallback is
  justified by being better than nothing and by restoring today's behaviour, not by a synchronisation it
  does not have. **Its conclusion was rejected**: test (2) asks whether `PrintWindow` produced a usable
  image, and it did not, so retry-exhaustion still qualifies.
- **Disabling the detector on every scrape path opened a real blind spot.** Round 12's rule was right for
  the two-stage element/window split and wrong for the whole-image check: a FULL-DESKTOP scrape can come
  back entirely black — `SetWindowDisplayAffinity`, DRM content, a session in transition — with nothing
  to distinguish it from a dark screen. `uniformCanvas` now runs there too. A deliberate small scope
  widening, recorded rather than done quietly, folded rather than deferred because this project does not
  defer a defect for being pre-existing.
- **The degenerate-`W1` guard's justification was wrong and is corrected.** Traced properly, every path
  from a degenerate `W1` already ends in a refusal, so the guard IS redundant — rounds 11 and 13 were
  both right about that, and the "element scope has an uncovered path" defence was not. It is kept as
  explicit DEFENCE IN DEPTH, on the grounds that a leak-class guarantee should not rest on a three-rule
  interaction when all three rules have been rewritten during this review. Labelled as a judgement call,
  not as a necessity.

**Rejected by measurement:** that `windowResized` and `popupsNotRendered` are mutually exclusive. The
argument was that popups graft masks, so a window with a popup always has a non-empty mask set and would
refuse rather than warn. Checked at `PerceptionManager.cs:1106` — `pw.Add(resolution.Rect)` runs only for
elements that are actually REDACTED, so a popup containing nothing sensitive grafts zero masks. An open
menu with no password field, on a window that resizes mid-capture, produces both codes. The combination
is reachable and the contract must handle it.

**The Simplicity Auditor seat produced a correct finding and the right answer was still to keep the
code** — which is the more useful outcome than either blind deletion or a reflexive defence. Asking what
can be removed forced the real justification into the open and replaced a wrong one.

### AGY-AFTER adversarial panel — round 14

Seats: Fold Auditor (round 13's edits, with the late scope widening as its hardest target), Simplicity
Auditor second pass, **Failure-Mode Cartographer** (build the COMPLETE map — every distinct failure mode
× exactly what the caller receives — as a table, one row per mode, then reason from the table rather than
the prose). Report: `.clavity/scratch/item8-panel/agy-round14.md`. **Verdict: NOT GREEN.** Five folds:

- **The full-desktop detector's recourse sent the agent into a guaranteed failure.** It inherited the
  window-scope text — "read the UIA tree via `desktop_snapshot` instead" — but the usual cause of an
  all-black DESKTOP is a secure desktop, DRM content or a session transition, and **UIA is blocked in
  exactly those states too**. Split into its own code, `desktopCanvasUniform`, whose recourse says so and
  tells the agent to wait rather than to query a tree it cannot read.
- **`popupsNotRendered` was gated to `printWindow` and should not have been.** The Cartographer's table
  showed a timeout WITH popups and a timeout WITHOUT them producing an identical row. The scrape fallback
  captures `captureBounds`, so a popup extending beyond the window's rect is cropped off — the image is
  still missing its menus, and the code that would have said so was suppressed by the backend switch. It
  now fires on both backends, with recourse covering both mechanisms.
- **A retracted justification was still live one section away.** §1 explicitly retracts "the scrape takes
  both at one instant"; risk 2's fallback rule still used it. This is the same failure round 7 found — a
  correction that does not reach every place the claim appears — and it is the second instance, so it is
  worth stating as a pattern rather than a one-off.
- **The `CaptureResult` code block quoted only the "before".** Same defect class round 8 found in §5's
  tool-description paragraph: a spec that shows the current shape and tells you to extend it can be
  satisfied by pasting the current shape. The required end state, including the `CaptureWarning` record,
  is now written out.
- **§4 named no error code.** "The plan owns an explicit mapping" is an abdication of contract dressed as
  a deferral — the same shape §5 was corrected for in round 6. A null GDI handle is
  `ToolErrorCode.CaptureUnavailable`, matching what the scrape path already raises for an environmental
  failure; only the hint's wording is the plan's.

**Verified, not folded:** the peer named `ScreenshotTools.cs:27-28` as an assertion it had not checked.
Checked — those lines are exactly `if (!ScreenCapture.IsDesktopRenderable()) throw new
ToolException(ToolErrorCode.CaptureUnavailable, ...)`. The spec is correct there.

**The Cartographer seat found its defect in the SHAPE of the table, not in any row's content** — two
rows with materially different causes were textually identical. That is a defect no prose reading
surfaces, because prose never puts the two cases adjacent.

### AGY-AFTER adversarial panel — round 15

Seats: Fold Auditor (round 14's edits, hardest on the LATE introduction of a new type), **Pattern Hunter**
(sweep the whole document for the two failure patterns that had each already bitten it TWICE),
Failure-Mode Cartographer second pass. Report: `.clavity/scratch/item8-panel/agy-round15.md`.
**Verdict: NOT GREEN.** Five folds — and the Pattern Hunter found BOTH of its targets a third time:

- **A retracted claim, live one section away, third instance.** §5's `popupsNotRendered` recourse says the
  scrape crops popups beyond the window's rect; risk 8 still said the scrape "includes its pixels" flatly.
  Corrected — a window-scoped scrape captures `captureBounds` and nothing outside it, so it includes a
  popup only where it overlaps.
- **A "before" block with no "after", third instance.** §2 quoted the current yardstick computation and
  demanded a caller-supplied switch in prose without ever showing the resulting call. The end state is now
  written out, including the fail-safe default direction.
- **The algorithm block still described the OLD pipeline.** It showed `Encode(src, absolute, masks,
  maxWidth)` while §5 had already settled a nine-field `CaptureResult`, leaving the bridge from the
  detector logic to the constructor unstated.
- **Three amendments to one flow had stopped agreeing.** Element-scope resize said "REFUSE with a retry
  hint" in one place and "on exhaustion fall back to the scrape" in another — two terminal outcomes for
  one condition. Restated as a single four-step flow with no terminal refusal on that path.
- **Every §1 refusal was missing from the error map.** §4 mapped only the GDI case; the degenerate,
  minimized, empty-crop and resize-with-masks refusals had no `ToolErrorCode`, so an implementer would
  have invented one per site. All six now mapped, and **no new code is introduced** — each reuses one the
  agent contract already documents.

**The Pattern Hunter seat is the highest-yield lens of the review per unit of instruction.** It was given
no subject-matter question at all — only two failure SHAPES that had recurred — and it found a fresh
instance of each. Once a defect class has appeared twice in an artifact, hunting the class beats hunting
the content.
