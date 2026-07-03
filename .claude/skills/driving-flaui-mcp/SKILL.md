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
Add per task: `desktop_type,desktop_key,desktop_click,desktop_drag` (synthetic input);
`desktop_set_caret,desktop_select_text_range` (lease-exempt text); `desktop_focus_window,desktop_window_transform` (recovery);
`desktop_find` (cheap targeting), `desktop_snapshot_diff` (change detection).

## Orientation (read-only, always safe)

1. `desktop_list_windows` — Title/ProcessName/Pid; exactly one `IsForeground:true`. Hang-proof.
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

## Synthetic input needs a human lease

- Check first: `desktop_input_status` → `{leaseStatus, secondsRemaining, shells}`.
- **Pre-flight the target window state (the #1 driving trap):** before an input plan, run
  `desktop_list_windows includeBounds:true` — off-screen bounds like `@{-31992,…}` mean the target is
  **MINIMIZED**. A minimized (or otherwise non-foreground) target makes EVERY synthetic action abort
  `ElementDisappearedDuringAction` (the TOCTOU guard). Fix: `desktop_window_transform wN restore` →
  re-`desktop_find`/snapshot (**refs change!**) → THEN type. Tell: if `desktop_focus_window wN` returns
  `ok:true` but `desktop_list_windows` STILL shows another window `IsForeground:true`, the target is
  almost certainly minimized — restore it. Do NOT theorize a "background-process foreground-lock" and
  retry the keystroke; check window state instead (a background process genuinely can't `SetForegroundWindow`
  past the active driving terminal, but that is NOT what an abort-after-`focus:ok` is telling you).
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
  field first if you want a clean `verified:true`). `verify` never throws.

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
| Keys go nowhere after closing a dialog | prior window lost foreground | `desktop_focus_window` before the next key |
| `desktop_launch_app` LaunchTimeout ("started but showed no titled window") | UWP/Store stub hands the window to ApplicationFrameHost, **or** a slow-showing app (the new Win11 Notepad) paints its titled window *after* the 10s timeout | recover via `desktop_list_windows` → `desktop_open_window by:title` — the window is usually there a moment later (bump `timeoutMs` to avoid the miss next time) |
| `InputDesktopUnavailable` | session locked/disconnected (RDP dropped) | reconnect + unlock the session |
| `REF_STALE_UNRESOLVABLE` / `AMBIGUOUS_MATCH` on invoke/click/type/set_value | held ref's exact element (RuntimeId) is gone or duplicated — **state-changing** tools resolve refs **strictly**: no silent retarget to a recycled `AutomationId` under virtualization | re-`desktop_snapshot` or `desktop_find` to mint a fresh ref, then act (reads stay lenient). Break-glass: env `FLAUI_MCP_REF_STRICT=off` disables the guard globally |
| `AMBIGUOUS_MATCH` even on a **read** (`desktop_get_text`) | reads are lenient about *recycling* but still fail closed when the ref's identity (`AutomationId`, else Name+type) matches **several live siblings** — the new Notepad shares `AutomationId "ContentTextBlock"` across 6 status texts | pick a **structurally unique** ref (e.g. the `Document`/root node, or one with a distinct Name) or re-snapshot for a more specific one |
| `REF_NOT_FOUND` on a ref you "just had", right after a window closed | closing a window (or its process exiting) **evicts that window's refs**; windows closed by hand are caught by an on-access liveness sweep at the next `snapshot`/`find`/`list_windows` | expected, not a bug — take a fresh `desktop_snapshot`; **never reuse a ref across a window close/reopen** (the tell is `REF_NOT_FOUND`, distinct from `WindowHandleStale` on the handle) |
| Typed text garbled / `verify.mismatch:true` | reactive/RichEdit editor races synthetic keystrokes | Mismatch result includes `canSetValue` (writable ValuePattern presence). **`canSetValue:true`** (snapshot shows `[Value,…]`, e.g. new Notepad Document): `recommendedFallbackTool:"desktop_set_value"` — byte-exact. **`canSetValue:false`** (Electron `contenteditable`, no ValuePattern): `recommendedFallbackTool:"desktop_clipboard_set"` — `set_value` would return `PatternUnsupported`, so use the **clipboard paste** path (`desktop_clipboard_set` → focus → `desktop_key "Ctrl+V"`) — lands atomically, doesn't race. (`desktop_type`'s `verify` flags the garble automatically.) |
| Snapshot is one opaque `Document` node, no children | Electron/Chromium a11y **off by default** | No refs to target — use the **coordinate path** (`desktop_click_at`/`desktop_drag` by `xPct`/`yPct`) or vision. Per-app fix: relaunch it with `--force-renderer-accessibility`. WinUI/WPF/Qt expose proper UIA and are fine. |

## Etiquette

Leave apps as found (clear text you added; `Ctrl+A`→`Delete`). Close dialogs with **Esc**, never
Enter/OK (don't execute). Prefer disposable apps (Calculator, Run dialog) for demos.

Deeper field notes live in the project memory `project-flaui-mcp-driving-notes`.
