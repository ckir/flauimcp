# FlaUI.Mcp Phase 3b-1 — Perception Completion & Stability — Design

**Goal:** Complete the read-only perception surface (screenshot, bounds, snapshot stats/global/diff, focus, wait conditions) and lock the coordinate/DPI contract that Phase 4 synthetic input is validated against. Every tool ships `readOnlyHint:true, destructiveHint:false`.

**Architecture:** New read-only tools layered on the existing split query/action STA dispatcher and option-C ref engine. Element/window bounds resolve on the STA; bitmap capture + PNG encode run off-STA. Wait loops poll via short STA ticks and sleep off-STA so they never monopolize the single query dispatcher. No synthetic input — that is deliberately isolated to Phase 4.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0 (incl. `FlaUI.Core.Capturing.Capture` for screen/element capture), ModelContextProtocol 1.4.0 (native `ImageContent` return), xUnit. P/Invoke limited to headless-desktop detection (`WTSQuerySessionInformation`/`OpenInputDesktop`); capture uses FlaUI's built-in — no manual `PrintWindow`/`System.Drawing` BitBlt.

**Release:** v0.4.0. Scope is **3b-1 only**. Phase 3b-2 (grid/text structured patterns + clipboard, all state-mutating) is a separate spec authored against 3b-1's merged code.

---

## 1. Scope

**In scope (7 new tools + 1 tool extension + 1 descriptor change + 2 plumbing items):**
New: `desktop_screenshot`, `desktop_get_bounds`, `desktop_snapshot_stats`, `desktop_snapshot_diff`, `desktop_wait_for`, `desktop_wait_for_stable`, `desktop_get_focused_element`. Extension: `desktop_list_windows` gains opt-in `includeBounds`/`includeStats`/`zOrder` (replaces a standalone `snapshot_global`). Plus the always-on `focused` descriptor flag; the `RefRegistry.Resolve` recycle-guard strengthening; and a **Task-0 `SnapshotEngine` node-model refactor** (prerequisite — see §8).

**Out of scope (→ 3b-2 or later):** grid (`get_grid_cell`/`grid_select`), text patterns (`get_text`/`set_caret`/`select_text_range`), clipboard (`clipboard_get`/`set`), any synthetic input, UIA event streaming, OCR.

---

## 2. The coordinate / DPI contract (keystone)

All bounds are returned as **absolute, signed, physical screen pixels** in the virtual-desktop coordinate space.

- **Signed / multi-monitor safe.** When the primary monitor is not top-left, the virtual desktop has **negative** X/Y. Bounds use signed ints throughout; nothing assumes a `(0,0)` origin.
- **Math is done for the agent.** The returned `{x,y,w,h}` are already physical screen pixels usable directly by a future action — the agent never multiplies by a scale factor.
- **`dpiScale` is informational only.** Returned as metadata — the effective scale of the monitor containing the captured region's (or element's) top-left, e.g. `1.5` — never load-bearing for targeting.
- **Phase-4 targeting stays ref-based / percentage-based.** Phase 4's `desktop_click` (ref → server computes center) and `desktop_click_at` (`xPct`/`yPct`, scale-invariant) mean no image-pixel→screen math is ever required of the agent. Screenshot pixels are for *vision/orientation*, not coordinate arithmetic.
- **Downscale caveat (documented):** when `maxWidth` downscales the returned image, image-pixel offsets are not screen pixels. This is acceptable precisely because targeting is ref/percentage-based, not image-pixel-based.

---

## 3. Tools

### 3.1 `desktop_screenshot`

Capture a window, a single element, or the full virtual desktop as a PNG.

**Params:**
| name | type | default | notes |
|---|---|---|---|
| `window` | string? | — | window handle (e.g. `w1`). Omit + omit `ref` → full virtual desktop. |
| `ref` | string? | — | element ref; capture that element (window-captured then cropped). Requires `window`. |
| `output` | `"inline"` | `"inline"` | **Only `inline` is implemented in v0.4.0.** The param exists so `"file"` is a non-breaking future add. Passing `"file"` → `NotImplemented` error. |
| `maxWidth` | int? | `1600` | downscale so width ≤ `maxWidth` (preserves aspect). `0`/null disables. |

**Returns:** MCP `ImageContent` (base64 PNG) **plus** a JSON metadata block:
```
{ "bounds": {"x":-1920,"y":0,"w":1920,"h":1080}, "dpiScale":1.5,
  "scaleApplied":0.833, "redactions":3 }
```
- `bounds` — absolute physical screen pixels of the captured region (§2).
- `scaleApplied` — downscale factor applied (`1.0` if none).
- `redactions` — count of black-rect redactions painted (§6).

**Capture mechanics (FlaUI built-in, screen-scrape):** uses `FlaUI.Core.Capturing.Capture` — `Capture.Element(el)` / `Capture.Screen()` / a rectangle for the virtual desktop. This is a **screen-region** capture: it does **not** see through occlusion, so a covered window captures the covering pixels. Mitigation: callers `desktop_focus_window` first (already shipped); the limitation is documented and PrintWindow-based occlusion capture is a backlog item. **Minimized windows** (`WindowVisualState.Minimized`) are rejected with `ElementNotActionable` ("restore the window first") — capturing them yields black.

**Return shape:** a **native MCP `ImageContent`** block (base64 PNG) + a `TextContent` metadata block, returned via a new `ToolResponse` image helper — **not** the `Task<string>` JSON path (which would bury the image as text and bloat context).

**Errors:** `CaptureUnavailable` (headless/disconnected desktop — §6), `ElementNotActionable` (minimized), `TargetDenied` (denylisted window — §6), `WindowNotFound`, `RefNotFound`, `NotImplemented` (`output:"file"`).

### 3.2 `desktop_get_bounds`

**Params:** `window` (string), `ref` (string).
**Returns:** `{ "bounds":{"x","y","w","h"}, "dpiScale", "isOffscreen":bool }`. Absolute physical px (§2). `isOffscreen` surfaces UIA `IsOffscreen` (note: that means scrolled/virtualized out, **not** "covered by another window" — occlusion is not knowable from UIA).
**Errors:** `WindowNotFound`, `RefNotFound`.

### 3.3 `desktop_snapshot_stats`

Cheap orientation without the full tree.
**Params:** `window` (string) **or** `snapshotId` (string) — exactly one.
**Returns:**
```
{ "snapshotId":"w1:4", "total":312, "interactive":47, "offscreen":15,
  "redacted":1, "byControlType":{"Button":40,"Text":120,"Edit":6, ...} }
```
Computed from a fresh snapshot (`window` form) or a cached one (`snapshotId` form).
**Errors:** `WindowNotFound`, `SnapshotNotFound`, `InvalidArguments` (both/neither supplied).

### 3.4 `desktop_list_windows` extension (not a new tool)

Rather than a near-duplicate `snapshot_global`, extend the **existing** `desktop_list_windows` (Win32 hang-proof enum) with opt-in detail. **Default output is unchanged** (back-compat).
**Added params:** `includeBounds` (bool, default `false`) → adds `bounds` + `zOrder`; `includeStats` (bool, default `false`) → additionally walks each window for §3.3 stats (G1: slow/CPU-spiky on big apps — opt-in only).
**Added fields:** `bounds` (absolute physical px), `zOrder` (top-to-bottom index, 0 = topmost, for occlusion reasoning), `stats` (null unless `includeStats:true`). Per-window stat failures degrade to `stats:null` with a `note`.

### 3.5 `desktop_snapshot_diff`

**Baseline is explicit — no server "last" state.**
**Params:** `window` (string), `baselineSnapshotId` (string, **required**). Re-snapshots `window` now and diffs against `baselineSnapshotId`.
**Returns:**
```
{ "baselineSnapshotId":"w1:2", "currentSnapshotId":"w1:5",
  "added":[<descriptor>...], "removed":[<descriptor>...],
  "changed":[ {"ref","was":{"name","value","enabled"},"now":{...}} ... ] }
```
- **Identity key:** RuntimeId, with `(ControlType, AutomationId, Name)` as tiebreak when RuntimeId is absent/recycled.
- `added` = identities in current not in baseline; `removed` = baseline not in current; `changed` = same identity, differing Name / value / enabled / focused.
- Refs in the result are scoped to `currentSnapshotId`.
**Errors:** `WindowNotFound`, `SnapshotNotFound` (baseline evicted), `SnapshotWindowMismatch` (baseline belongs to a different window).

### 3.6 `desktop_wait_for`

One **selector** predicate, evaluated against a **fresh** tree each poll (no ref staleness trap).
**Params:**
| name | type | default | notes |
|---|---|---|---|
| `window` | string | — | required |
| `by` | `"automationId"\|"name"\|"controlType"` | — | required |
| `value` | string | — | match target for `by` |
| `until` | `"exists"\|"enabled"\|"gone"\|"valueEquals"` | `"exists"` | condition |
| `equals` | string? | — | required iff `until:"valueEquals"`; compares the matched element's ValuePattern value |
| `timeoutMs` | int | `5000` | |
| `pollIntervalMs` | int | `500` | a full UIA walk of a dense window can take 150–300ms; 500ms keeps the single query STA off 100% duty cycle |

**Returns (timeout is a NORMAL result, never an exception):**
`{ "satisfied":bool, "ref":string?, "elapsedMs":int, "snapshotId":string }` — `ref`/`snapshotId` present when satisfied (a final fresh snapshot is registered so the agent can act immediately).
**Semantics:** each poll resolves the selector fresh; a not-yet-existing element is simply "not satisfied," never an error. `gone` is satisfied when no element matches. A selector matching multiple elements resolves to the **first in tree order** (consistent with option-C). `valueEquals` reads the value via `ValuePattern → Name → LegacyIAccessiblePattern.Value`; an unreadable value counts as **not satisfied** (never throws `PatternUnsupported` mid-wait). **Poll reads are transient — they do NOT `Register` refs** (no per-tick `RefRegistry` growth); only the final satisfying snapshot is registered. `InvalidArguments` if `valueEquals` without `equals`.
**Errors:** `WindowNotFound`, `InvalidArguments`. (No `WaitTimeout` — timeout ⇒ `satisfied:false`.)

### 3.7 `desktop_wait_for_stable`

Wait until a subtree stops structurally changing.
**Params:** `window` (string), `ref`/`by`+`value` (optional — **scope the check to a subtree**; default = whole window), `includeText` (bool, default `false`), `quietMs` (default `500`), `timeoutMs` (default `5000`), `pollIntervalMs` (default `500`).
**Returns:** `{ "stable":bool, "elapsedMs":int, "snapshotId":string }`.
**Stability signature:** structural hash over the scoped subtree = ordered set of `(ControlType, AutomationId, depth)`. By **default it excludes volatile Name/Value**, so a blinking caret / live clock / spinner can't block stability forever. When `includeText:true`, `Name`/`Value` are folded into the signature — use it to wait on a status-text settle (`"Saving…"`→`"Ready"`); do **not** combine it with a window that has a live clock/counter or it may never report stable. Stable when the signature is unchanged across `ceil(quietMs/pollIntervalMs)` consecutive polls. Timeout ⇒ `stable:false` (normal result). The optional subtree scope is the second antidote to perpetual motion.
**Errors:** `WindowNotFound`, `RefNotFound`/`SelectorNoMatch` (when a scope was given).

### 3.8 `desktop_get_focused_element`

O(1) "where am I" — UIA `IUIAutomation::GetFocusedElement`, far cheaper than a full snapshot.
**Params:** none.
**Returns:** `{ "ref":string, "descriptor":<descriptor line>, "window":{"handle"?,"title","pid"} }` — registers the focused element in a fresh single-element snapshot context and returns its ref. `window.handle` is present if the owning window is already open, else null (+ title/pid).
**Errors:** `AccessDeniedIntegrity` (focus on a UAC/secure desktop — caught COM `ACCESS_DENIED`), `NoFocusedElement` (genuinely nothing focused).

---

## 4. Descriptor change — `focused` flag

`ElementDescriptor` gains an always-on boolean `focused` (UIA `HasKeyboardFocus`). Snapshot lines render it in the brace group: `{enabled, focusable, focused}` (omitted when false, like the other flags). This is the cheap always-on companion to `get_focused_element` (D4: both kept — the flag for board-context during a snapshot, the tool for O(1) focus lookup without re-walking the tree). `snapshot_diff.changed` treats a `focused` transition as a change.

---

## 5. Plumbing — `RefRegistry.Resolve` recycle guard

**A ControlType-based recycle guard already exists.** `RefRegistry.Resolve`'s fast-path (`RefRegistry.cs:83`) already gates the cached element on `RuntimeId AND ControlType AND !IsOffscreen`. It misses **same-ControlType** recycling: a virtualized `DataGrid` reusing a row peer's RuntimeId keeps `ControlType=DataItem`, so the guard passes and returns the wrong row's data. **Fix = strengthen the existing equality** — additionally compare the cached element's live `Name` to `descriptor.Name` on the fast-path; on mismatch, fall through to the existing cache-free `ResolveDescriptor` walk (which already re-finds by AutomationId then Name+ControlType). This is a one-line strengthening of a guard that already exists, not a new mechanism. Load-bearing now because actions (3a) and these new tools consume refs.

---

## 6. Screenshot security & environment floors

- **Visual pixel redaction (D6) — computed at capture time, never persisted.** Masks are computed when the screenshot is taken (Claim 3: bounds/`IsPassword` are not stored on the descriptor, and a persisted bound goes stale the instant a window moves/scrolls → would mis-place the rect and leak the secret; capture-time uses **live** bounds). **Window/element capture:** run a targeted `IsPassword=true` query scoped to the captured window, take live bounds, and paint opaque black rectangles mapped through the same crop + `scaleApplied` transform as the image. **Full-desktop capture:** black the **entire Win32 box** of every credential-**denylist** window (we refuse to walk them, so per-field is impossible — G2), and best-effort per-field redaction for non-denied windows; window-scoped capture is recommended when secrets are plausible. `redactions` reports the count. Rationale: the Phase-2 tree redaction protects `Name`/value; a screenshot would otherwise leak the same secret as pixels (e.g. a login form inside an ordinary, non-denylisted app window). Redacting text but leaking pixels is a broken model.
- **Credential-window denylist.** `screenshot` refuses to capture a denylisted credential/UAC window outright (`TargetDenied`), consistent with the Phase-2 snapshot denylist.
- **Headless / disconnected-desktop detection.** Before capture, detect a locked or RDP-disconnected session (DWM not rendering → black frames). If headless, return `CaptureUnavailable` with `suggestedRecovery:"the desktop session is disconnected or locked; reconnect to restore rendering"` rather than returning a black image. Detection: `OpenInputDesktop`/`WTSQuerySessionInformation` (session connect-state) check.
- **Honest limitation (documented):** redaction relies on UIA reporting `IsPassword`; an app that reveals a password (toggles `IsPassword` false) is, by its own declaration, no longer secret — pixels show it, same as the tree.

---

## 7. Error codes (additions to `ToolErrorCode`)

**Reuse existing** (verified in `ToolErrorCode`): `TargetDenied` (denylisted-window capture), `AccessDeniedIntegrity` (focus on secure/UAC desktop), `ElementNotActionable` (minimized-window capture), `WindowNotFound`, `RefNotFound`, `InvalidArguments`. **Add new:** `CaptureUnavailable`, `SnapshotNotFound`, `SnapshotWindowMismatch`, `SelectorNoMatch`, `NoFocusedElement`, `NotImplemented`. Every envelope keeps `suggestedRecovery`. (Note `Timeout` already exists but is deliberately **not** used for waits — D5: timeouts return `{satisfied:false}` data.)

---

## 8. Architecture / file placement

- **Core/Perception/SnapshotEngine.cs** (refactor — **Task 0, prerequisite**) — project a `SnapshotNode` model (ref, ControlType, AutomationId, Name, bounds, isPassword, focused, patterns, depth) from `Walk`; render the existing text **from** the model so output stays **byte-identical** (pinned by current snapshot tests). `Stats`/`Diff`/stability-signature then consume the model. Today `Walk` returns only `(string,int)` — this unblocks all three.
- **Core/Perception/ScreenCapture.cs** (new) — wraps `FlaUI.Core.Capturing.Capture` (screen/element), headless/disconnected detection (`OpenInputDesktop`/`WTS`), capture-time redaction painting on the returned bitmap, PNG encode. **No occlusion handling** (screen-scrape) — callers focus-first.
- **Server/Tools/ScreenshotTools.cs** (new) — `desktop_screenshot` (native `ImageContent` via a new `ToolResponse` image helper, **not** `Task<string>`), `desktop_get_bounds`.
- **Server/Tools/SnapshotTools.cs** (extend) — `snapshot_stats`, `snapshot_diff`, `get_focused_element`; extend `desktop_list_windows` with `includeBounds`/`includeStats`/`zOrder`.
- **Core/Perception/WaitCoordinator.cs** (new) — `wait_for`/`wait_for_stable`: one short `RunQueryAsync` tick + off-STA `Task.Delay` per poll; **transient reads, never `Register` per tick** (a `Task.Delay` inside `RunQueryAsync` would deadlock the query STA).
- **Core/Perception/ElementDescriptor.cs** (extend) — `focused`.
- **Core/Perception/RefRegistry.cs** (extend) — strengthen the existing `:83` fast-path guard with a `Name` compare.

**Threading discipline:** bounds/selector resolution and FocusedElement run on the query STA via the existing dispatcher; bitmap capture + encode run on a worker (GDI is thread-agnostic; never block the STA on encode). Wait loops issue one short `RunQueryAsync` per tick and `Task.Delay` between ticks off-STA, so the single query dispatcher stays free between polls.

---

## 9. Testing (RDP-aware)

Per the test-env constraint (RDP-only, no physical console), **screenshot capture works over RDP** while the session is connected (framebuffer present) — unlike synthetic input — so 3b-1 is fully testable on the dev/CI box. TestApp-driven (`[Trait("Category","Desktop")]`):

- **screenshot:** returns non-null PNG of expected dims; `bounds` equals the element's UIA bounds; `dpiScale` equals the monitor scale; element crop matches `get_bounds`; redaction — a TestApp `PasswordBox` region is black in the output (`redactions>=1`).
- **get_bounds:** equals UIA `BoundingRectangle`; signed-coord path exercised if a second (negative-origin) monitor is present (else documented manual gate).
- **snapshot_stats/global:** counts match a full snapshot's tally; global lists the TestApp window.
- **snapshot_diff:** mutate the TestApp tree (add a control / change a label) → assert `added`/`changed`; `SnapshotWindowMismatch` on a foreign baseline.
- **wait_for:** TestApp reveals a control after a delay → `satisfied:true` with ref; missing control → `satisfied:false` at timeout (no exception); `valueEquals` on a label that updates.
- **wait_for_stable:** animated TestApp element blocks whole-window stability but a scoped subtree goes stable; timeout ⇒ `stable:false`.
- **get_focused_element + focused flag:** focus a known control → tool returns its ref; snapshot line shows `focused`.
- **recycle guard:** simulate a recycled RuntimeId (reuse id, change Name/AutomationId) → resolve returns stale/`RefNotFound`, not the wrong element.
- **CaptureUnavailable:** documented manual gate (disconnect RDP) — automated check asserts the detection predicate in isolation.

---

## 10. Safety summary

All 3b-1 tools `readOnlyHint:true, destructiveHint:false`. The only new attack surface is the screenshot pixel channel, floored by visual redaction + denylist + headless detection (§6). No state-changing capability is added; the state-mutating completion (grid/text/clipboard) is deferred to 3b-2 under the established blast-radius discipline.
