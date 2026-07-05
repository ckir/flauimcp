# FlaUI.Mcp — Human-Attention Toolset (SP-A) — Design

**Date:** 2026-07-05
**Status:** Draft (SP-A of a staged "foreground-lock ergonomics" effort)
**Origin:** Consumer-driven. While driving the server, the agent (the primary consumer) hit the Windows
foreground-lock: a *background-process* server cannot `SetForegroundWindow` an already-open window past
the active terminal, so an input action on a non-foreground window fails the ugly way — a generic
`ElementDisappearedDuringAction` abort discovered *mid-action*, after a wasted focus call.

This design is the product of a multi-round AGY-FIRST/AGY-AFTER exploration; §9 records what was
deliberately dropped so it is not silently re-litigated.

## 1. Reframe (the load-bearing idea)

The foreground-lock is **not an enemy to defeat — it is the boundary of human attention.** If a window
isn't foreground, the human's attention isn't there, so a background process sneaking focus is *wrong*,
not merely blocked. This design **leans into** that: instead of fighting the lock, it makes the lock
**legible** (tell the agent exactly what's blocked and what to do) and gives the human a **physical
handshake** (flash the window; on request, speak) — fully aligned with the product promise, *"eyes and
hands for AI agents on the Windows desktop, with a human holding the keys."*

**Brain/hands split (locked boundary):** the *goal, loop, timers, and escalation/paging* live in the
**agent** (a Claude-Code `/loop`-style command, working name `/autogoal`) — **not** in this server. The
server provides only **tools**: the hands (existing input/read tools), the **legible gate + attention
signals** (this spec), and the **key** (the lease). No autonomous loop, no timers, and — critically — **no
outbound network** is ever built into the server.

## 2. Scope

**In scope (SP-A — the "Human-Attention Toolset," the tool surface `/autogoal` will consume):**

1. **Enriched `TargetNotForeground`** at the mutative input gate (replaces the generic abort).
2. An **`IAttentionSignal` seam** (pluggable attention channels), paralleling the existing overlay's
   `Null…`/`Gdi…` split.
3. A **Flash** channel (`FlashWindowEx`), the always-available default.
4. A **TTS** channel, gated by a new human-only CLI verb **`flaui-mcp autosound on|off`** (off by default),
   debounced inside the channel.
5. A blocking **`desktop_wait_for_foreground(window, timeoutMs)`** resume primitive.
6. A **long-lease disclaiming warning** at grant (the "key").

**Out of scope / deferred (see §10):**
- **SP-C — legitimate raise** (unlock-time `AllowSetForegroundWindow` "golden token"): needs a Win32 spike;
  separate spec.
- **`/autogoal`** itself: agent-layer, separate from this repo.
- Any **in-server remote-alert / outbound network**: permanently dropped (§9).
- **`userIdleMs` / presence sensor**: dropped (§9); the `wait_for_foreground` timeout is the present/absent
  signal instead.

## 3. Architecture

A single seam decouples *"the target isn't foreground, tell the human"* from *how* the human is signaled:

```
Input tool (type/click/key/…)          desktop_wait_for_foreground
        │ foreground check (fast, pre-UIA)        │
        ▼                                         ▼
   TargetNotForeground  ──emits──►  IAttentionSignal.Signal(targetWindow)
                                          │
                          ┌───────────────┼───────────────┐
                          ▼               ▼                ▼
                   FlashSignal      TtsSignal         (future channels
                   (default)     (autosound-gated,     via the seam)
                                  debounced)
```

`IAttentionSignal` mirrors the overlay pattern already in the codebase: a real implementation when
enabled, a `Null` no-op when disabled, DI-bound by flag. The gate logic knows only *"signal attention on
window X"* — never *how*.

## 4. Component specifications

### 4.1 Enriched `TargetNotForeground` (the legible gate)

- **Trigger:** a mutative input tool (`desktop_type`/`desktop_click`/`desktop_key`/etc.) targets a window
  that is not the OS foreground. The check fires **fast, before UIA engages** (the existing gate already
  re-verifies foreground before `SendInput`; this replaces its *generic* failure with a specific one).
- **Result shape (leak-safe):**
  ```
  TargetNotForeground {
    targetWindow:     "<wN>",
    currentForeground:{ handle:"<hwnd-or-wN>", process:"<processName>" },   // NO title by default (see below)
    recommendedAction:"launch-fresh" | "call-wait-for-foreground",
    recovery:         "<one-line script routing the agent to the tool — see below>"
  }
  ```
- **Title leak rule (mandatory — AGY-AFTER panel, security seat):** `currentForeground` returns the
  **process name only** by default. Process-match is **NOT** a sufficient reason to disclose a title —
  Chrome/Edge/Explorer run many windows under one PID, so a same-PID rule would leak a *sibling* window's
  title (e.g. the human's banking tab) while the agent targets an unrelated window of the same app. A
  title may be returned **only** when the foreground window is a **modal owned by the EXACT target window
  instance** — verified by an owner/child HWND relationship (`GetWindow(target, GW_OWNER)` / owner-HWND ==
  target), never by process equality. If that hierarchy check is not cheaply available, return **no
  title at all**. Rationale: a raw title could be `"1Password — Main Vault"`; leaking whatever else the
  human is looking at into the agent's context is a privacy breach and must never happen.
- **`recommendedAction` selection (AGY-AFTER panel, API seat — it must route to the TOOL, not a chat-yield):**
  `call-wait-for-foreground` when the target window **exists and is visible** (the fix is to wait for the
  human to click it — via the tool, §4.5); `launch-fresh` when the remedy is a fresh launch
  (launch-grants-foreground). The enum value deliberately names the *tool*, because an LLM told to
  "ask-human-to-click" will emit a chat message and **yield the turn** — bypassing `wait_for_foreground`.
- **`recovery` string:** must explicitly route the agent to the primitive, e.g.
  *"Call `desktop_wait_for_foreground` on this window — do NOT yield the chat turn to wait."* (for
  `call-wait-for-foreground`) or *"Relaunch the app; the fresh window takes foreground."* (for
  `launch-fresh`).
- **`desktop_focus_window`** returns the same enriched why-not (it already returns `foregroundGained`;
  extend it with `currentForeground`/`recommendedAction` under the same leak rule).
- **Emitting the attention signal:** on a `call-wait-for-foreground` result, the gate calls
  `IAttentionSignal.Signal(targetWindow)` (flash by default) so the human gets the physical pointer.

### 4.2 `IAttentionSignal` seam

- Interface: `void Signal(WindowHandle target)` (best-effort, never throws — a failed signal must not turn
  a tool result into an error).
- Implementations: `FlashSignal` (default, always available), `TtsSignal` (autosound-gated), `NullSignal`
  (no-op). DI-bound by the enabled channels, mirroring the overlay's `IActionOverlay` Null/Gdi wiring.
- Composable: multiple channels may be active (flash **and** TTS when `autosound on`); `Signal` fans out.

### 4.3 Flash channel

- `FlashWindowEx` with `FLASHW_TIMERNOFG` (flashes the taskbar button until the window comes to the
  foreground; **steals no focus**, needs **no foreground rights**, works cross-process with just the HWND).
- Always available (no flag) — it is silent and non-intrusive.
- Edge case: a window with no taskbar button (tool windows) may not visibly flash; acceptable, documented.

### 4.4 TTS channel + `flaui-mcp autosound on|off`

- **Human-controlled CLI verb only**, modeled exactly on the existing **`flaui-mcp overlay on|off`**:
  `autosound on` re-registers the server across client configs with an `--autosound` flag; `off` removes
  it. **Off by default.** The **agent can never enable audio** — whether the machine may *talk* is the
  human's call over their own physical environment (this also denies a prompt-injected agent a megaphone).
- **Takes effect on reconnect, not live (AGY-AFTER panel, release/ops seat):** like `overlay on|off`, this
  edits the MCP client's config; MCP clients (e.g. Claude Code) do **not** hot-reload a server's launch
  args mid-session. So the flag applies only after the client reconnects/restarts (`/mcp`). The CLI output
  **must say this explicitly** ("run `/mcp` to reconnect for this to take effect") so the human doesn't run
  `autosound on`, hear nothing, and assume it's broken. (No live IPC — the reconnect model matches the
  existing overlay toggle.)
- **When off:** `TtsSignal` resolves to a no-op (like `NullSignal`) — byte-for-byte no audio path.
- **Debounce/rate-limit lives INSIDE the channel, CHANNEL-WIDE (AGY-AFTER panel R2, adversarial seat):**
  the cooldown must be a **global token-bucket agnostic to the target** — NOT per-target. A per-target
  debounce is trivially evaded by a prompt-injected agent oscillating targets (`w1`→`w2`→`w1`…), which
  never re-triggers the same-target suppressor → continuous audio spam. One channel-wide bucket (e.g. ≤ K
  utterances per rolling window, coalesce bursts) caps total audio regardless of target. (Exact rate is a
  plan-level constant.)
- **Spoken content is leak-safe:** the utterance names **only the target app** (the app the agent already
  chose to act on and already knows) — e.g. *"Please click Character Map."* It must **never** speak the
  cross-process foreground title (same rule as §4.1).
- **Speech stack:** `System.Speech` vs WinRT `SpeechSynthesizer` is a plan-level decision; the seam hides
  it. Whichever is chosen must not inflate the default (off) build path.

### 4.5 `desktop_wait_for_foreground(window, timeoutMs)` (the resume primitive)

Solves the **resumption trap**: after a `TargetNotForeground`, the agent shouldn't tight-poll
`desktop_focus_window` (burns tokens) or force the human to click-then-switch-back-and-type-"done"
(destroys the "just click the app" UX). Instead it calls one blocking tool that flashes + waits.

- **Behavior:** flash the target (via `IAttentionSignal`), then block until one of three things: the target
  gains foreground, the target window is **destroyed**, or `timeoutMs` elapses. Return
  `{ foregroundGained: bool, reason: "gained" | "timeout" | "window-destroyed", currentForeground:{handle,process} }`
  (leak rule §4.1 applies).
- **Window-destruction early-exit (AGY-AFTER panel R2, API seat):** if the human answers the flash by
  **closing** the target (a legitimate "no, I reject this workflow"), it will never gain foreground — so the
  wait must ALSO watch `EVENT_OBJECT_DESTROY` (or poll `!IsWindow(target)`) and return early with
  `{ foregroundGained:false, reason:"window-destroyed" }` rather than hanging the full timeout on a dead
  HWND. This also hands the agent a meaningful "human declined" signal distinct from "human is away."
- **Concurrency (mandatory, AGY-folded):** the wait MUST be event-driven on a **dedicated non-STA thread**
  — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + `EVENT_OBJECT_DESTROY` (or equivalent) — **never**
  `Thread.Sleep`/spin on the shared query or action STA (that would deadlock the dispatcher and starve
  every other tool call).
- **Timeout hard-cap (mandatory, AGY-folded — the "chat-hostage" fix):** `timeoutMs` is **server-capped to
  a strict maximum (~45–60 s)** regardless of what the caller requests. A long block would hold the agent
  hostage — most LLM clients do not deliver new human chat until the current tool call returns, so a
  5-minute block means a human *"stop, I changed my mind"* is invisible until timeout, leaving only
  click-the-window or Ctrl+C as exits. A short cap makes this a **slow-poll**: on timeout return
  `foregroundGained:false`; the agent **re-invokes** to keep waiting — few tokens, and it yields to the
  client frequently enough to process human messages. (It also stays under common MCP-client per-tool
  timeout ceilings, so the client never SIGTERMs the call.)
- **DoS guard:** `MaxConcurrentWaiters = 1` server-side (an injected agent cannot fire N parallel waiters
  to hammer the flash API).
- **Lease:** **exempt** — flash + wait involve no synthetic input, so this works while input is locked
  (parity with the other read/`wait_for_*` tools).
- **Escalation fork (no sensor needed):** the timeout **is** the present/absent signal. Present → the human
  clicks, it returns fast, no paging. Absent → it times out repeatedly → the *agent* decides to page via
  its own notification MCP. This is why `userIdleMs` is unnecessary (§9).

### 4.6 Long-lease disclaiming warning (the "key")

- Allow a long input lease (e.g. `unlock --minutes 999`); a long lease is treated like any lease
  mechanically — **no `--unattended` branding, no bespoke guardrails** (those would imply a safety the
  server cannot deliver — §9).
- **On grant of a lease beyond a normal-interactive threshold**, print a one-time **honest, safety-
  *disclaiming*** warning requiring explicit acknowledgment:
  > `WARNING: You are granting an uncontained {N}-minute input lease. FlaUI.Mcp provides NO sandboxing or`
  > `protection during this window. A prompt-injected agent can take full control of this machine and your`
  > `credentials. Only an ephemeral VM or low-privilege guest account can contain this risk. Type`
  > `'I understand' to continue.`
- **Non-interactive bypass (AGY-AFTER panel, UX/CI seat):** the interactive prompt would **hang headless
  CI** that legitimately wants a long lease (e.g. an automated UI-test run). `unlock` must accept an
  explicit `--accept-risk` (a.k.a. `--i-understand`) flag that acknowledges the warning **non-interactively**
  — it prints the same warning text to the log (so the acknowledgment is on record) but skips the `stdin`
  read. Interactive humans get the prompt; scripts pass the flag. Absence of both a TTY and the flag on a
  long-lease request → refuse (don't silently proceed).
- This is **disclosure, not theater** — it points at the open door and says "there are no walls," pushing
  the risk boundary entirely to the operator's OS-level containment choices (VM / stripped guest account +
  the agent's own `--dangerously-skip-permissions`). The threshold above which the warning fires is a
  plan-level constant (a normal short interactive unlock stays frictionless).

## 5. Security posture (what makes this safe)

- **No outbound network in the server, ever.** Paging an absent human is the *agent's* job via a separate
  notification MCP. The desktop server stays a local, offline bridge — the property that keeps a
  lethal-trifecta surface contained.
- **Leak-safe everywhere:** cross-process foreground exposes process name only (never title), in both the
  `TargetNotForeground` payload and the spoken TTS line.
- **No evasion sensor:** `userIdleMs` is not exposed — a prompt-injected agent gets no clean "wait until
  the human is idle, then strike" clock.
- **Human owns the physical environment:** audio (`autosound`) is human-CLI-only; the agent can never turn
  on the speakers.
- **No safety theater:** the server does not pretend to sandbox an uncontained lease; it discloses the
  absence of protection and defers containment to the OS. Autonomous-action risk is the operator's,
  explicitly acknowledged.
- **DoS/robustness:** `wait_for_foreground` is single-waiter, short-capped, event-driven off the STAs, and
  never throws from the attention path.

## 6. Wire-contract / backward compatibility

- `TargetNotForeground` replaces the generic `ElementDisappearedDuringAction` for the *not-foreground*
  cause specifically, and fires **earlier** (before UIA). Primary consumers are LLM agents reading JSON,
  which adapt to the new shape immediately; the change is documented in `CHANGELOG.md` and the driving
  skill. The result must be returned cleanly through `ToolResponse` and must never crash the server.
- `ElementDisappearedDuringAction` remains for its *other* cause (focus genuinely stolen mid-action after
  a successful foreground check) — the two are now distinguishable, which is the point.

## 7. Testing

- **Headless (`Category!=Desktop`):** the leak-safe payload shaping (cross-process → process-name-only),
  the `recommendedAction` selection, the debounce logic, the `IAttentionSignal` fan-out/Null wiring, and
  the timeout-cap clamping are pure and unit-testable without a desktop.
- **Desktop (`Category=Desktop`, console-only — the dev/CI box is headless/RDP):** flash actually flashes;
  `wait_for_foreground` unblocks on a real foreground change and times out cleanly; `autosound on` speaks;
  the enriched gate fires on a real non-foreground target. Validated at a console smoke, mirroring prior
  releases.
- **Do not** mock the foreground-lock headlessly — it proves nothing (same discipline as the v0.11.x work).

## 8. Exhaustiveness self-audit (author, against the settled design)

- Every settled component (§2 items 1–6) has a §4 subsection with a concrete contract. ✅
- Every AGY-folded hazard is pinned in-document: cross-process title leak → §4.1/§5; STA starvation →
  §4.5; client-timeout/chat-hostage → §4.5; DoS single-waiter → §4.5; TTS content leak → §4.4; megaphone →
  §4.4; safety-theater/outbound → §5/§9; wire-break → §6. ✅
- AGY-AFTER team-panel folds (round 1): same-PID browser-tab title leak → tightened to owner/exact-HWND
  (§4.1); `recommendedAction` chat-hostage inducement → enum routes to the tool `call-wait-for-foreground`
  + `recovery` says "do not yield the turn" (§4.1); autosound reconnect-not-live → CLI must say `/mcp`
  (§4.4); interactive warning hangs CI → `--accept-risk` non-interactive bypass (§4.6). ✅
- **Deferred-to-plan (named, not vague):** TTS speech-stack choice (§4.4); debounce cooldown constant
  (§4.4); the `timeoutMs` hard-cap value (§4.5); the long-lease warning threshold (§4.6); exact
  DI/registration wiring for `--autosound` (§4.4). These are constants/mechanics, not open design forks.
- **No placeholders / TBD.** Open *decisions* (not gaps) are listed in §9/§10.

## 9. Decisions on record (dropped — do NOT re-litigate)

1. **In-server remote-alert / outbound channel — DROPPED permanently.** Airgap; paging is the agent's job
   via a separate notification MCP. An outbound client on a lethal-trifecta server is an exfil surface
   (incl. low-bandwidth timing covert channels) that no server-side lockdown closes.
2. **Bespoke "sudo mode" (allow-list / per-action reconfirm / kill-switch-as-safety) — DROPPED.** The
   server cannot reliably gate action semantics (coordinate clicks bypass name-gates; audit is post-hoc; a
   kill-switch is useless if the human is away). An unattended desktop lease is an uncontainable machine
   compromise; the only real containment is OS-level isolation, owned by the operator.
3. **`userIdleMs` presence sensor — reconsidered and re-added in SP-B, in a safe form.** The concern
   here (a "wait-for-the-guard" evasion sensor for a prompt-injected agent) was addressed rather than
   left unaddressed: SP-B ships a coarse `active`/`nearby`/`away` enum only (never raw idle-ms),
   opt-in and off by default, human-only to enable, with the agent orchestrating any escalation. See
   [`SP-B design`](2026-07-05-flaui-mcp-user-state-presence-design.md).
4. **A long lease is a time-scoped blank check** (a mid-lease injection inherits the remainder). Accepted
   as the operator's OS-contained risk; the §4.6 warning discloses it.

## 10. Follow-on tracks (not this spec)

- **SP-C — legitimate raise:** the unlock-time `AllowSetForegroundWindow` "golden token" (the granting
  `unlock.exe` is foreground, so it can hand the server a one-shot foreground grant; its fragility is a
  feature — a human click voids it). Rests on **two unverified Win32 claims** (does the grant survive the
  unlock→first-focus time gap; does a human click truly void it) → **needs a disposable spike** before any
  commitment. This is the *actual* critical path for truly-unattended `/autogoal` action.
- **`/autogoal`:** the agent-side goal loop that composes these tools + a notification MCP. Agent-layer,
  separate from this repo. Build **after** this toolset is complete.
