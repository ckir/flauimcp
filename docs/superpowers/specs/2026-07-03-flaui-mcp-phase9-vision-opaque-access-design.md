# Phase 9 — Vision & Opaque-App Access (design spec)

**Status:** DRAFT (spec, not plan — no code exists yet). Gated on Phase 8 merged (✓ v0.8.0 shipped). Target **v0.9.0**.
**Origin:** AGY-FIRST divergent consult (cascade f0977c1d) + USER delegated the pick to me as the tool's consumer
([[feedback-flaui-mcp-claude-is-consumer]]) + a **decisive live wake-up spike** (below). This spec cites that spike.

---

## §1 Problem

The #1 documented limitation: **zero-UIA blindness.** Chromium/Electron (VS Code, Slack, Teams, Discord, Chrome),
games, Citrix/RDP-embedded, and pure-`<canvas>` surfaces collapse to ONE opaque UIA node — the agent can neither
perceive nor target anything inside. Phases 2–8 gave rich perception/input/events for *accessible* apps; Phase 9
extends that to the *opaque* ones.

## §2 Design principle (the reframe)

**Claude (the consumer) is multimodal — it already SEES screenshots** (`desktop_screenshot` returns a PNG image
block the model reads). So Phase 9 is NOT a server-side "screen reader" that dumps text. The agent's real gap on
opaque apps is **precise targeting** (LLMs estimate pixel coordinates poorly) and **native structure where it's
cheaply recoverable**. Therefore, two-pronged, native-first:

- **Prong A — Accessibility Wake (the 80%, native, full precision).** For Chromium/Electron, *activate* the native
  UIA tree instead of falling back to OCR. **Spike-confirmed feasible** (§4).
- **Prong B — OCR text targeting (the residual only).** For genuinely zero-accessibility surfaces (games, Citrix,
  canvas, and an editor's *text body*), use on-box OCR strictly as a **coordinate resolver** (§5), pairing with the
  agent's own screenshot vision + `desktop_click_at`.

Rejected (agy + consumer): full-screen OCR screen-reader (redundant with the model's vision); Set-of-Mark overlays
(heavier, partly redundant). OCR is the *aim*, not the *eyes*.

## §3 Tool surface (proposed — see §11 forks)

- **`desktop_wake_accessibility(window) → {wakeId}`** / **`desktop_release_accessibility(wakeId)`** — register and
  HOLD a UIA event handler on the window to keep Chromium's AXMode active; the tree stays hydrated until released
  or the window closes. ReadOnly + lease-exempt (observation only). (FORK §11.1: dedicated primitive vs reuse
  `desktop_watch` vs auto-wake-on-snapshot-of-opaque.)
- **`desktop_find_text(query, window?|region?, {matchMode, all}) → {matches:[{text, bounds:[x,y,w,h], center:[x,y],
  xPct, yPct, confidence}]}`** — OCR the window/region, return every text run matching `query` with coordinates in
  BOTH physical-screen space (`bounds`/`center`) AND `desktop_click_at`'s window-fraction space (`xPct`/`yPct`), so
  the agent can click a match directly (§6). On-box `Windows.Media.Ocr`. ReadOnly + lease-exempt. **`region` is
  window-relative FRACTIONS `[xPct,yPct,wPct,hPct]` (R2 Seat B)** — NOT physical/logical px (the agent has no reliable
  px frame; it thinks in window-fractions like `desktop_click_at`); the server maps region→physical + handles
  cropping. **False-positive caveat (R2 Seat C):** a fuzzy `query` like "Submit" can match inside body text ("Click
  Submit below"); every match already carries its `text`+`bounds`+`center`, and the agent MUST inspect the matched
  text/position (or the screenshot) before firing `desktop_click_at` — prefer returning ALL matches (`all:true`) so
  the agent picks the right occurrence rather than blindly acting on the first.
  **`matchMode` defaults to FUZZY (Seat A):** OCR mis-reads real UI text ("Submit"→"5ubmit"/"Subm it"), so matching
  MUST normalize whitespace + case and allow a bounded edit-distance / confidence tolerance by default; `matchMode:
  "exact"` is opt-in. Exact-only matching would make find_text/wait_for_text spuriously miss visibly-present text.
- **`desktop_wait_for_text(query, window?|region?, timeoutMs) → {satisfied, match?}`** — OCR-anchored wait (the
  novel bit; UIA-based `desktop_wait_for` already ships). Polls `find_text` (fuzzy) until a match appears or timeout;
  timeout returns `{satisfied:false}` DATA, not an error (mirrors `desktop_wait_for`). **Hard poll throttle (Seat C):**
  `Windows.Media.Ocr` allocates large bitmaps + heavy CPU, so the poll loop MUST enforce a generous minimum interval
  (≥ ~750 ms) between OCR passes and run OCR OFF the query STA — a tight loop would thrash GC and starve the STA/host.
- **Wakeability hint on `desktop_snapshot` (Seat E — closes the guess-and-check token trap):** an opaque window is a
  single empty `Pane` whether it's wakeable (Chromium/Electron) or not (a game/canvas); the agent cannot tell which
  tool to reach for. `desktop_snapshot` (and/or `desktop_list_windows`) MUST expose a `wakeable` hint on an opaque
  root, derived from the Win32 `ClassName` (`Chrome_WidgetWin_1` etc.) / process, so the agent picks wake-vs-OCR in
  ONE round-trip instead of wake→re-snapshot→check→maybe-unwatch→fallback. **Condition it (R2 Seat E): `wakeable =
  IsChromiumClass AND tree-is-collapsed/empty`** — a Chromium app already accessible (screen reader running, or
  launched `--force-renderer-accessibility`) has a rich tree already; flagging it `wakeable` would waste a wake
  round-trip. If the window already exposes real descendants, OMIT the hint (nothing to wake). (This is a small additive perception
  change, adjacent to Phase 9's scope; include it here since it is what makes the two-prong routing usable.)

Not included in v1 (FORK §11.4): a full-screen `desktop_read_text` dump — the model reads screenshots itself.

## §4 Prong A — Accessibility Wake (SPIKE-CONFIRMED, 2026-07-03)

**Mechanism (measured live on VS Code, PID 26200):** registering a UIA event handler on a Chromium/Electron window
triggers its AXMode → the native accessibility tree hydrates. Evidence:

| Step | Result |
| --- | --- |
| Snapshot, a11y OFF | **14 nodes** — window frame + native Min/Restore/Close chrome + nested EMPTY `Pane`s; entire editor/tree/tabs/statusbar invisible. |
| `desktop_watch` the window (registers a UIA event handler) → re-snapshot | **236 nodes** — full Files-Explorer tree (every file a named `TreeItem` w/ path), menu bar, activity tabs, editor tabs, all toolbars/buttons w/ labels, status bar. |
| `desktop_unwatch` → re-snapshot | **collapses to 15 nodes** — Chromium re-sleeps the tree when the AT disconnects. |

**Two load-bearing consequences:**
1. The Phase-8 `desktop_watch` event-registration IS the wake trigger — Phase 9 reuses this exact mechanism.
2. **The wake is NON-PERSISTENT** — it must be HELD (a live registration) for the whole interaction; on release the
   tree tears down. So `desktop_wake_accessibility` returns a HELD handle (like a subscription), and its lifecycle
   reuses the Phase-6 `WindowInvalidated` auto-evict chokepoint (release on window close).
3. **WAKE ≠ WATCH — null-sink its events + exempt from watch caps (R2 Seats A/D):** waking a complex app hydrates
   236+ nodes → a STORM of `StructureChanged` events. Wake registers the handler ONLY to activate AXMode; it MUST
   DROP those events at the COM-callback edge (a null sink) — it must NOT feed the Phase-8 `EventCoalescer`/channel/
   drain pipeline (the agent didn't ask to *watch*). And because waking is a BASELINE PERCEPTION need (an agent may
   need Slack+VSCode+Teams+Chrome+Discord awake at once just to *see* them), wake handles MUST be **exempt from the
   `WatchRegistry` 5/window·20/session caps** (or carry their own, much higher, separate limit) — do NOT let waking
   burn the event-watch quota. Wake and watch share the underlying UIA-registration mechanism but are separate
   surfaces with separate accounting.

**Caveat (measured):** an editor's *text body* stays behind a screen-reader-mode gate (node showed "The editor is
not accessible… use Shift+Alt+F1") — but ALL structure/chrome/tree hydrates. So even for VS Code, waking covers
navigation/commands/file-tree natively; only the *document text* falls to Prong B (OCR) or the model's own vision.

**Open (build-time spike):** does waking generalize across Chromium versions / other Electron apps (Slack/Teams)
and non-Chromium opaque apps? Does the held-handle need to be a specific event kind (StructureChanged sufficed)?
Is there a lighter activation than a full watch (e.g. a one-time `UiaRootObjectId` `WM_GETOBJECT`) that still holds?

## §5 Prong B — OCR engine

**`Windows.Media.Ocr`** (WinRT, on-box, free, ships with Windows; language packs via the OS). No cloud, no external
dep. **Build-time feasibility spike:** confirm a `net10.0-windows` project can reference the WinRT projection
(CsWinRT / `Microsoft.Windows.SDK.Contracts` or the built-in projections) and run OCR on a captured `SoftwareBitmap`.
If the reference is problematic, fall back is a spike decision (Tesseract as a bundled native dep is the alternative,
but avoid if WinRT works). v1 = one engine; no pluggable abstraction until a second is needed.

## §6 The coordinate contract (THE dealbreaker — get this exactly right)

OCR runs on the **screenshot bitmap**, which is (a) offset by the capture origin and (b) **downscaled** by the
screenshot pipeline. Grounded against the existing code:
- `desktop_screenshot` returns `bounds {x,y,w,h}` (physical screen px of the captured region) + `scaleApplied`
  (downscale factor; width clamped ≤1600, hard 1920 ceiling) — `ScreenshotTools.cs`.
- `desktop_click_at` consumes **window-relative fractions** `xPct`/`yPct ∈ [0,1]` of the window's PHYSICAL rect,
  mapped via `CoordinateMath.PctToPhysical(left, top, width, height, xPct, yPct)`.

So the mapping `find_text` MUST implement (and unit-test headless, pure math):
```
bitmapPx            (OCR output, in the downscaled capture bitmap)
→ screenPx  = { x: bounds.x + bitmapPx.x / scaleApplied,   y: bounds.y + bitmapPx.y / scaleApplied }   // undo downscale + add origin
→ windowPct = { xPct: (screenPx.x - winRect.left) / winRect.width,  yPct: (screenPx.y - winRect.top) / winRect.height }
```
`find_text` returns BOTH `center`/`bounds` (physical screen px, pairs with `desktop_get_bounds`) AND `xPct`/`yPct`
(pairs with `desktop_click_at` directly). **This normalization is the single feature-killer if wrong** (on a 150%
display an un-normalized box clicks ~200px off). A headless `CoordinateMapping` core + a Desktop test that OCRs a
known-position control and asserts the derived click lands on it is mandatory (§10). NOTE: for OCR accuracy the
capture should ideally NOT be downscaled (`maxWidth:0` path) — but the 1920 hard ceiling still applies, so the
`scaleApplied` term is never skippable. **CROPPED-CAPTURE HAZARD (Seat D — the origin term must be the CAPTURE's
rect, not the window's):** the math above is correct only if `bounds.x/y` is the top-left of what was ACTUALLY
captured. A window straddling monitors or partly off-screen may be captured cropped to visible bounds and/or across
mixed-DPI monitors. Build-time grounding: confirm what `desktop_screenshot` `bounds` returns (capture rect vs window
rect) — `find_text` MUST offset by the real capture origin, and the headless guard + a Desktop test MUST cover
partial-viewport, negative-origin (multi-monitor left/top), and mixed-DPI captures explicitly.

## §7 When to wake vs OCR vs plain UIA (agent guidance, baked into tool descriptions + SKILL.md)

1. Rich UIA app → just `desktop_snapshot`/`desktop_find` (don't OCR).
2. Opaque **Chromium/Electron** (one big empty `Pane`) → `desktop_wake_accessibility`, THEN native snapshot/find/
   interact with full precision. This is the 80%.
3. Opaque **non-accessibility** surface (game / Citrix / canvas / editor text body) → `desktop_find_text` to target,
   `desktop_click_at` to act; read content via the agent's own screenshot.
Self-trigger/ephemerality notes from Phase 8 carry over where a held wake also emits events.

## §8 Security & redaction

- **Wake** is observation-only → ReadOnly + lease-exempt (no synthetic input; consistent with §Phase-8 watch).
- **OCR redaction (INV-5 parity):** OCR reads only *rendered* pixels — a masked password renders as `••••`, so OCR
  reads bullets, not the secret (unlike UIA which can read the underlying `Value`). Mostly a non-issue (agy, folded).
  Belt-and-suspenders (FORK §11.5): if a UIA password element overlaps an OCR region, suppress OCR text there — but
  this requires the UIA tree, which opaque surfaces lack, so it is best-effort only. Screenshots already redact
  password fields at capture; `find_text` should reuse that capture path so any capture-time redaction applies.
- Deny-list: `find_text`/`wake` on a denied process refuse before doing work (reuse `PerceptionPolicy.IsDenied`).
- **NESTED-PROCESS DENY BYPASS in remote wrappers (Seat B — document as a limitation):** the deny-list is
  process-coarse, and OCR reads rendered pixels *blind to nested boundaries*. A denied app running INSIDE an allowed
  RDP/Citrix/VM wrapper (`mstsc.exe`, `wfica32.exe`, a hypervisor console) is invisible to the process check — OCR can
  perceive/target it, punching through process-coarse enforcement for remote sessions. This is inherent to
  pixel-level perception (there is no nested-process identity in pixels). Mitigation is limited: document it plainly,
  and let an operator deny the *wrapper* process itself if they must block all OCR into remote sessions. (Parallels
  the already-documented process-coarse limitation for in-process navigation to credential surfaces.)

## §9 Lifecycle & threading

- Wake handles are held UIA registrations on the query STA — reuse the Phase-8 `Uia3EventSource`/self-marshaled
  teardown + Phase-6 `WindowInvalidated` auto-evict (release the wake when the window closes).
- OCR runs off the query STA (it needs only a captured bitmap, not live UIA). **For `wait_for_text`'s repeated poll,
  BOTH the capture AND the OCR must be OFF the query STA (R3 Seat A):** a 750 ms loop that forces a screen capture on
  the single query STA each pass (capture is a blocking ~50–150 ms op) would consume a large fraction of STA time and
  stall concurrent `desktop_find`/`desktop_click`. Screen capture does NOT require the UIA COM apartment — use a
  capture path decoupled from the query STA for the polling loop (a one-shot `find_text` may reuse the existing
  screenshot path, but the WAIT loop must not repeatedly hit the STA). Build-time: confirm/introduce an STA-free
  capture for this path.

## §10 Testing

- **Headless:** `CoordinateMapping` (bitmapPx→screenPx→windowPct, incl. `scaleApplied` and origin) — pure math, the
  dealbreaker's guard; `find_text` match logic (substring/case/matchMode) against a fake OCR result.
- **Desktop (`Category=Desktop`):** (a) **wake** — launch VS Code, snapshot (assert opaque/~≤15 nodes), wake, snapshot
  (assert hydrated, e.g. a named `TreeItem` present), release, snapshot (assert re-collapsed) — this is the §4 spike
  turned into a regression test; (b) **find_text coord** — OCR a control of known screen position and assert the
  derived click coordinate lands inside its bounds; (c) **wait_for_text** timeout returns data not error.

## §11 Open forks (USER decides; my consumer lean noted)

1. **Wake primitive shape** — (a) dedicated `desktop_wake_accessibility(window)` held handle *(my lean: cleanest —
   separates "keep the tree awake" from "I want events")*; (b) reuse `desktop_watch` (agent watches to wake — zero new
   surface, but conflates intent); (c) auto-wake on `desktop_snapshot` of an opaque window (magic ergonomics, but a
   held handle + surprising cost/lifecycle on every snapshot). Lean: **(a)**, and document that `desktop_watch` also
   wakes as a known side-effect.
2. **`find_text` coordinate return** — return window-fractions (`xPct`/`yPct`, click_at-ready) AND physical
   `center`/`bounds`. *Lean: both* (fractions for click_at, physical for get_bounds parity). No new click primitive.
3. **OCR engine** — `Windows.Media.Ocr` only for v1, no pluggable abstraction *(lean: yes, add abstraction only when a
   2nd engine is justified)*.
4. **Full `desktop_read_text` dump** — include or skip? *Lean: skip v1* — the model reads screenshots; `find_text` is
   the targeting primitive. Reconsider if a non-multimodal client ever consumes this server.
5. **OCR-over-password suppression** — best-effort only (needs a UIA tree opaque surfaces lack); rely on capture-time
   screenshot redaction. *Lean: document the limitation, don't over-engineer.*

## §12 Build-time spikes (gate the plan, like Phase 8's spikes)

- **Spike α (OCR engine):** `net10.0-windows` can reference + run `Windows.Media.Ocr` on a `SoftwareBitmap`; measure
  accuracy on small UI text and the coordinate round-trip on a scaled display.
- **Spike β (wake generality):** does the §4 wake reproduce on other Electron (Slack/Teams) and is StructureChanged
  the minimal held registration; is a lighter one-shot activation possible.

## §13 Version / docs

`0.8.0 → 0.9.0`: CHANGELOG, ROADMAP (Phase 9 shipped; OCR/vision row), README (new "Opaque apps: wake + find_text"
section + Known-limitations update — OCR is targeting not reading; editor-text gate), SKILL.md (§7 decision guidance:
wake-first for Chromium, find_text for the residual).

## §14 Self-review (spec coverage)

Problem (§1) → principle/reframe (§2) → surface (§3) → wake mechanism spike-confirmed (§4) → OCR engine (§5) →
coordinate dealbreaker grounded in real code (§6) → agent decision guidance (§7) → security/redaction (§8) →
lifecycle reuses Phase-6/8 chokepoints (§9) → testing incl. the wake regression + coord-landing test (§10) → forks
for the user (§11) → build-time spikes gate the plan (§12) → version/docs (§13). Both original cruxes de-risked:
wake = SOLVED (held UIA-event reg, spike-confirmed); OCR-coordinate-mapping = grounded contract + mandatory headless
guard. Residual unknowns are explicit build-time spikes (α/β), not hidden gaps — the correct spec/plan boundary.

## §15 AGY-AFTER panel record (3 rounds → converged)

Team-panel review (cascade f0977c1d), folded WITH my own verification. **R1 (5 findings, all folded):** fuzzy match
default (§3); RDP/Citrix nested-deny bypass documented (§8); wait_for_text poll throttle + off-STA (§3); cropped/
mixed-DPI capture-origin contract (§6); `wakeable` snapshot hint (§3). **R2 (5, all folded):** wake null-sinks events
+ exempt from watch caps (§4.3); `region` = window-fractions (§3); fuzzy false-positive → return match text+bounds,
verify before click (§3); `wakeable` = class AND empty-tree (§3). **R3 (1 finding, then DRIED UP — 4/5 seats "no new
findings" + affirmed the dedicated wake primitive and backward-compat of the hint):** wait_for_text CAPTURE (not just
OCR) must be off the query STA (§9). **STOP:** round 3 landed no broad challenge (diminishing-returns signal per the
panel discipline). One design decision was CHALLENGED and survived: the dedicated `desktop_wake_accessibility`
primitive vs merely documenting `desktop_watch`'s side-effect — the panel affirmed the dedicated primitive (else it
collides with the 20/session caps + emits unwanted event spam). Net: two prongs unchanged; ~11 refinements folded.
