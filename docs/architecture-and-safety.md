# Architecture and Safety

This document defines the guarantees and rationale behind FlaUI.Mcp — how it works and why it is safe.

## What it does

FlaUI.Mcp gives an AI agent eyes and hands on the Windows desktop. It translates Model Context Protocol (MCP) commands into native UI Automation reads, pattern-based interactions, and synthetic OS-level input. It exposes Windows desktop control to non-deterministic agents while wrapping that access in a safety foundation that deterministic test-automation tools lack.

## The dual-axis safety model

The server enforces safety across two independent axes: the input lease and the destructive flag.

**Axis 1: The Lease (Synthetic Input)**
Only the `SendInput`-backed synthetic-input tools require a human-granted lease (`flaui-mcp unlock`). These are exactly: `desktop_type`, `desktop_paste_text`, `desktop_key`, `desktop_click`, `desktop_click_at`, and `desktop_drag`. Tools that change state via UI Automation patterns (like `desktop_set_value` or `desktop_toggle`) do not need a lease.

**Axis 2: The Destructive Flag (Read-Only Mode)**
Any state-changing tool is blocked when the server runs in `--read-only-mode`. This is a separate axis from the lease.

The two axes are independent. A tool can be destructive but lease-exempt (e.g. `desktop_set_caret`, `desktop_select_text_range`, `desktop_read_terminal_tab` change state and need no lease, but are still blocked in `--read-only-mode`). Unattended-safe operation requires tools that are both lease-exempt and non-destructive (the pure read tools). See the [Agent Contract](agent-contract.md) for each tool's axis.

## Why synthetic input needs a lease

Synthetic input (`SendInput`) drives the desktop exactly as a physical keyboard or mouse does. It bypasses application-level API isolation and can drive high-risk sinks like credential dialogs, run prompts, and shells. The time-lease acts as a dead-man's switch. It ensures synthetic input only fires while a human is present, actively supervising, and has explicitly authorized the time window. The agent cannot grant or extend its own lease; a human grants it out-of-band via the [Operator Manual](operator-manual.md).

## Read-only mode rationale

Agents often need to perceive the desktop without permission to act on it. Starting the server in `--read-only-mode` physically drops the ability to mutate state. Every interaction tool, synthetic input tool, and window lifecycle tool (launch/focus/close) short-circuits to `WriteBlockedReadOnly` without touching the desktop. Enumeration, event streaming, and perception remain fully active.

## Perception safeguards

The server defends against credential exfiltration at the perception layer:

- **Credential stores:** Windows owned by known password managers are blocked outright. A snapshot or grid read returns `TargetDenied`.
- **Redaction:** UI Automation password fields are always redacted. They appear as `[REDACTED]` in the accessibility tree and are painted over with an opaque black rectangle during screenshots, covering popups and menus.
- **Full-desktop capture:** Capturing the entire virtual desktop is refused if any credential-store window is visible.
- **Dead sessions:** Screenshots return `CaptureUnavailable` if the desktop is locked or RDP-disconnected, preventing a black capture.
- **Elevation guard:** The server warns if started with Administrator rights. It is meant to run at the user integrity level.
- **RDP/Citrix limitation:** The credential deny-list is process-coarse. It cannot inspect applications running inside remote desktop wrappers. OCR can still punch through to read text inside these wrappers.

## User-state presence

`desktop_user_state` exposes a coarse human-presence signal so an agent can reason about whether a human is at the keyboard. 

The tool returns a coarse activity bucket (`active`, `nearby`, `away`) rather than raw idle milliseconds, protecting the user's keystroke cadence and biometric timing. The signal is off by default. The agent cannot enable it; a human must opt in out-of-band. The server acts only as a dumb sensor making no outbound calls. Escalating to a remote channel is the agent's job.

## The auditing model and what it guarantees

Auditing features ensure a supervising human can predict and verify agent actions:

- **Intent overlay:** Draws a red rectangle or crosshair on the target element before a mutative action fires. This is a visibility aid so a human sees what the agent will touch. It is not an authorization gate.
- **Audible attention cue:** Speaks the target app name when a background window needs attention. It is leak-safe and never exposes window titles or content.
- **Element-identity audit trace:** When a selector resolves, the input audit log records the element's stable identity (`RuntimeId`, `AutomationId`, `ClassName`, `ControlType`, bounds). It strictly omits content-bearing properties (`Name`, `Value`, `HelpText`).
- **Foreground-lock attention handshake:** Exposes the OS foreground lock state. When the background server cannot bring a window to the front, it returns the current foreground process (leak-safe) so the agent can orchestrate recovery.

## Why opaque apps need waking

Native Windows apps expose an accessibility tree by default. Chromium and Electron apps keep their trees off by default for performance, exposing only a single opaque `Document` node to a snapshot.

The `desktop_wake_accessibility` tool activates and holds the native UI Automation tree for that specific process. Waking hydrates the UI hierarchy, enabling standard snapshot and interaction tools.

If an application exposes zero accessibility data (games, canvas apps) or keeps its document text body gated even when woken, the agent falls back to on-box OCR (`desktop_find_text`). OCR resolves visible text to click coordinates so the agent can act via coordinate-based input.

## How it compares to WebDriver/Appium

WebDriver-based tools (Appium, WinAppDriver) exist for deterministic test automation. A test author writes an explicit script, runs it in a CI pipeline, and expects identical passes or failures every time.

FlaUI.Mcp exists for non-deterministic AI agents deciding at runtime what to look at and what to do next. It replaces the test runner assumptions with safety guardrails: time-leases, credential redaction, and per-window budgets. It speaks JSON-RPC over stdio, not the HTTP WebDriver protocol, and is not a test framework.

## The full danger rationale

Installing this software gives an AI agent the ability to operate your computer as if it were sitting at the keyboard. FlaUI.Mcp provides guardrails, but it is not a sandbox.

- **User Privileges:** The server runs at your user integrity level. The agent can launch programs, close windows (losing unsaved work), and interact with files. Anything you can do without an admin prompt, the agent can do.
- **Prompt Injection:** An agent reading attacker-controlled content (emails, web pages) can be instructed to attack the host. The blast radius of a successful injection is the entire desktop session.
- **Live Input:** The agent drives real OS input. The lease constraints manage when input happens, but they do not parse intent.
- **Arbitrary Execution:** The tool surface allows the agent to launch arbitrary executables. Treat the agent's access as equivalent to giving it a shell.
- **Elevation Boundary:** The server cannot send input to apps running as Administrator (UIPI). This is a Windows boundary, not a bug.

Run this software only where you are comfortable with an automated agent taking actions on your behalf and where you can supervise it.
