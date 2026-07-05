# FlaUI.Mcp — User-State Presence (SP-B) — Design

**Date:** 2026-07-05
**Status:** Draft (SP-B — depends on SP-A "Human-Attention Toolset")
**Origin:** Consumer + user-driven. Long-running / semi-attended jobs (e.g. "monitor my email while I
watch a movie") need the agent to escalate *how* it signals a human based on **how present the human is**
— a flash when they're watching, speech when they've stepped back, a remote DM when they're gone.

This design was settled through an AGY-FIRST consult (cascade `0cb93949`) that reconciled a prior
contradiction and endorsed the exact shape below; §7 records what SP-A had dropped and why it is safely
reconsidered here.

## 1. The idea & the boundary

The human's presence maps to an **escalation ladder**:

| Agent-derived state | Meaning | Channels the agent fires |
|---|---|---|
| **watching** | agent's target window is foreground **and** recent input activity | visual (flash) |
| **working** | recent input activity **but** the agent's target is **not** foreground (human busy elsewhere — the movie player is foreground, not the email window) | visual |
| **nearby** | no input activity for **X** | visual + **tts** |
| **away** | no input activity for **Y** (Y > X, entered after `nearby`) | visual + tts + **DM (Telegram)** |

**Load-bearing decomposition (keeps the server a dumb sensor):** these four *named* states are **derived
by the AGENT**, not the server. They combine two dimensions:

- **Activity** (idle time) — the **server** exposes this as a **coarse enum** (`active / nearby / away`).
- **Focus** ("is *my* target the foreground window?") — the **agent already has this from SP-A**
  (`foregroundGained`, `currentForeground`, `desktop_wait_for_foreground`).

The agent combines them (`my-target-focused + active → watching`; `not-focused + active → working`;
`≥X → nearby`; `≥Y → away`) and orchestrates the channels. The server never needs to be told "which
window is the agent's target," and never fires tts/DM itself. **Brain in the agent; the server is a dumb
sensor + hands** — the same boundary as SP-A.

## 2. Scope

**In scope (SP-B — server side only):**
1. A **coarse activity-state sensor** (`active / nearby / away`) from `GetLastInputInfo`, exposed as a
   read-only signal — **never raw idle-milliseconds**.
2. Gating behind a new human-only CLI verb **`flaui-mcp presence on|off`** (off by default), mirroring
   `autosound`/`overlay`.
3. Configurable **X** (active→nearby) and **Y** (nearby→away) thresholds.

**Out of scope / elsewhere (see §7/§8):**
- The **watching/working/nearby/away derivation** and all **channel orchestration** (flash/tts/DM): the
  **agent** (`/autogoal`), composing this sensor with SP-A. Not server code.
- The **Telegram/DM** send: the **agent's own notification MCP**. The server does **no** outbound (SP-A
  §9.1 airgap preserved).
- SP-A's attention channels (flash/TTS/wait_for_foreground) — that is SP-A.

## 3. Component specifications

### 3.1 Coarse activity-state sensor

- **Source:** `GetLastInputInfo` (Win32) → milliseconds since the last physical mouse/keyboard input. This
  is a **local, read-only** query; no synthetic input, no network.
- **TickCount rollover (mandatory, AGY-AFTER panel R1, adversarial seat):** `GetLastInputInfo.dwTime` is a
  32-bit tick count that wraps every ~49.7 days of uptime. `idle = now - dwTime` computed with naive
  signed ints underflows at the wrap → the sensor jams in a wrong `active`/`away` state until reboot. Use
  rollover-safe math: prefer `GetTickCount64()` for `now` and widen, or do the subtraction in `unchecked`
  32-bit wraparound arithmetic against the same 32-bit tick source. Pin this in a headless test that feeds
  a near-wrap `dwTime`.
- **Discretization (mandatory — privacy, AGY-FIRST folded):** the raw idle-ms is **never returned**. It is
  bucketed into a coarse enum:
  - `active` — idle < X
  - `nearby` — X ≤ idle < Y
  - `away` — idle ≥ Y
  Rationale: raw idle-ms is a high-frequency behavioral-biometric stream (keystroke cadence, mouse
  hesitation) that would leak the human's physical micro-profile to the LLM provider. The coarse enum
  delivers the semantic escalation triggers the agent needs while discretizing the biometric away.
- **Exposure (UNIFIED non-polymorphic schema — AGY-AFTER panel R1, API seat):** a **read-only** tool,
  working name **`desktop_user_state`**, always returning the **same shape**:
  `{ enabled: bool, activity: "active" | "nearby" | "away" | null }`. When presence is on →
  `{ enabled:true, activity:"active"|… }`; when off → `{ enabled:false, activity:null }`. A jagged shape
  (`{activity}` vs `{enabled:false}`) trips LLM/JSON-schema parsers — the shape must be invariant. Read-only
  ⇒ **lease-exempt**.
- **Thresholds:** `X` and `Y` are configurable (CLI args on `presence on`, e.g.
  `presence on --nearby-secs=60 --away-secs=300`), with sane defaults (plan-level constants). `Y > X`
  enforced; invalid → refuse.

### 3.2 `flaui-mcp presence on|off` (the opt-in gate)

- **Human-controlled CLI verb only**, modeled exactly on the existing `flaui-mcp overlay on|off` /
  `autosound on|off`: `presence on` re-registers the server across client configs with a `--presence`
  flag (+ optional threshold args); `off` removes it. **Off by default** — a default deployment exposes
  **zero** presence telemetry; headless CI leaves it off; a human-in-the-loop unattended job opts in
  knowingly. **The agent can never enable it** — presence is the human's own telemetry, human-owned
  consent (this also denies a prompt-injected agent the ability to switch the sensor on).
- **When off:** `desktop_user_state` returns `{ enabled: false }` (no activity data computed or returned).
- **Non-destructive config merge (mandatory, AGY-AFTER panel R1, ops seat — cross-cutting):** the CLI now
  manages **three** independent flags (`--overlay`, `--autosound`, `--presence`). `presence on|off` MUST
  parse the client config's existing `args` array and inject/remove **only** `--presence`, preserving
  sibling flags — a naive array rewrite would silently wipe the user's `autosound`/`overlay` settings. This
  is a shared requirement for all three verbs (retroactively covers SP-A's `autosound`); the plan should
  use one non-destructive config-updater.
- **`on` applies on reconnect; `off` must be IMMEDIATE (AGY-AFTER panel R1, UX seat — consent boundary):**
  turning presence *on* can wait for the next `/mcp` reconnect (like `autosound`/`overlay`). But turning it
  *off* is a **privacy-consent revocation** — the human expects telemetry to stop *now*, not at
  session-end. Because MCP clients don't hot-reload launch args, `--presence` alone can't be revoked live.
  Resolve by making the **running server read the enabled-state LIVE per query** from a small state file
  the CLI writes (mirroring the existing **file-based lease** mechanism) — so `desktop_user_state` checks
  the file each call and returns `{enabled:false, activity:null}` the instant the file flips, with no
  reconnect and no killing the process. (Preferred over agy's "CLI taskkills the server" — a live-checked
  flag is cleaner and non-disruptive. The `--presence` launch flag still sets the *default*; the file is
  the live override.)

## 4. Security & privacy posture (what makes this safe)

- **No real security is lost by exposing presence, and none is bought by hiding it (AGY-FIRST reconciled):**
  a prompt-injected agent driving flaui-mcp almost always also has a shell (e.g. Claude Code + Bash) and
  can poll `GetLastInputInfo` directly in two lines of PowerShell. Withholding it from the MCP API only
  hampers *benign* agents. (For the rarer shell-less agent, the opt-in default-off flag leaves the sensor
  absent entirely.)
- **Privacy is protected by the coarse enum:** no raw idle-ms ⇒ no keystroke-cadence biometric leak.
- **Consent is explicit and human-owned:** off by default; only a human CLI verb enables it.
- **Airgap preserved:** the server does NO outbound; the `away`→DM rung is the *agent's* notification MCP.
- **Server stays dumb:** it exposes one coarse read-only enum; the state-machine derivation and channel
  orchestration are the agent's.

## 5. How the agent uses it (informative — not server code)

The agent (`/autogoal`) polls `desktop_user_state` alongside SP-A's focus info and derives the ladder:
`active + my-target-foreground → watching (flash)`; `active + not-foreground → working (flash)`;
`nearby → nearby (flash + speak via autosound)`; `away → away (flash + speak + DM via its notification
MCP)`. The server contributes only the `active/nearby/away` axis.

## 6. Testing

- **Headless (`Category!=Desktop`):** the ms→enum bucketing (boundary values around X and Y), the
  `Y > X` validation, and the enabled/disabled gating are pure and unit-testable by injecting an idle-ms
  value (abstract `GetLastInputInfo` behind a seam so tests supply the number — do **not** call the real
  Win32 in a headless unit test).
- **Desktop (`Category=Desktop`, console-only):** real `GetLastInputInfo` transitions `active→nearby→away`
  as the console sits idle; `presence off` → `{ enabled:false }`. Validated at a console smoke.

## 7. Relationship to SP-A (the reconsidered drop)

SP-A §9.3 **dropped `userIdleMs`** as a "wait-for-the-guard" evasion sensor. SP-B **deliberately
reconsiders** that on the AGY-FIRST reconciliation: the evasion concern is moot (a shell-capable agent
reads `GetLastInputInfo` anyway), so the drop bought no real security. SP-B re-adds presence in the
**safe** form the original drop lacked: a **coarse enum** (privacy), **opt-in default-off** (consent),
**agent-derives-and-orchestrates** (boundary), **no server outbound** (airgap). SP-A's §9.3 note should be
updated to point here rather than read as an absolute drop.

## 8. Exhaustiveness self-audit (author)

- Every in-scope item (§2.1–2.3) has a §3 contract. ✅
- AGY-FIRST folds pinned: coarse-enum-not-raw-ms → §3.1/§4; opt-in default-off flag → §3.2/§4; DM
  agent-side / airgap → §2/§4; agent-derives-states (server stays dumb) → §1/§2/§5. ✅
- **Deferred-to-plan (named):** default X/Y values (§3.1); exact `desktop_user_state` field names /
  disabled-shape (§3.1 — `{activity}` / `{enabled:false}` proposed, confirm at plan); `--presence` DI /
  registration wiring mirroring `--autosound` (§3.2); the `GetLastInputInfo` seam for headless testing
  (§6).
- **No placeholders / TBD.** Open items above are constants/wiring, not design forks.

## 9. Follow-on

- The agent-side derivation + orchestration + DM belongs to **`/autogoal`** (agent layer, separate repo).
- SP-B build order: it is a server tool `/autogoal` consumes, sibling to SP-A; either may ship first, but
  the SP-A `IAttentionSignal`/gate work and SP-B presence sensor are independent enough to build in
  parallel tasks. (Plan sequencing decided in writing-plans.)
