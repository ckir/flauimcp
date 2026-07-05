---
name: driving-flaui-mcp
description: Use when driving or dogfooding this project's installed FlaUI.Mcp desktop-automation MCP server — inspecting live desktop state (windows, accessibility tree) or firing synthetic input (type/click/key/drag) against real apps, including lease unlock and focus/ref recovery.
---

# Driving FlaUI.Mcp (live server)

Empirically grounded in live dogfooding. Use the **installed** MCP server's
`desktop_*` tools to see/act on the real desktop — never `Get-Process`. The tools are **DEFERRED**:
load them before use.

## Step 0 — load tools (one ToolSearch call)

```
ToolSearch "select:mcp__flaui-mcp__desktop_list_windows,mcp__flaui-mcp__desktop_open_window,mcp__flaui-mcp__desktop_snapshot,mcp__flaui-mcp__desktop_get_text,mcp__flaui-mcp__desktop_input_status"
```
Add per task: `desktop_type,desktop_key,desktop_click,desktop_drag,desktop_paste_text` (synthetic input);
`desktop_set_caret,desktop_select_text_range` (lease-exempt text); `desktop_focus_window,desktop_wait_for_foreground,desktop_window_transform` (foreground/recovery);
`desktop_find` (cheap targeting), `desktop_snapshot_diff` (change detection);
`desktop_wake_accessibility,desktop_release_accessibility,desktop_list_wakes` (opaque Chromium/Electron);
`desktop_find_text,desktop_wait_for_text` (OCR targeting); `desktop_user_state` (coarse presence — read-only, opt-in).

## Watching the agent

- **Intent overlay** — pass `overlay:true` (or `overlay:<milliseconds>` for custom duration, `0` disables)
  to any mutative tool to draw a red rect on the target element ~500 ms **before** the action fires —
  so a human watching sees what the agent is about to touch. It is a visibility aid, not a safety gate.
- **Audit trace** — when a tool resolves a `selector` targeting, the audit line now names the resolved
  element's stable identity: `RuntimeId`, `AutomationId`, `ClassName`, `ControlType`, and bounds
  (never `Name`/`Value`/content). Check the audit log to verify the agent touched the right element.

## Orientation (read-only, always safe)

1. `desktop_list_windows` — Title/ProcessName/Pid; exactly one `IsForeground:true`. Hang-proof.
   Pass **`includeHandles:true`** to get a reusable `wN` on each window inline — act/read (snapshot,
   find, interaction) **directly, skipping step 2's `desktop_open_window`**. Handles are minted lazily
   (the list stays pure-Win32/non-blocking) and reused across polls; the UIA binding happens on first
   use, guarded so a recycled HWND fails `WindowHandleStale` rather than acting on the wrong window.
2. `desktop_open_window` by `pid` or exact `title` → returns a handle `wN`.
3. `desktop_snapshot wN` → indented tree with `[eN] Role "Name" @{x,y,w,h}` refs. Read results
   from **Text** nodes; **refs follow tree order, not value** — read the label before acting on `eN`.

## Targeting without a full walk

- `desktop_find wN` queries a window for element refs by `automationId` / `name` (`eq`|`contains`) /
  `controlType` / `enabledOnly` (optional subtree `scope`) **without** walking the whole tree — the cheap
  way to grab one control's ref. Returns `matches[{ref,automationId,name,controlType,bounds,…}]` +
  `totalMatches`/`isTruncated` (narrow the query if truncated). No match ⇒ empty list (not an error).
  Refs are **additive** — a find does NOT invalidate a prior `desktop_snapshot`'s refs. Password fields
  return `name:"[REDACTED]"` and are not findable by name.
- `desktop_snapshot_diff wN <baselineSnapshotId> scope=<ref>` diffs only that element's subtree (cheap
  re-walk + in-memory baseline slice) — added/removed/changed since the baseline.

## Selector vs snapshot ref — pick by what you already know

- **Known control → `selector`, no snapshot needed.** Every state-changing interaction tool, the six
  ref-taking input tools, and the ref-taking content reads accept a `selector`
  (`{automationId?, name?, nameMatch?, controlType?, scope?, ignoreCase?}`) as an alternative to `ref`,
  resolved **fresh at the moment of the call** — no `eN` to go stale, no re-snapshot after every action.
  If you already know a stable `automationId` (from a prior snapshot or from app knowledge), skip the
  snapshot and act directly with `selector:{automationId:"..."}`.
- **Unknown control → `desktop_snapshot`/`desktop_find` first.** A selector still needs *something*
  material (`automationId`/`name`/`controlType`) to search for — if you don't know any of those yet,
  discover the control with a snapshot or `desktop_find` (which also lets you preview a name match with
  `ignoreCase:true` before committing to a selector).
- **Exactly one of `ref` | `selector`** — passing both or neither is `InvalidArguments`. (`desktop_key`
  is the one exception: at most one, omitting both targets the current foreground window.)
- **`ignoreCase` defaults true on `selector`** (ergonomic — `"Submit"` matches `"submit"`), **false on
  `desktop_find`** (back-compat). If a selector's name collides across a real `Submit`/`submit` pair,
  add `ignoreCase:false` to disambiguate.
- **Resolution is fail-closed, count==1 only:** 0 matches → `SelectorNoMatch` (target not present —
  reveal it first, or re-check the fields); >1 matches → `AmbiguousMatch` — refine by adding
  `controlType`/`automationId`, a `scope` (an `eN` narrowing to a subtree), `ignoreCase:false`, or just
  fall back to a `desktop_snapshot` ref for a control with no stable identity.
- **`resolvedElement` (the freshly minted `eN` returned on a successful selector call) is
  short-lived** — like any ref, it dies on the next re-walk/snapshot of that window. Reuse it only for
  an *immediate* follow-up, don't hold onto it across turns.

## React instead of poll (`desktop_watch`)

- Instead of looping `desktop_snapshot`/`desktop_find` to notice a change, subscribe once:
  `desktop_watch wN [window_opened,window_closed,focus_changed,structure_changed]` → returns a
  `subscriptionId`. Optional `scope=<ref>` narrows `structure_changed` to one subtree.
- **Get a handle first — lease-free.** `desktop_watch` needs a `wN` handle; if you don't have one,
  `desktop_open_window by:pid|title` attaches to an existing window read-only (**no lease**, unlike
  `desktop_launch_app`). All four watch tools (`desktop_watch`/`desktop_unwatch`/`desktop_list_watches`/
  `desktop_drain_events`) are ReadOnly + lease-exempt — you can watch, list, and drain while input is
  locked. Only *triggering* an event via your OWN `desktop_type`/`desktop_click`/launch needs a lease.
- **What actually fires (empirical):** `structure_changed` needs elements ADDED/REMOVED
  (a menu/dialog opening, a list rebuild) — pure text edits or a terminal printing output fire *text*
  changes, NOT structure, so watching those yields nothing. A **minimized** target has a root-only tree
  (`desktop_snapshot` shows one node, bounds `{0,0,0,0}`) and emits NO events — `desktop_window_transform
  wN restore` it first if you need to observe it. An empty `desktop_drain_events` (`count:0`) usually
  means "no qualifying event happened," not a failure.
- **In Claude Code, push notifications aren't surfaced to you** — poll
  `desktop_drain_events(subscriptionId)` instead to fetch the buffered events (push+drain: the
  server buffers every event server-side specifically because some hosts don't surface
  `notifications/flaui/desktop_event`). Each drained event is `{subscriptionId, event, window,
  ref?, controlType?, name?, bounds?, coalescedCount, timestampUtc}`.
- For detail on an event, `desktop_snapshot`/`desktop_get_text` its `ref` — but act promptly:
  event `ref`s are **ephemeral** (a small bounded pool), so an old one returns `REF_NOT_FOUND` once
  it ages out. Re-`desktop_snapshot` for a durable ref rather than treating that as an error.
- **Self-trigger warning:** your own `desktop_type`/`desktop_click`/`desktop_key` calls fire UIA
  events too — an event right after your own input is likely self-caused; correlate by timing
  before reacting to it as an external change.
- `droppedCount` on a `desktop_list_watches` entry (>0) means events were coalesced/buffer-dropped
  under load — re-snapshot to resync rather than trusting the stream as complete.

## Opaque apps: wake-first, find_text for the residual

- **Decision flow, cheapest first:** rich UIA (WinUI/WPF/WinForms/Qt) → `desktop_snapshot` directly.
  Opaque Chromium/Electron (`desktop_snapshot` returns one big empty node with `wakeable:true`) →
  `desktop_wake_accessibility wN` then re-`desktop_snapshot`/`desktop_find`/interact. Zero-accessibility
  (games, canvas, Citrix/RDP inners, or an editor's text body still gated even when woken) →
  `desktop_find_text` + `desktop_click_at` (read the content from the screenshot yourself — OCR here
  is targeting, not reading).
- **Wake is HELD, not one-shot.** `desktop_wake_accessibility wN` returns `{wakeId, window,
  alreadyAwake}` — idempotent (re-waking an already-awake window just returns the same `wakeId`), and
  it auto-releases when the window closes. Call `desktop_release_accessibility wakeId` when you're
  done with it, but don't expect the tree to collapse immediately: Chromium re-collapses it **lazily**
  once idle, not on release itself. `desktop_list_wakes` recovers your active wakes after a context
  loss. All three wake tools are ReadOnly + lease-exempt (no lease needed, even while input is locked).
  *(Verified live: VS Code snapshot went **14→131 nodes** on wake, exposing the
  full menu/activity-bar/status-bar tree; re-wake returned the same `wakeId` with `alreadyAwake:true`;
  release, then `desktop_list_wakes` empty, then a second release were all idempotent.)*
- **A wake doesn't always reach the document body.** Some editors gate their actual document text
  separately from the chrome UIA hydrates — if `desktop_snapshot` still shows an empty text body after
  waking, that's expected; fall through to `desktop_find_text` for that content instead of retrying
  the wake.
- **`desktop_find_text` is fuzzy by default** (OCR misreads UI text, e.g. `"Submit"` → `"5ubmit"`) and
  `all` defaults to true (every occurrence, not just the best). A fuzzy query can match **inside**
  unrelated body text (`"Click Submit below"` matching a query for `"submit"`) — always check a
  match's `text`/`bounds` (or just look at the screenshot) before `desktop_click_at`ing its
  `xPct`/`yPct`. `desktop_wait_for_text` polls (≥750ms between OCR passes) and returns
  `{satisfied:false}` on timeout — not an error. Both fail `OcrUnavailable` if no Windows OCR language
  pack is installed.
- **When a surface is both woken and OCR-able, the two coordinate systems agree** — `find_text`
  bounds land within a few px of the matching UIA element's bounds, and crisp large UI text OCRs at
  `confidence:1.0` (smoke: "Show All Commands" → `find_text` `[927,679,157,12]` vs UIA
  `@{926,673,160,23}`; menu "Selection" → OCR `[205,16,64,12]` vs UIA `[194,0,86,43]`, centers align).
  Useful as a sanity cross-check that OCR resolved the right thing before `desktop_click_at` — but the
  fuzzy-match caution above still holds for small/dense/anti-aliased text.

## Synthetic input needs a human lease

- Check first: `desktop_input_status` → `{leaseStatus, secondsRemaining, shells}`.
- **Pre-flight the target window state (the #1 driving trap):** before an input plan, run
  `desktop_list_windows includeBounds:true` — off-screen bounds like `@{-31992,…}` mean the target is
  **MINIMIZED**. A minimized (or otherwise non-foreground) target makes EVERY synthetic action abort
  `ElementDisappearedDuringAction` (the TOCTOU guard). Fix a minimized target: `desktop_window_transform
  wN restore` → re-`desktop_find`/snapshot (**refs change!**) → THEN type.
- **`desktop_focus_window` returns `{ok, foregroundGained}` — read the bool, don't assume.**
  `foregroundGained:false` means the focus did NOT actually land; two causes, **different fixes**:
  (a) target **minimized** (`includeBounds` shows off-screen `@{-31992,…}`) → `restore` it, re-snapshot, act;
  (b) target is a normal visible window but you're driving from a foreground terminal → the real
  **foreground-lock**: a background-process server genuinely cannot `SetForegroundWindow` an *existing*
  window past the active one. You can't focus your way out of (b) — instead **launch the target fresh**
  (`desktop_launch_app` grants the new window foreground) and act **immediately**, before it loses it.
  Empirically (console smoke): `desktop_focus_window` on an already-open charmap → `foregroundGained:false`,
  but a *freshly-launched* charmap was foreground and typed/minimized fine.
- **On a `targetNotForeground` result (or `desktop_focus_window` returning `foregroundGained:false`
  with a `recommendedAction`), call `desktop_wait_for_foreground(window)` — don't yield your turn.**
  `desktop_type`/`desktop_key` return this instead of aborting when the target isn't the OS foreground:
  `{ targetWindow, currentForeground:{handle, process}, recommendedAction:"call-wait-for-foreground",
  recovery }`. `desktop_wait_for_foreground` flashes the window then blocks (server-capped at 45s) until
  it gains foreground, is closed, or times out, returning `{foregroundGained, reason:"gained"|"timeout"|
  "window-destroyed", currentForeground}`. On `reason:"timeout"`, call it again immediately rather than
  ending your turn — it's designed to be re-invoked. It's lease-exempt (works even while input is
  locked) and single-waiter (one outstanding wait at a time). `currentForeground` is **leak-safe** —
  only the foreground process's name, never its window title (a `title` appears only for a modal owned
  by the exact target window you asked about). Clicks (`desktop_click`/`desktop_click_at`/`desktop_drag`)
  don't trigger this — a click activates the window itself, so it's a remedy, not a victim.
- Locked? A human runs on the host: `flaui-mcp unlock --minutes N` (suggest the user type `! flaui-mcp unlock --minutes N`).
- The lease **expires mid-session** (`InputNotLeased` when it lapses) → re-unlock.
- **Lease-exempt even while locked:** `desktop_set_caret`, `desktop_select_text_range`, `desktop_get_text`
  (UIA TextPattern, no OS input). `desktop_type`/`desktop_key`/click/drag fail closed `InputNotLeased`.
- **Terminals/shells need the extra `shells` capability.** Synthetic input into an interlocked shell
  sink (WindowsTerminal, conhost) fails `SinkInterlocked` on a plain unlock → re-grant with
  `flaui-mcp unlock --minutes N --allow-shells`. Reading (`desktop_get_text`) is **not** gated — only
  click/type/key into the terminal hit this. (Deliberate: injecting keystrokes into a live shell is high-risk.)

## Typing

- `desktop_type` is **paced by default** (`interKeyDelayMs=15`) so reactive editors (the new Win11
  Notepad's RichEdit/autocomplete) don't drop/garble fast input; pass `0` for a raw atomic blast.
- For a type **capability test**, prefer a **classic Win32 Edit** (Run dialog via `desktop_key "Win+R"`)
  over the new Notepad. Always verify with `desktop_get_text`.
- **`verify` (default true)** reads the element back and returns a soft `verify{ran,verified,mismatch}`;
  on garble → `mismatch:true` + `expected`/`actual` + `recommendedFallbackTool`. It only asserts an exact
  match when the field started **empty** — typing into a non-empty field (e.g. a Run box with MRU history)
  returns `verified:false, mismatch:false, reason:"field-not-empty"` (abstains — **not** a failure; clear the
  field first if you want a clean `verified:true`). `verify` never throws. `recommendedFallbackTool` now
  points to `desktop_set_value` (writable ValuePattern) or `desktop_paste_text` (no writable
  ValuePattern) instead of the old manual `desktop_clipboard_set`+`desktop_key "Ctrl+V"` two-step.
- **A reactive editor that garbles `desktop_type`** (new Win11 Notepad, Chromium `contenteditable`) →
  use `desktop_paste_text wN eN "text"` instead. It's an atomic clipboard-backed Ctrl+V: all input
  gates run before the clipboard is touched, so a refused paste never clobbers it. Clipboard restore
  is **best-effort** — it only restores your prior clipboard when the paste is confirmed to have
  landed; otherwise (including whenever you pass `verify=false`, or in editors that transform pasted
  text so the landing check can't confirm) it reports `clipboardRestored:"abandoned"` and leaves your
  pasted text on the clipboard. Non-text clipboard content (image/files) is refused
  (`ClipboardHoldsNonText`) unless `forceOverwriteClipboard=true`.

## Terminals & reading another agent's TUI

- WindowsTerminal exposes **only the active tab** to UIA. To read a background tab, switch to it
  (click its `TabItem` — needs the `shells` lease) then **re-snapshot**: the content `Text` ref
  **changes on every tab switch** (`RefNotFound` if you reuse the old one). The active-tab content
  node is `Custom → Text "PowerShell"`.
- **A modal TUI popup can't be read from the session driving flaui-mcp** — opening it (e.g. Claude
  Code `/usage`) suspends your own turn, so the popup only exists while you can't act. Drive a
  **second, idle** agent session instead: switch to its tab, `desktop_type` the command +
  `desktop_key Enter`, read the popup, then `Esc` to dismiss (leave it as found). The `/` slash-menu
  renders in-buffer — read back and confirm the command is highlighted before Enter.

## Gotchas & recovery

| Symptom | Cause | Fix |
|---|---|---|
| `ElementDisappearedDuringAction` on type/key | foreground changed / target not truly focused (TOCTOU guard) — most often the target is **MINIMIZED** (esp. if `focus_window` returned `ok:true` yet `list_windows` still shows another window `IsForeground`) | Check `desktop_list_windows includeBounds:true`; if off-screen (`@{-31992,…}`) → `desktop_window_transform wN restore` → **re-snapshot (refs change!)** → retry. Otherwise `desktop_focus_window wN` → re-snapshot → retry ONCE (don't retry-loop). |
| Window at `@{-31992,…}`, focus won't take | window is **minimized** (won't come forward) | `desktop_window_transform wN restore` → **re-snapshot (refs change!)** → act |
| Keys go nowhere after closing/minimizing a window | the foreground window collapsed and orphaned keyboard focus | The server's **own** `desktop_close_window` and `desktop_window_transform minimize` **auto-restore** foreground to the next visible top-level window — so those won't orphan the keyboard. Only an *app-internal* dialog dismissed via synthetic `Esc`/`Enter` (not a `desktop_close_window`) can still orphan it → `desktop_focus_window` (check `foregroundGained`) before the next key. Best-effort under the foreground-lock. |
| `desktop_launch_app` LaunchTimeout ("started but showed no titled window") | UWP/Store stub hands the window to ApplicationFrameHost, **or** a slow-showing app (the new Win11 Notepad) paints its titled window *after* the 10s timeout | recover via `desktop_list_windows` → `desktop_open_window by:title` — the window is usually there a moment later (bump `timeoutMs` to avoid the miss next time) |
| `InputDesktopUnavailable` | session locked/disconnected (RDP dropped) | reconnect + unlock the session |
| `REF_STALE_UNRESOLVABLE` / `AMBIGUOUS_MATCH` on invoke/click/type/set_value | held ref's exact element (RuntimeId) is gone or duplicated — **state-changing** tools resolve refs **strictly**: no silent retarget to a recycled `AutomationId` under virtualization | re-`desktop_snapshot` or `desktop_find` to mint a fresh ref, then act (reads stay lenient). Break-glass: env `FLAUI_MCP_REF_STRICT=off` disables the guard globally |
| `AMBIGUOUS_MATCH` even on a **read** (`desktop_get_text`) | reads are lenient about *recycling* but still fail closed when the ref's identity (`AutomationId`, else Name+type) matches **several live siblings** — the new Notepad shares `AutomationId "ContentTextBlock"` across 6 status texts | pick a **structurally unique** ref (e.g. the `Document`/root node, or one with a distinct Name) or re-snapshot for a more specific one |
| `REF_NOT_FOUND` on a ref you "just had", right after a window closed | closing a window (or its process exiting) **evicts that window's refs**; windows closed by hand are caught by an on-access liveness sweep at the next `snapshot`/`find`/`list_windows` | expected, not a bug — take a fresh `desktop_snapshot`; **never reuse a ref across a window close/reopen** (the tell is `REF_NOT_FOUND`, distinct from `WindowHandleStale` on the handle) |
| Typed text garbled / `verify.mismatch:true` | reactive/RichEdit editor races synthetic keystrokes | Mismatch result includes `canSetValue` (writable ValuePattern presence). **`canSetValue:true`** (snapshot shows `[Value,…]`, e.g. new Notepad Document): `recommendedFallbackTool:"desktop_set_value"` — byte-exact. **`canSetValue:false`** (Electron `contenteditable`, no ValuePattern): `recommendedFallbackTool:"desktop_paste_text"` — `set_value` would return `PatternUnsupported`, so use `desktop_paste_text` (atomic clipboard-backed Ctrl+V; clipboard restore is best-effort, `clipboardRestored:"abandoned"` if the landing can't be confirmed). (`desktop_type`'s `verify` flags the garble automatically.) |
| Snapshot is one opaque `Document` node, no children (often `wakeable:true`) | Electron/Chromium a11y **off by default** | `desktop_wake_accessibility wN` then re-`desktop_snapshot` — usually hydrates the full tree. Still empty (or a document text body specifically)? Use `desktop_find_text` (OCR) or the **coordinate path** (`desktop_click_at`/`desktop_drag` by `xPct`/`yPct`). Per-app fix: relaunch it with `--force-renderer-accessibility`. WinUI/WPF/Qt expose proper UIA and are fine. |
| `desktop_type`/`desktop_key` returns `targetNotForeground` (no error thrown) | target window isn't the OS foreground (foreground-lock) — the tool flashed it instead of typing blind | Call `desktop_wait_for_foreground(window)` (don't yield your turn); re-invoke on `reason:"timeout"` (server caps each call at 45s) |

## Combining presence with foreground (watching/working/nearby/away)

- `desktop_user_state` (read-only, lease-exempt) reports a coarse **activity** axis only:
  `{ enabled, activity: "active"|"nearby"|"away"|null }`. Off by default — a human must run
  `flaui-mcp presence on` before it returns anything but `{enabled:false, activity:null}`. It never
  exposes raw idle milliseconds; there is no finer signal to poll for.
- Cross it with SP-A's **focus** axis (`desktop_focus_window`'s `foregroundGained`, or whether your
  target window is the one reported by `desktop_list_windows`' `IsForeground:true`) to derive a
  richer state yourself — the server does not compute this for you:
  - **watching** — your target is the OS foreground *and* `activity:"active"`.
  - **working** — `activity:"active"` but your target is *not* the foreground (human is doing
    something else).
  - **nearby** — `activity:"nearby"` (idle past the short threshold, default 60s).
  - **away** — `activity:"away"` (idle past the long threshold, default 300s).
- Use the derived state to decide how hard to escalate attention: the intent-overlay flash is
  always-on regardless; add `autosound` (spoken cue) when nearby/away; if you need to reach someone
  who's away, that's a job for **your own** notification MCP — this server does no outbound
  signaling of any kind (dumb sensor by design).
- Presence is human-only and off by default, same posture as `overlay`/`autosound` — don't assume
  it's enabled; check `enabled` in the reply before trusting `activity`.
- **Empirical (live smoke):** `desktop_user_state` reads the REAL OS idle clock (`GetLastInputInfo`),
  and your OWN synthetic input resets it — a `desktop_type`/`desktop_click`/`desktop_key`/mouse move
  bumps idle straight back to `active`. So you can't observe `nearby`/`away` *while* injecting input; the
  read itself is lease-exempt and does NOT reset the clock, so poll it while going input-silent. (To smoke
  the transitions fast, set tiny thresholds: `flaui-mcp presence on --nearby-secs 5 --away-secs 15`.) Any
  flash + spoken cue you hear during a foreground wait is `desktop_wait_for_foreground` firing the
  attention signal on a non-foreground target (autosound speaks via a child process — reliable even on a
  stdio host where in-process System.Speech throws).

## Etiquette

Leave apps as found (clear text you added; `Ctrl+A`→`Delete`). Close dialogs with **Esc**, never
Enter/OK (don't execute). Prefer disposable apps (Calculator, Run dialog) for demos.

Deeper field notes live in the project memory `project-flaui-mcp-driving-notes`.
