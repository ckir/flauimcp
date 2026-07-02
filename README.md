# FlaUI.Mcp

[![CI](https://github.com/ckir/flauimcp/actions/workflows/ci.yml/badge.svg)](https://github.com/ckir/flauimcp/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/ckir/flauimcp?sort=semver)](https://github.com/ckir/flauimcp/releases/latest)
[![License: PolyForm NC 1.0.0](https://img.shields.io/badge/license-PolyForm%20NC%201.0.0-blue)](LICENSE)
![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server that lets an AI
agent — Claude Code, Antigravity (agy), or any MCP client — **control the Windows desktop**:
enumerate windows, launch applications, focus/close windows, **snapshot a window's UI into
a ref-tagged accessibility tree**, **screenshot windows or the desktop, read element bounds,
diff/stat snapshots, and wait for UI conditions**, **act on elements through UI Automation
patterns** (click, set value, toggle, expand, select, scroll, focus, window min/max), and
**read and write structured content** (grid/table cells, element text via TextPattern, clipboard)
— and **drive synthetic mouse/keyboard input** (`SendInput`-backed type/click/drag/key, plus
`TextPattern` caret/selection), all gated behind a safety foundation (time-lease,
deny-list, per-window budget, audit, elevation guard). Think "Playwright, but for native Windows
apps."

> **This README describes the current state of the project.** For the per-version feature
> history — what landed when — see [`CHANGELOG.md`](CHANGELOG.md).

---

<a id="warning"></a>

## ⚠️ WARNING / DISCLAIMER — READ BEFORE INSTALLING

**Installing this software gives an AI agent the ability to operate your computer as if it
were sitting at the keyboard.** That is the entire point of the tool, and it is also the
entire risk. Understand the following before you install:

- **It runs with *your* user privileges.** Anything you can do without an admin prompt, the
  agent can do: launch programs, close windows (losing unsaved work), interact with your
  files, browser, email, and any open application. There is no built-in sandbox.
- **An agent can launch arbitrary executables.** The current tool surface already includes
  "launch this program with these arguments." A confused, jailbroken, or
  prompt-injected agent could start unwanted software. Treat the agent's permission to use
  this server as equivalent to giving it a shell.
- **Prompt injection is a real attack vector.** If the agent reads attacker-controlled
  content (a web page, a document, an email) that content can attempt to instruct the agent
  to take desktop actions. The blast radius of a successful injection is your whole desktop
  session.
- **Synthetic input is live.** The `SendInput`-backed mouse/keyboard tools are gated behind the
  time-lease, deny-list, per-window budget, audit, and elevation guard of the safety foundation.
  A human must explicitly unlock a time-bounded window (`flaui-mcp unlock`)
  before any input fires. The permission you grant at install (`mcp(flaui-mcp/*)`) covers these
  tools.
- **The released binaries are NOT code-signed.** Windows SmartScreen and antivirus software
  will likely flag the installer and the self-extracting executable. You will have to click
  through "More info → Run anyway." Verify the published SHA-256 checksums before trusting a
  download. (Code signing is a planned v2 improvement.)
- **It cannot drive elevated apps** (UIPI): a normally-launched server cannot send input to
  applications running as Administrator. This is a Windows security boundary, not a bug.
- **No warranty.** Per the license, the software is provided "as is," with no warranty and no
  liability for any damage arising from its use.

**Recommended use:** run it against a machine, VM, or user account where you are comfortable
with an automated agent taking actions on your behalf, and where you can supervise it. Do not
install it on a machine holding data you cannot afford to have an agent touch.

By installing, you accept these risks.

---

## What it does

FlaUI.Mcp is a stdio MCP server built on [FlaUI](https://github.com/FlaUI/FlaUI) (UI
Automation / UIA3) and the official MCP C# SDK. It exposes Windows desktop control as MCP
tools an agent can call.

**Current tools (window management + perception):**

| Tool | Read-only | Description |
| --- | --- | --- |
| `DesktopListWindows` | ✅ | List top-level windows with title, process name, and PID. Opt-in `includeBounds` adds absolute physical-pixel `Bounds` + `ZOrder` (0 = topmost) for occlusion reasoning. |
| `DesktopOpenWindow` | ✅ | Open a window by `pid` or `title`, returning a handle (e.g. `w1`). |
| `DesktopSnapshot` | ✅ | Walk a window's UI into an indented, ref-tagged accessibility-tree snapshot. Each line carries an `eN` ref, control type, name, bounds, state (incl. `focused`), and supported patterns. Options: `interactiveOnly` (prune noise, default on), `fullProperties` (add AutomationId/HelpText), `includeOffscreen` (default off), `maxDepth`, and `root` (root the walk at a prior ref). |
| `DesktopLaunchApp` | — | Launch an executable (with optional args) and return a handle to its main window. |
| `DesktopFocusWindow` | — | Bring a window to the foreground. |
| `DesktopCloseWindow` | — | Close a window and free its handle. |

**Perception-completion tools (read-only):** screenshot, bounds, snapshot
stats/diff, focus, and wait conditions. All are `readOnlyHint`/non-destructive.

| Tool | Description |
| --- | --- |
| `DesktopScreenshot` | Capture a window, an element (`window` + `ref`), or the full virtual desktop as a PNG (returned as a native MCP image block + `{bounds, dpiScale, scaleApplied, redactions}` metadata). Password fields are redacted at capture time; width is clamped to 1920. Focus the window first (no occlusion handling). |
| `DesktopGetBounds` | Absolute physical-pixel screen bounds `{x,y,w,h}` (signed, multi-monitor safe) of an element, plus its monitor `dpiScale` and `isOffscreen`. |
| `DesktopSnapshotStats` | Cheap orientation: control counts (total / interactive / offscreen / redacted) + a per-control-type histogram, without the full tree. Takes a `window` or a prior `snapshotId`. |
| `DesktopSnapshotDiff` | Diff a window's current tree against an explicit baseline `snapshotId` → `added` / `removed` / `changed` (Name/Enabled/Focused), keyed by a composite identity. Optional `scope=<ref>` re-roots the diff at a subtree instead of the whole window (slices the cached baseline in-memory). |
| `DesktopWaitFor` | Poll until a selector condition holds (`until` = exists \| enabled \| gone \| valueEquals). A timeout returns `{satisfied:false}` data, not an error; on success returns the matched ref + a fresh snapshotId. |
| `DesktopWaitForStable` | Poll until a window (or a scoped subtree) stops structurally changing; `includeText` also waits on text/Name settling. Timeout returns `{stable:false}`. |
| `DesktopGetFocusedElement` | O(1) "where am I": the UIA-focused element's ref + descriptor + owning window (handle/title/pid). The ref is scoped to that window so you can act on it. |
| `DesktopFind` | Query a window for element refs by `automationId` / `name` (`nameMatch` = eq \| contains) / `controlType` / `enabledOnly`, optional subtree `scope`, without walking the whole tree. Returns `matches` (ref, automationId, name, controlType, bounds, isOffscreen, isEnabled, hasFocus) plus `totalMatches`/`isTruncated` (capped by `max`, default 20). Honors the perception deny-list and password redaction — a password field's `name` is `[REDACTED]` and not matchable by name. Refs are additive: a find does not supersede snapshot refs. |

Read-only tools are annotated as such so MCP clients can auto-approve them while still
prompting for the mutating ones. Every tool returns structured JSON. Errors come back as a
uniform envelope (`{ error, message, suggestedRecovery }`) so the agent can recover rather than
crash the session.

**Interaction tools (pattern-based):** act on an element by its snapshot `ref`
through UI Automation control patterns. These are **not** synthetic mouse/keyboard input (that
is a later phase) — they drive the app's own automation providers, so they work over RDP and
never move the real cursor. All are state-changing (annotated `destructive`).

| Tool | Description |
| --- | --- |
| `DesktopInvoke` | Activate an element (e.g. click a button) via InvokePattern. |
| `DesktopSetValue` | Set a control's text/value via ValuePattern (focuses first). |
| `DesktopToggle` | Toggle a checkbox/switch via TogglePattern. |
| `DesktopExpand` | Expand/collapse a tree node, expander, or combo via ExpandCollapsePattern. |
| `DesktopSelect` | Select a list item / radio / tab via SelectionItemPattern. |
| `DesktopScroll` | Scroll a container (`direction` = up/down/left/right, `amount` = 1–50 steps) via ScrollPattern. |
| `DesktopScrollIntoView` | Realize a specific item in a scrollable container via ScrollItemPattern. |
| `DesktopSetFocus` | Set keyboard focus to an element via UIA Focus (reveals lazy-loaded content). |
| `DesktopWindowTransform` | Maximize / minimize / restore a window via the Window pattern. |

An action that opens a modal returns `ActionBlockedPending` instead of hanging the server —
snapshot the window to see the dialog, then act on it.

**Structured content & clipboard tools:** read structured data from grid/table
elements and interact with the system clipboard. Read-only tools are `readOnlyHint`; mutating
tools are `destructive` and blocked in `--read-only-mode`.

| Tool | Description |
| --- | --- |
| `DesktopGetGridCell` | ✅ Read-only. Read one grid/table cell by 0-based `(row, col)`. Returns `{value, controlType, automationId, isPassword}`. Password cells are masked as `[REDACTED]`/`isPassword:true`; credential-store windows are denied outright (`TargetDenied`). `GridCellOutOfRange` if out of bounds; `PatternUnsupported` if the element is not a grid. |
| `DesktopGetText` | ✅ Read-only. Read an element's text via UIA TextPattern. `selectionOnly` reads only the current selection; `maxLength` caps output (default 10 000 chars, returns `truncated:true` if hit). Password fields return `[REDACTED]`/`isPassword:true`. Off-screen targets are readable. `PatternUnsupported` if no TextPattern. |
| `DesktopGridSelect` | Select a grid/table cell by `(row, col)` via UIA SelectionItemPattern. Off-screen cells return `ElementNotActionable` — scroll into view first. Blocked in `--read-only-mode`. |
| `DesktopClipboardGet` | ✅ Read-only. Read the system clipboard as plain text (CF_UNICODETEXT). ⚠ **Clipboard exfil risk:** the clipboard may contain passwords or tokens the user recently copied — no redaction is possible at the clipboard layer. Returns `ClipboardUnavailable` when the clipboard is locked or holds non-text content. |
| `DesktopClipboardSet` | Write text to the system clipboard. Blocked in `--read-only-mode`. |

`DesktopGetGridCell` and `DesktopGetText` honor the same credential-store denylist and
`IsPassword` redaction as `DesktopSnapshot` — password cells and fields always surface as
`[REDACTED]`, and windows owned by known credential stores are denied outright.

**Input safety foundation.** The safety infrastructure and seam interfaces landed *before* any
synthetic input tool could — deliberately *before* the blast radius. No mouse/keyboard tool lives
in this layer; the `SendInput`-backed tools (`desktop_type`, `desktop_click`, `desktop_key`, etc.)
build on it — see **Synthetic input** below.

What this layer introduces:

- **Three seam interfaces** — `ISyntheticInput`, `IPlatformEnvironment`, `ILeaseProvider` —
  injectable, testable boundaries isolating all future input work from the rest of the server.
- **InputGuard pipeline** — deny-list, per-window action budget, and an event-only audit log.
  Synthetic input into UAC / `consent.exe`, credential dialogs, and high-risk sinks (terminal,
  browser address bar, Win+R run dialog) is refused outright; other windows consume from a
  configurable per-window budget.
- **Time-lease (dead-man's switch)** — synthetic input is hard-refused by default. A human grants
  a time-bounded window via `flaui-mcp unlock --minutes N [--allow-shells]` run out-of-band; the
  lease expires automatically when the time runs out, or immediately on `flaui-mcp lock`. The agent
  cannot grant or extend its own lease.
- **Elevation hard-fail** — if the server starts with Administrator rights, synthetic input is
  refused unless `--unsafe-allow-elevation` is passed explicitly at launch.

### Synthetic input

Real `SendInput`-backed mouse/keyboard input, built on the safety foundation above.
**Eight tools** ship:

- **`desktop_type`** — type Unicode text into the focused element (or a `@ref` target). Capped at
  4096 characters per call (`InvalidArguments` over the cap; split on a surrogate-safe boundary).
  Keystrokes are **paced by default** (`interKeyDelayMs=15`) so slow/async consumers
  keep up with fast input; the foreground is re-verified before *each* key, so a mid-type focus-steal
  still aborts. Pass `interKeyDelayMs=0` for a single atomic blast. **Note:** pacing does **not** cure
  the Windows 11 Notepad autocomplete garble (it corrupts synthetic input at any pacing); for
  reactive/autocomplete editors prefer a non-keystroke path (`desktop_set_value`, or clipboard paste
  for editors without `ValuePattern`). The garble is now flagged automatically by `desktop_type`'s
  `verify` (below).
  
  `desktop_type` takes an optional `verify` (bool, default `true`). When on, it reads the element back after typing and returns a `verify` object:
  - `{ ran, verified, mismatch }` — always present.
  - On a clean match: `verified:true`.
  - On a mismatch: `mismatch:true` with `expected`, `actual` (both truncated to 256 chars), a stable `recommendedFallbackTool:"desktop_set_value"`, and a human-readable `remedy`.
  - When it can't assert (empty-field precondition not met, no readable TextPattern, read failed, or a password/redacted field): `verified:false, mismatch:false, reason:"…"`.
  - `reason` is an **open** string — treat unknown values as forward-compatible; branch machines on `recommendedFallbackTool`, never parse `remedy`.
  
  Mismatch is **advisory** — `ok` stays `true` and nothing is retried or corrected. **For reactive / RichEdit editors (the new Win11 Notepad), prefer `desktop_set_value` (UIA ValuePattern)** for reliable text entry; `SendInput` can garble those editors at any pacing. Pass `verify=false` for the old fire-and-forget speed (skips a ~100 ms settle + two reads).
- **`desktop_key`** — send a key chord (e.g. `ctrl+a`, `enter`, `alt+f4`) to the focused window, or
  to a `@ref`/`window` target. `ref` without `window` is `InvalidArguments`.
- **`desktop_click`** — click a `@ref` element by its hit-test point.
- **`desktop_click_at`** — click an absolute window-relative point (`xPct`/`yPct`).
- **`desktop_drag`** — press-move-release between two points; **both** endpoints are deny-list
  checked and the end point is re-hit-tested before the button releases.
- **`desktop_input_status`** *(read-only)* — report the current lease state
  (`active`/`locked`, seconds remaining, whether the `shells` capability is held). Never exposes
  the SID or any payload.
- **`desktop_set_caret`** / **`desktop_select_text_range`** — move the caret / select a character
  span via UIA `TextPattern`. These do **not** use `SendInput`, so they are **exempt from the
  lease, session-active, and budget gates** — but the deny-list and terminal/console interlock
  still apply (you cannot drive a credential dialog or a shell this way without the `shells`
  capability).

**The out-of-band lease is the required enabler.** None of the five `SendInput` tools fire
unless a human has granted a live lease out-of-band:

```bash
flaui-mcp unlock --minutes 5 [--allow-shells]   # grant a time-bounded synthetic-input lease
flaui-mcp lock                                   # revoke it immediately
```

The agent cannot grant, extend, or read the SID of its own lease; it expires automatically. Use
`desktop_input_status` to check how much time remains.

**The session must stay active and unlocked.** `SendInput` cannot reach a locked or
RDP-disconnected desktop — those calls return `InputDesktopUnavailable` rather than silently
dropping keystrokes. Keep the RDP/console session connected and the workstation unlocked while an
agent drives input.

**Elevation is still hard-refused** unless `--unsafe-allow-elevation` is passed at launch.

**Honest boundary:** the lease and deny-list defend against the agent driving *high-risk* sinks,
not against a determined same-user host shell — anything running as your user can already act as
your user. This is a guardrail for an agent, not a sandbox.

#### Ref resolution: safe by default

A ref (`e23`) captured from a snapshot is re-resolved when you act on it — **strict on state-changing
tools**, **lenient on reads**, and it never silently binds a different control than the ref pointed at:

- **State-changing tools** (`desktop_invoke`, `desktop_set_value`, `desktop_toggle`, `desktop_expand`,
  `desktop_select`, `desktop_set_focus`, `desktop_scroll`, `desktop_scroll_into_view`, `desktop_type`,
  `desktop_key`, `desktop_click`, `desktop_set_caret`, `desktop_select_text_range`) require the **exact
  element** (matched by UIA RuntimeId). If it was destroyed and recreated (e.g. a virtualized row
  recycled its AutomationId), the action is **refused** with `REF_STALE_UNRESOLVABLE` — never
  retargeted. Take a fresh `desktop_snapshot`. (Legacy Win32 apps with no stable UIA identity: use
  `desktop_click_at`.)
- **Read tools** (`desktop_get_text`, `desktop_get_grid_cell`, snapshots) re-bind within the element's
  original container. `AMBIGUOUS_MATCH` on a duplicate AutomationId/Name (or duplicated ancestor
  container); `REF_STALE_UNRESOLVABLE` if the container is gone or identity can't be re-verified — it
  never guesses. Re-snapshot and pick a specific ref.

**Operator override:** set `FLAUI_MCP_REF_STRICT=off` (read at startup) to force lenient resolution
globally as a break-glass for apps with too-volatile UIA identity — this **disables the INV-8 guard**,
so use it only when strict resolution blocks a legitimate workflow. `FLAUI_MCP_REF_MAXSCOPES` (default
512) tunes the ancestor fan-out cap.

### Read-only mode

Start the server with **`--read-only-mode`** to refuse every state-changing tool — all the
interaction tools above, `DesktopGridSelect`, `DesktopClipboardSet`, plus launch/focus/close. They short-circuit to `WriteBlockedReadOnly`
without touching the desktop, while perception and enumeration keep working. Use it for an agent
that may *see* the desktop but not *act* on it.

### Perception safeguards (built in)

`DesktopSnapshot` and `DesktopScreenshot` read UI into the agent's context, so they ship with
privacy and safety floors — defense in depth, not a substitute for supervising the agent:

- **Credential stores are blocked.** Snapshotting a window owned by a known password manager
  (1Password, Bitwarden, KeePass, and similar) is rejected outright (`TargetDenied`).
- **Password fields are always redacted — in the tree *and* in pixels.** Any UIA password field
  renders as `[REDACTED]` in a snapshot; in a screenshot it is painted over with an opaque black
  rectangle at capture time (covering popups/menus too), so typed secrets leak through neither
  channel. A full-desktop screenshot is *refused* (`TargetDenied`) if a credential-store window is
  visible — capture a specific window instead.
- **Structured content tools inherit the same protections.** `DesktopGetGridCell` and
  `DesktopGetText` apply the credential-store denylist and always mask `IsPassword` fields as
  `[REDACTED]`. The clipboard layer (`DesktopClipboardGet`) cannot redact — see the exfil caveat
  in the tool table above.
- **Screenshots detect a dead session.** If the desktop is locked or RDP-disconnected (so the
  framebuffer would be black), `DesktopScreenshot` returns `CaptureUnavailable` rather than a black
  image.
- **Off-screen elements are culled by default.** A snapshot reflects what the user can see; pass
  `includeOffscreen` to reach scrolled-off-but-real elements.
- **Never run elevated.** The server warns (on stderr) if started with Administrator rights — it
  is meant to run at your user integrity level.

**On the roadmap** (see [`ROADMAP.md`](ROADMAP.md)): a "driving FlaUI.Mcp" dogfood skill,
occlusion-aware capture (PrintWindow), AOT/trim to shrink the self-contained executable, and an
HTTP transport.

### Electron / Chromium & other custom-render apps

Not every app exposes a clean accessibility tree. Honestly:

- **Electron / Chromium (VS Code, Slack, Discord, Teams, …):** Chromium keeps its accessibility
  tree **off by default**, so a snapshot is usually **one opaque `Document` node with no children**.
  When you see that, don't hunt for inner refs — fall back to the **coordinate path**
  (`desktop_click_at` / `desktop_drag` by `xPct`/`yPct`) or vision. Typed text into Chromium editors
  (Monaco, CodeMirror, `contenteditable`) can **garble** like the new Notepad; `desktop_type`'s
  `verify` flags it, but `desktop_set_value` often **isn't available** there (no `ValuePattern`) —
  the reliable path is **clipboard paste** (`desktop_clipboard_set` → focus → `desktop_key ctrl+v`),
  which lands atomically and doesn't race.
  - **Escape hatch:** launch the specific app with **`--force-renderer-accessibility`** (edit its
    shortcut / launch args) and Chromium exposes its full UIA tree for that process.
- **WinUI 3 / WPF / WinForms / Qt:** generally expose **proper UIA peers** out of the box — this
  caveat mostly does **not** apply (a custom-drawn control with no UIA peer is the exception).
- **Zero-UIA surfaces (games, canvas, Citrix/RDP inners):** the coordinate + screenshot path is the
  intended route; OCR-assisted targeting is a roadmap item.

Everything above degrades **safely** — the foreground/hit-test re-verify, lease, and deny-list mean
a limited surface **aborts or no-ops**; it never mis-fires into the wrong window.

## How it compares to WebDriver-based test automation (e.g. Appium)

If you've automated Windows UIs before, it was probably with **WebDriver-based test automation** —
Appium driving the Windows Application Driver (WinAppDriver). FlaUI.Mcp and that stack both control
Windows apps through UI Automation, but they're built for **different jobs**, and it's worth being
clear which one you actually want.

**The intent divide (the important part).** WebDriver-based tools exist for **deterministic test
automation**: a test author writes an explicit script, runs it in a CI pipeline, and expects the
same steps to pass or fail the same way every time. FlaUI.Mcp exists for **non-deterministic AI
agents**: a model decides at runtime what to look at and what to do next, over MCP. These are
largely **non-competing niches** — one serves test suites, the other serves agents. Writing a
regression suite? Reach for the WebDriver stack. Giving an agent eyes and hands on the desktop?
That's this.

**What's similar**

- Both drive Windows apps through **UI Automation** (FlaUI.Mcp via UIA3/FlaUI; appium-windows-driver
  wraps WinAppDriver, also UIA-based).
- Both **enumerate windows, read element properties, screenshot, and interact** with controls.
- Both can **synthesize mouse/keyboard input** — though FlaUI.Mcp's is lease-gated (a human unlock is
  required), while Appium's fires immediately.

**What's different**

| Aspect | FlaUI.Mcp | WebDriver-based (Appium + WinAppDriver) |
|--------|-----------|------------------------------------------|
| **Consumer** | Non-deterministic AI agents | Test authors / CI pipelines |
| **Protocol** | Model Context Protocol (stdio JSON-RPC) | WebDriver protocol (HTTP) |
| **Shape** | A tool surface an agent calls ad hoc | A scripted client session |
| **Safety** | Time-lease, deny-lists, per-window budget, credential redaction | No equivalent guardrails |
| **Ecosystem** | MCP clients (Claude Code, Antigravity, generic) | Selenium/Appium language bindings, large community |
| **Packaging** | Single self-contained exe, no runtime | Appium server + driver + client stack |

**It is *not* a drop-in Appium replacement.** FlaUI.Mcp speaks **no WebDriver protocol**, ships **no
language-binding ecosystem** (the Java/Python/C#/JS Selenium clients), and is **not a test
framework** — no assertions, no test runner, no page objects. If you have an Appium suite, this does
not run it. It's a different tool for a different consumer.

**Rough guide:** if you're giving an AI agent supervised control of the Windows desktop — with safety
rails and credential redaction — FlaUI.Mcp is built for that. If you need deterministic UI test
automation, cross-platform coverage (Appium also drives macOS/Linux/Android/iOS), or you already have
a WebDriver investment, the Appium stack is the established choice.

## Known limitations

Capability boundaries you'll meet in practice. Each links to the fuller explanation above — this
is just the scannable index. (These are about what the tool **can't reach**; that's distinct from
the [security warning](#warning) at the top, which is about
what an agent **can do** to your machine.)

- **Can't drive elevated / Administrator apps.** UIPI blocks a normally-launched server from sending
  input to higher-integrity windows — a Windows boundary, not a bug. [→ Synthetic input](#synthetic-input)
- **No headless operation.** It drives real windows and needs an interactive desktop session.
  [→ Requirements](#requirements)
- **Input needs a connected, unlocked session.** `SendInput` can't reach a locked or RDP-disconnected
  desktop; those calls return `InputDesktopUnavailable`. [→ Synthetic input](#synthetic-input)
- **Electron / Chromium apps show one opaque node.** Their accessibility tree is off by default (VS
  Code, Slack, Discord, Teams); use the coordinate path or `--force-renderer-accessibility`.
  [→ Electron / Chromium](#electron--chromium--other-custom-render-apps)
- **Some editors garble typed text.** The new Win11 Notepad and Chromium editors corrupt `SendInput`
  at any pacing; prefer `desktop_set_value` or clipboard paste. [→ Synthetic input](#synthetic-input)
- **Screenshots don't handle occlusion.** A covered window is captured as-is — focus it first.
  [→ Perception safeguards](#perception-safeguards-built-in)
- **Zero-UIA surfaces need the coordinate path.** Games, canvas apps, and Citrix/RDP inners expose no
  accessibility tree; drive them by coordinate + screenshot. [→ Electron / Chromium](#electron--chromium--other-custom-render-apps)
- **Elements with no AutomationId *and* no Name can't be re-resolved after recycling.** On a
  cache-miss a state-changing action fails `REF_STALE_UNRESOLVABLE` (it never guesses); fall back to
  `desktop_click_at`. [→ Ref resolution](#ref-resolution-safe-by-default)
- **Released binaries are unsigned.** SmartScreen and antivirus will flag them; verify the published
  SHA-256 checksums. [→ warning](#warning)
- **It's a guardrail, not a sandbox.** The lease and deny-list constrain an agent driving high-risk
  sinks, but anything running as your user can act as your user. [→ Synthetic input](#synthetic-input)

## Requirements

- **Windows 10/11, x64.** (FlaUI/UIA is Windows-only.)
- **No .NET runtime and no build tools required** for the released binaries — they are
  self-contained, single-file `win-x64` executables.
- An **interactive desktop session** (the agent drives real windows; it does not work
  headless).

## Installation

The installer drops `flaui-mcp.exe` into `%LOCALAPPDATA%\Programs\FlaUI.Mcp\` and configures
every agent it can find. No manual config editing is required.

### Option A — Standalone installer (recommended)

1. Download `flaui-mcp-setup.exe` from the [latest release](https://github.com/ckir/flauimcp/releases/latest).
2. (Optional but recommended) verify its SHA-256 against `SHA256SUMS.txt` from the same release.
3. Run it. SmartScreen will warn (unsigned) — choose **More info → Run anyway**.
4. The installer configures Claude Code, Antigravity, and a generic MCP config automatically.
5. **Restart Antigravity (agy)** if you use it, so it reloads its tool registry.

### Option B — Silent one-liner (PowerShell)

```powershell
irm https://raw.githubusercontent.com/ckir/flauimcp/master/dist/install.ps1 | iex
```

This downloads the latest `flaui-mcp-setup.exe` and runs it silently
(`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`). Pass `-Version vX.Y.Z` to pin a release.

### Option C — Manual (the exe is its own installer)

Download `flaui-mcp.exe` from the release, put it somewhere permanent, and run:

```powershell
flaui-mcp install --agent all
```

Use `--agent agy|generic|claude` to configure a single agent instead of all.

## What the installer changes

The configuration is **idempotent** (safe to re-run), writes **atomically** with a
timestamped backup of each file it touches, and on uninstall performs **targeted key
removal** — it only deletes FlaUI.Mcp's own entries and leaves your other settings intact.

| Agent | File(s) | Change |
| --- | --- | --- |
| **Claude Code** | (via `claude mcp add/remove` CLI) | Registers the `flaui-mcp` MCP server. |
| **Antigravity (agy)** | `~/.gemini/settings.json` + `~/.gemini/antigravity-cli/settings.json` | Adds `mcpServers.flaui-mcp` and appends `mcp(flaui-mcp/*)` to `permissions.allow`. |
| **Generic MCP** | `~/.flaui-mcp/generic-mcp.json` | Writes the standard `{ "mcpServers": { "flaui-mcp": { "command": "<exe>", "args": [] } } }` snippet. |

To see the generic config snippet without writing any file:

```powershell
flaui-mcp print-config --agent generic
```

## Usage

Once installed and your agent is restarted, just ask the agent to do desktop work — e.g.
"list my open windows," "launch Notepad," "focus the Calculator window." The agent calls the
MCP tools above.

The MCP server itself is the bare executable speaking JSON-RPC over stdio (no wrapper script,
no launch-time download). Running `flaui-mcp` with **no arguments** starts the server;
running it with a verb (`install`, `uninstall`, `print-config`, `--version`) runs the
installer instead.

## Uninstall

- **Via the installer:** uninstall "FlaUI.Mcp" from **Settings → Apps** (or
  Add/Remove Programs). The uninstaller reverts every agent's config (targeted removal) and
  removes the files.
- **Manually:** `flaui-mcp uninstall --agent all`, then delete the executable.

Uninstalling reverts configuration entries but leaves your unrelated settings untouched.

## CLI reference

```text
flaui-mcp                                   # run the stdio MCP server (no args)
flaui-mcp --read-only-mode                  # run the server but refuse all state-changing tools
flaui-mcp --unsafe-allow-elevation          # allow synthetic input when running elevated (default: hard-refused)
flaui-mcp unlock --minutes N [--allow-shells]  # grant a time-bounded synthetic-input lease (human out-of-band)
flaui-mcp lock                              # revoke the synthetic-input lease immediately
flaui-mcp install   --agent agy|generic|claude|all
flaui-mcp uninstall --agent agy|generic|claude|all
flaui-mcp print-config --agent generic      # print the JSON snippet to stdout
flaui-mcp --version
```

`--config <path>` overrides the target config file (useful for testing without touching your
real agent configuration).

## Building from source

Requires the **.NET 10 SDK** (the project targets `net10.0-windows`).

```powershell
# Build + run tests (the UIA/window tests need an interactive desktop)
dotnet build test/FlaUI.Mcp.TestApp
dotnet test

# Run only the non-desktop unit tests (e.g. in headless CI)
dotnet test --filter "Category!=Desktop"

# Produce the self-contained single-file exe
dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Tagging a commit `v*` triggers the release workflow
([`.github/workflows/release.yml`](.github/workflows/release.yml)), which builds the exe and
the Inno Setup installer and publishes them — with checksums — to a GitHub Release.

## Contributing

Contributions — **especially new MCP tools** — are welcome. The fast path: run
`./scripts/new-tool.ps1 -Name DesktopFoo` to scaffold a tool + test, fill the stub, and open a PR.
See **[CONTRIBUTING.md](CONTRIBUTING.md)** for setup, the (honest) test loop, and the tool pattern.

Heads-up: FlaUI.Mcp is noncommercially licensed and the maintainer sells commercial licenses, so your
first PR triggers a one-click **CLA** ([CLA.md](CLA.md)).

## License

[PolyForm Noncommercial License 1.0.0](LICENSE) — © 2026 Costas Kirgoussios. Free for
noncommercial use (personal, research, education, hobby, nonprofit, government). Commercial
use requires a separate license.
