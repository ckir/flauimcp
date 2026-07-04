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
Automation / UIA3) and the official MCP C# SDK. It exposes the Windows desktop to an agent as
MCP tools across five areas: **window management**; **perception** (ref-tagged accessibility-tree
snapshots, screenshots, find, diff/stat, wait conditions); **pattern-based interaction** (invoke,
set value, toggle, expand, select, scroll, focus, window transform); **structured content &
clipboard**; and **lease-gated synthetic mouse/keyboard input** — plus **event streaming** and
**opaque-app access** (wake + on-box OCR). Every tool returns structured JSON with a uniform error
envelope, and read-only tools are annotated so clients can auto-approve them while still prompting
for the mutating ones. All synthetic input sits behind a safety foundation — time-lease, deny-list,
per-window budget, audit, and elevation guard.

➡ **Full tool tables, the safety model, event streaming, and opaque-app access:
[docs/features-and-safeguards.md](docs/features-and-safeguards.md).**

### Targeting: `ref` or `selector`

Interaction tools accept either a `ref` from a `desktop_snapshot` **or** a `selector`
(`{automationId?, name?, nameMatch?, controlType?, scope?, ignoreCase?}`) resolved fresh at action
time — exactly one of the two, always. A selector with a stable `automationId` survives the
snapshot churn that would otherwise force a re-`desktop_snapshot`; one with no `automationId` and a
non-unique `name` still needs a snapshot ref (0 or >1 matches fail closed as `SelectorNoMatch` /
`AmbiguousMatch`, never a silent guess). Full contract, the `resolvedElement` durability caveat, and
`ignoreCase` semantics:
[Targeting: ref or selector](docs/features-and-safeguards.md#targeting-ref-or-selector).

## Documentation

- **[Features & safeguards](docs/features-and-safeguards.md)** — full tool reference, the safety
  model, ref resolution, event streaming, opaque-app access, and the Appium comparison.
- **[Ops manual](docs/ops-manual.md)** — manual install, what the installer changes, uninstall,
  and the full CLI reference.
- **[Building from source](docs/building.md)** — SDK, test loop, and packaging.
- **[Contributing](CONTRIBUTING.md)** — setup, the (honest) test loop, and the tool pattern.
- **[Changelog](CHANGELOG.md)** — per-version feature history.

## Requirements

- **Windows 10/11, x64.** (FlaUI/UIA is Windows-only.)
- **v0.9.0+ requires Windows 10 build 19041 (version 2004) or later** — the on-box OCR features
  (`desktop_find_text`/`desktop_wait_for_text`) depend on the WinRT OCR projection, which raised the
  minimum supported OS build.
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

> **Manual install** (the exe is its own installer), **what the installer changes**, and
> **uninstall** live in the **[Ops manual](docs/ops-manual.md)**.

## Usage

Once installed and your agent is restarted, just ask the agent to do desktop work — e.g.
"list my open windows," "launch Notepad," "focus the Calculator window." The agent calls the
MCP tools documented in **[Features & safeguards](docs/features-and-safeguards.md)**.

The MCP server itself is the bare executable speaking JSON-RPC over stdio (no wrapper script,
no launch-time download). Running `flaui-mcp` with **no arguments** starts the server;
running it with a verb (`install`, `uninstall`, `print-config`, `--version`) runs the
installer instead. See the **[CLI reference](docs/ops-manual.md#cli-reference)**.

## Known limitations

Capability boundaries you'll meet in practice. Each links to the fuller explanation — this
is just the scannable index. (These are about what the tool **can't reach**; that's distinct from
the [security warning](#warning) at the top, which is about
what an agent **can do** to your machine.)

- **Can't drive elevated / Administrator apps.** UIPI blocks a normally-launched server from sending
  input to higher-integrity windows — a Windows boundary, not a bug. [→ Synthetic input](docs/features-and-safeguards.md#synthetic-input)
- **No headless operation.** It drives real windows and needs an interactive desktop session.
  [→ Requirements](#requirements)
- **Input needs a connected, unlocked session.** `SendInput` can't reach a locked or RDP-disconnected
  desktop; those calls return `InputDesktopUnavailable`. [→ Synthetic input](docs/features-and-safeguards.md#synthetic-input)
- **Electron / Chromium apps show one opaque node.** Their accessibility tree is off by default (VS
  Code, Slack, Discord, Teams); use the coordinate path or `--force-renderer-accessibility`.
  [→ Electron / Chromium](docs/features-and-safeguards.md#electron--chromium--other-custom-render-apps)
- **Some editors garble typed text.** The new Win11 Notepad and Chromium editors corrupt `SendInput`
  at any pacing; prefer `desktop_set_value` where available, else `desktop_paste_text` (its clipboard
  restore is best-effort, not guaranteed). [→ Synthetic input](docs/features-and-safeguards.md#synthetic-input)
- **Watch event refs are ephemeral.** An event's `ref` lives in a small bounded per-window pool (64,
  shared across all event kinds), so a busy `structure_changed` watch can evict older refs before you
  act on them → `REF_NOT_FOUND`. Re-`desktop_snapshot` for a durable ref. [→ Event streaming](docs/features-and-safeguards.md#event-streaming-desktop_watch)
- **OCR text targeting needs a Windows OCR language pack.** `desktop_find_text`/`desktop_wait_for_text`
  return `OcrUnavailable` if none is installed (Settings → Time & Language → Language & region → add
  a language, ensuring its optional OCR component is installed). [→ Opaque apps: wake + find_text](docs/features-and-safeguards.md#opaque-apps-wake--find_text)
- **OCR is targeting, not reading.** `desktop_find_text` resolves visible text to click coordinates;
  it does not summarize or transcribe text back to the agent as data — the model reads that from the
  screenshot. A fuzzy query can also match inside unrelated body text, so verify each match's
  `text`/`bounds` before acting on it. [→ Opaque apps: wake + find_text](docs/features-and-safeguards.md#opaque-apps-wake--find_text)
- **OCR only recognizes installed OCR languages.** `Windows.Media.Ocr` reads only the Windows OCR
  languages installed on the host; a target window rendering text in a language whose OCR pack isn't
  installed yields **no matches**, not an error — indistinguishable from "text not present."
  [→ Opaque apps: wake + find_text](docs/features-and-safeguards.md#opaque-apps-wake--find_text)
- **The process-coarse deny-list can be punched through by OCR into RDP/Citrix wrappers.** The
  credential-store deny-list matches by process name; a denied app rendered *inside* a remote-desktop
  window is invisible to it (the visible process is the RDP/Citrix client, not the remote app), so an
  OCR capture of that window can still surface and target the remote app's on-screen text.
  [→ Perception safeguards](docs/features-and-safeguards.md#perception-safeguards-built-in)
- **An editor's document text body can stay behind a screen-reader gate even when woken.**
  `desktop_wake_accessibility` hydrates a Chromium/Electron window's *chrome* tree, but some editors
  keep the actual document text gated separately — if `desktop_snapshot` still shows an empty text
  body after waking, fall back to `desktop_find_text`. [→ Opaque apps: wake + find_text](docs/features-and-safeguards.md#opaque-apps-wake--find_text)
- **`desktop_find_text` coordinate mapping is host-limited in CI, not a product limitation.** The
  screen-px/window-fraction mapping is validated end-to-end on a DPI-aware connected console/server
  session; the CI xUnit test host runs DPI-virtualized, so the capture-based Desktop test for it is
  maintainer-run rather than CI-asserted. [→ Building from source](docs/building.md#building-from-source)
- **Screenshots don't handle occlusion.** A covered window is captured as-is — focus it first.
  [→ Perception safeguards](docs/features-and-safeguards.md#perception-safeguards-built-in)
- **Zero-UIA surfaces need the coordinate path.** Games, canvas apps, and Citrix/RDP inners expose no
  accessibility tree; drive them by coordinate + screenshot. [→ Electron / Chromium](docs/features-and-safeguards.md#electron--chromium--other-custom-render-apps)
- **Elements with no AutomationId *and* no Name can't be re-resolved after recycling.** On a
  cache-miss a state-changing action fails `REF_STALE_UNRESOLVABLE` (it never guesses); fall back to
  `desktop_click_at`. [→ Ref resolution](docs/features-and-safeguards.md#ref-resolution-safe-by-default)
- **Released binaries are unsigned.** SmartScreen and antivirus will flag them; verify the published
  SHA-256 checksums. [→ warning](#warning)
- **It's a guardrail, not a sandbox.** The lease and deny-list constrain an agent driving high-risk
  sinks, but anything running as your user can act as your user. [→ Synthetic input](docs/features-and-safeguards.md#synthetic-input)

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
