---
name: driving-flaui-mcp
description: Use when driving or dogfooding this project's installed FlaUI.Mcp desktop-automation MCP server — inspecting live desktop state (windows, accessibility tree) or firing synthetic input (type/click/key/drag) against real apps, including lease unlock and focus/ref recovery.
---

# Driving FlaUI.Mcp (live server)

Empirically validated by live dogfooding (through v0.7.2). Use the **installed** MCP server's
`desktop_*` tools to see/act on the real desktop — never `Get-Process`. The tools are **DEFERRED**:
load them before use.

## Step 0 — load tools (one ToolSearch call)

```
ToolSearch "select:mcp__flaui-mcp__desktop_list_windows,mcp__flaui-mcp__desktop_open_window,mcp__flaui-mcp__desktop_snapshot,mcp__flaui-mcp__desktop_get_text,mcp__flaui-mcp__desktop_input_status"
```
Add per task: `desktop_type,desktop_key,desktop_click,desktop_drag` (synthetic input);
`desktop_set_caret,desktop_select_text_range` (lease-exempt text); `desktop_focus_window,desktop_window_transform` (recovery).

## Orientation (read-only, always safe)

1. `desktop_list_windows` — Title/ProcessName/Pid; exactly one `IsForeground:true`. Hang-proof.
2. `desktop_open_window` by `pid` or exact `title` → returns a handle `wN`.
3. `desktop_snapshot wN` → indented tree with `[eN] Role "Name" @{x,y,w,h}` refs. Read results
   from **Text** nodes; **refs follow tree order, not value** — read the label before acting on `eN`.

## Synthetic input needs a human lease

- Check first: `desktop_input_status` → `{leaseStatus, secondsRemaining, shells}`.
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
- **`verify` (default true, v0.7.2)** reads the element back and returns a soft `verify{ran,verified,mismatch}`;
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
| `ElementDisappearedDuringAction` on type/key | foreground changed / target not truly focused (TOCTOU guard) | `desktop_focus_window wN` → re-snapshot → retry |
| Window at `@{-31992,…}`, focus won't take | window is **minimized** (won't come forward) | `desktop_window_transform wN restore` → **re-snapshot (refs change!)** → act |
| Keys go nowhere after closing a dialog | prior window lost foreground | `desktop_focus_window` before the next key |
| `desktop_launch_app` LaunchTimeout (UWP/Store app) | stub launcher hands window to ApplicationFrameHost | recover via `desktop_list_windows` → `desktop_open_window by:title` |
| `InputDesktopUnavailable` | session locked/disconnected (RDP dropped) | reconnect + unlock the session |
| Typed text garbled / `verify.mismatch:true` | reactive/RichEdit editor races synthetic keystrokes | **Has `ValuePattern`** (snapshot shows `[Value,…]`, e.g. new Notepad Document): `desktop_set_value` — byte-exact. **No `ValuePattern`** (Electron `contenteditable`): `set_value` returns `PatternUnsupported` → **clipboard paste** (`desktop_clipboard_set` → focus → `desktop_key "Ctrl+V"`) — lands atomically, doesn't race. (`desktop_type`'s `verify` flags the garble automatically.) |
| Snapshot is one opaque `Document` node, no children | Electron/Chromium a11y **off by default** | No refs to target — use the **coordinate path** (`desktop_click_at`/`desktop_drag` by `xPct`/`yPct`) or vision. Per-app fix: relaunch it with `--force-renderer-accessibility`. WinUI/WPF/Qt expose proper UIA and are fine. |

## Etiquette

Leave apps as found (clear text you added; `Ctrl+A`→`Delete`). Close dialogs with **Esc**, never
Enter/OK (don't execute). Prefer disposable apps (Calculator, Run dialog) for demos.

Deeper field notes live in the project memory `project-flaui-mcp-driving-notes`.
