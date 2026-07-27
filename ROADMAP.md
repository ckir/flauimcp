# FlaUI.Mcp Roadmap

Design spec: [`docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md`](docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md)

## Status: v1 feature-complete (2026-07-11, v0.13.0)

The full phased v1 plan has shipped — window management, hybrid perception (a11y tree +
screenshot/coordinates), pattern-based interaction, synthetic input behind the safety stack,
ref-resolution hardening, direct `desktop_find`, push event streaming, vision/opaque-app OCR + wake,
first-class `selector` targeting, inline window handles, session-hygiene healing, the human-attention
toolset, coarse presence, and background Windows-Terminal-tab reading. The tool surface an agent needs
to *perceive and drive an arbitrary Windows desktop app* is present and dogfooded.

The roadmap therefore pivots from **"phases toward v1"** to **two forward tracks plus a formal
drop-list.** Track A finishes the road to a stamped **v1.0**; Track B is curated post-v1.0 features.

## v1 scope (in the spec)

General agent control of arbitrary Windows desktop apps via FlaUI/UIA3 + the
official MCP C# SDK. Hybrid perception (a11y tree + screenshot/coordinates),
explicit multi-window handles, stdio + HTTP transports, option-C ref engine.

**Resilience cornerstones:** split query/action STA dispatcher (blocking-`Invoke`
can't freeze the server), popup grafting into snapshots, per-snapshot ref
scoping, connection-lifecycle cleanup, DPI-aware coordinate contract.

---

## Track A — v1.0 Release Candidate Path

**Completing Track A is what stamps the official v1.0.** These are productionization/trust items, not
features: they make the already-complete v1 surface *provably correct* and *trusted at install*. Ordered.

### A1 — Continuous interactive CI (Desktop/UIA + synthetic input) — **DO FIRST**

Today the green CI badge proves only the *headless* half; the `Category=Desktop` suite — UIA + real
`SendInput`, the product's entire reason to exist — is maintainer-run at manual smoke time. A regression
in real interaction behavior is not caught continuously. This is the single biggest correctness blind
spot, and it gates everything else: sign and ship only a tool that CI proves works.

**Split (agy-consulted, user-decided 2026-07-11): A1a before A1b** — ship the test hygiene as its own
green-locally increment first, then stand up the runner. Fixing flakes *inside* the runner work creates a
"two variables" problem: a red build is then ambiguous between a bad test and a bad runner environment.

#### A1a — Desktop suite reliably green **locally** (test hygiene) — the increment to build first

Pure test-harness work against code that exists today; no infra, no hardware. Definition of done: the
whole `Category=Desktop` suite runs reliably green on a connected+leased dev console.

- **`InputToolsTests` harness — ALREADY FIXED (`d84fedf`, 2026-07-03).** The Phase 7 §9 "known-broken
  harness" note is stale: the `RefForAid` failure was resolved same-day by snapshotting with
  `FullProperties=true` (which emits the `aid=` tokens `RefForAid` scans). Remaining A1a work here is
  only to *validate* it green on a connected+leased console (needs the physical console + a lease).
- **`TerminalTabE2ETests` title-settle flake — FIXED (this increment).** The independent restore-verify
  hard-asserted after only a 5s bound on WT's async caption repaint; widened to the discovery poll's
  proven 15s so the async-settle tail can't flake it under load.

#### A1b — Unattended interactive runner (infra) — **DEFERRED pending a ~$150 mini-PC** (decided 2026-07-11)

**Decision (agy-consulted divergent pass + user, 2026-07-11):** the maintainer has only a daily-driver
machine. Forcing a focus-stealing `Category=Desktop` suite onto a working box is a path of endless
friction, so A1b is **deferred until a cheap dedicated box exists** (~$150 mini-PC). At that point the
plan-of-record below applies as-is. **v1.0 consequence:** completing Track A stamps v1.0, so v1.0 is now
gated on this hardware — reconsider whether A1b must block v1.0 or can move to a v1.0.x follow-on (open).

<details><summary>Daily-driver alternatives considered and parked (so a future session need not re-derive)</summary>

- **V2 — GHA self-hosted runner + manual `workflow_dispatch`** (leanest, ~15 min): runner on the daily
  driver, Desktop job runs only when manually dispatched against a **fresh clone**; you trigger it when
  stepping away. Tests the *committed* tree (PR = source of truth). Not auto-continuous; risk of an
  accidental mid-work input seize if left armed.
- **V1 — Hyper-V VM in *Basic* Session Mode** (true continuous, ~2 h + perpetual 4–8 GB RAM): a runner
  inside a local VM. **Key fact:** *Basic* (not *Enhanced*/RDP) session keeps a **synthetic physical
  console** (virtual framebuffer), so `SendInput` inside the VM works fully, decoupled from the host
  input even when minimized. This is the one way to get continuous gating on a single machine — costs a
  second OS running forever.
- **Rejected — local script gating** (agy's challenge, correct): a local `dotnet test` script tests the
  **dirty working tree, not the commit** → you pass locally, tag, then find an un-`git add`ed file.
  Whatever ships MUST run via a GHA runner against a fresh clone, never a working-tree script.

</details>

When the box exists — the **sound** unattended approach (feasibility validated 2026-07-03): `SendInput` works in any
*connected, unlocked* session, so a local connected+leased run is already a legitimate pre-tag gate.
For unattended: Sysinternals **Autologon → box boots into an unlocked physical console → run the CI agent
as an interactive startup app** (never a Session-0 Windows service). `tscon /dest:console` is **not**
sound (resolution collapse breaks bounds/visibility tests; WS2022/Win11 harden against `tscon` hijacking).

Plan-of-record for the A1b spec (agy-consulted 2026-07-11):
- **Runner model:** self-hosted **GitHub Actions** runner launched via the **startup folder**
  (`shell:startup`, *not* a service — so it inherits the Autologon unlocked session), labeled e.g.
  `[self-hosted, windows-desktop-automation]` so existing workflows fan a `Category=Desktop` job onto it.
  Keeps all signal on the PR ("PR is source of truth"), vs. an out-of-band nightly task hiding failures
  in local logs.
- **Gating posture:** **informational first** (runs on PRs, non-blocking), promote to a *required* check
  only after a few weeks of proven-green — real-`SendInput` UI tests are inherently flake-prone (focus
  theft, update reboots, timing jitter), so day-one blocking self-inflicts merge blocks.
- **Host:** a **dedicated box/VM, not the maintainer's daily driver** (a push would seize the mouse
  mid-work; an RDP disconnect locks the session → `SendInput` silently drops). Physical headless box
  needs an **HDMI dummy plug** (else Windows collapses to 640×480 and breaks bounds tests); manage a VM
  via the Hyper-V console, never RDP.
- **Host hardening (runner reliability):** disable screen-sleep + lock-on-idle (or PowerToys Awake) — an
  idle lock after ~15 min fails all subsequent jobs; and debloat / disable notifications (Windows Update,
  Edge "make default", AV scans steal focus mid-test).
- Complements the deferred "Full DPI × OS × integrity test matrix in CI" (kept in v2 — CI runners are
  single-DPI/non-elevated; the DPI matrix stays a documented manual gate).

### A2 — Code signing the distributed exe

An unsigned, self-extracting binary that **synthesizes input and configures agents** is a rough
first-touch trust barrier for a *security* tool — a strong AV/SmartScreen trigger. Authenticode signing
(cert) materially improves the install experience and unblocks locked-down environments. v1 ships
unsigned + checksum + "Run anyway" docs; signing is the last thing before the v1.0 stamp.

> **Ordering note (agy-consulted, user-decided 2026-07-11):** A1 before A2 — *guarantee the tool works
> (CI) before asking the OS to vouch for it (signing).* The exception that would flip it: a hard adoption
> wall where target users literally cannot run an unsigned exe. Not the case today.

---

## Track B — curated post-v1.0 features

Genuine feature work, sequenced after v1.0. Each gets its own spec → plan → implementation cycle.

### B1 — SP-C: legitimate foreground raise

The natural completion of the SP-A attention line: a *sanctioned* way to actually bring a window to the
foreground when that is the correct outcome, versus today's flash-and-wait handshake. Specced as the
SP-A follow-on ([`SP-A design`](docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md));
backlog only, not yet planned.

### B2 — System-tray pseudo-window (`desktop_open_tray`)

Walk `Shell_TrayWnd` / `NotifyIconOverflowWindow` to expose the notification area as a driveable
surface. Useful for machine management (Wi-Fi, Docker, VPN trays). A special-case surface beyond core
app control — **prioritized** within Track B.

### B3 — OLE/COM file drop (`desktop_drop_files`) — **low**

Synthesize `IDataObject` / `IDropTarget` to drop files into apps (the real need: uploading files into a
UI). Complex, error-prone COM injection; rated **YAGNI-High**. Kept on the list but lowest priority.

### B4 — Reduce the visible-switch cost of WT multi-tab app discovery — **low / opportunistic**

*Dogfooding note, 2026-07-26 — logged honestly after a driver error, NOT a missing-capability claim.*
The capability to find a CLI app hiding in a **generic-titled** Windows Terminal tab **already ships**
(v0.13.0 `desktop_read_terminal_tab` + the `driving-flaui-mcp` growth recipe *"tab title is a HINT, never
a filter"*). Confirmed live: a consuming agent (Claude, via the clavity-ls ↔ Antigravity pairing) located
the exact `agy` session clavity-ls was driving — in a tab titled bare `C:\WINDOWS\system32\cmd.exe` —
by reading each candidate tab (`PONG-9F2C` diagnostic round-trip). Two agy/Gemini instances were running
in *separate* bare-`cmd.exe` tabs; only a third titled its tab (braille spinner + task). The initial
"can't find it" was a **driver error** — trusting titles instead of reading candidates, the exact trap
the skill's trap-table names — **not a product gap.** No skill change needed; the recipe is correct and
was simply not applied.

**Genuine residual (modest, real-but-hard):** the recipe costs **one visible tab-switch per candidate**
(`read_terminal_tab` selects → reads → restores — `restoreConfidence` even dropped to `reduced` here),
which flickers the human's screen and is O(generic-tabs). A less-disruptive identification path would
remove both the flicker and the N-call cost:
- map each `TabItem` to its **hosted process/pid** (so "the agy tab" is found by process, no buffer read
  or switch at all), and/or
- a single composite that scans **all** tab buffers for a query string and returns the matching
  `tabIndex`.

Both are constrained by WT's UIA model (only the *active* tab's `Custom→Text` buffer is populated, so any
buffer scan must still select each tab). Low priority — the shipped recipe is sufficient; this only trims
disruption. **Driver-side lesson (already in the skill, re-underscored):** read EVERY candidate tab; a
generic launcher title (`cmd.exe`/`PowerShell`) is never proof of a bare shell.

**Recommended approach (for a future brainstorm — not yet decided):**

- **Primary — process-based tab identity, ZERO switch.** WT spawns a ConPTY child process per tab/pane;
  the real program (agy = a `gemini`/node process; `pwsh`; `cmd`; …) is a **descendant of the
  `WindowsTerminal.exe` PID.** Enumerate that descendant tree (Win32 `CreateToolhelp32Snapshot` +
  parent-PID walk, command line via `NtQueryInformationProcess`/WMI) to answer the agent's ACTUAL
  question — *"is app X running under this WT, how many instances, and each one's command line?"* —
  **without selecting a single tab (no flicker).** Expose as e.g.
  `desktop_list_terminal_processes {window} → [{pid, name, commandLine, …}]`. Standalone win even if the
  last mile below is hard.
  - **Hard last mile:** PID → **tab ordinal.** ConPTY doesn't obviously expose which tab hosts which
    child, and UIA `TabItem`s carry no PID. Investigate: correlate the active pane's UIA `ProcessId`
    after a switch (defeats "no switch"), read WT's own session state, or **accept process-level identity
    without the ordinal** and only switch when interaction (not identification) is actually required.
- **Fallback — single composite `desktop_find_terminal_tab {window, query}`** that owns the
  select→read→match→restore loop internally and returns the matching `tabIndex` in ONE call. Does NOT
  remove the flicker (a buffer scan must select each tab), but collapses N agent calls to 1 and
  centralizes the `restoreConfidence` handling. Ship if the ordinal last-mile is infeasible.
- **Cross-tool synergy to weigh:** the clavity driver already knows agy's LS port/PID (it reads
  `cli.log`); if the driver passes that PID, the process-list approach resolves *"which WT hosts agy"*
  directly — the highest-leverage version pairs `desktop_list_terminal_processes` (flaui) with the driver
  supplying the target PID (clavity).
- **Lean going in:** ship `desktop_list_terminal_processes` **first** — a pure, non-disruptive win that
  unblocks "find agy without flicker"; treat the ordinal-precise composite as a follow-on gated on a real
  need to *interact with* (not merely identify) a background tab.

---

## 🔴 DEFECT (v0.18.0) — the activation hook does not fire. M1 is inert as shipped.

*Found 2026-07-27 by the observational check, immediately after releasing 0.18.0 and restarting.
**M0, M2 and M3 are unaffected and working** — only the SessionStart hook (M1) is dead.*

**Reproduce:** in a directory with no `.claude/settings.json` (so the plugin hook is the only possible
source), start a session and ask whether the context contains text beginning *"flaui-mcp is installed"*.
Answer: **no**. Confirmed twice.

**Everything we control is correct, which is why every gate passed:**

| Check | Result |
|---|---|
| `flaui-mcp status` | `Activation hook: wired (SessionStart -> flaui-mcp activation-payload)` |
| `claude plugin list` | `flaui-mcp@flaui-mcp-marketplace` v0.18.0, **enabled** |
| cached `hooks/hooks.json` | correct SessionStart entry, matcher `startup\|clear\|compact`, installed exe path |
| the command, run standalone | `bash -c '<exact string>'` → valid JSON, exit 0 |
| the plugin's skills | load correctly (M0/M2/M3 all live) |

**Root cause: LIKELY NOT OURS — plugin hooks appear to require a full Claude Code restart.**

A throwaway diagnostic plugin (`hooktest`) was built mirroring `agy-autotrain`'s exact layout —
marketplace root with `source: "./plugins/hooktest"`, plugin root with `.claude-plugin/plugin.json`,
and a `bash "${CLAUDE_PLUGIN_ROOT}/hooks/marker.sh"` SessionStart command. It registered cleanly,
shows **enabled**, and its cache copy contains both `hooks/hooks.json` and the script.
**Its hook does not fire either.**

The pattern across the whole plugin cache:

| plugin | cached | hook fires |
|---|---|---|
| `clavity-agy-autotrain` | 2026-07-13 | yes |
| `clavity-dotnet` | 2026-07-24 | yes |
| `flaui-mcp-marketplace` | 2026-07-19 (upgraded to 0.18.0 today) | **no** |
| `hooktest-mp` | today, minutes before the test | **no** |

Plugins registered in earlier client lifetimes fire; plugins installed or upgraded during the current
one do not — regardless of layout, command form, or manifest placement. So the structural differences
listed above are almost certainly red herrings, and the shipped artifact may be correct.

**Prediction to confirm:** after a FULL Claude Code restart (quit the client, not just a new session),
both `flaui-mcp`'s activation hook and the `hooktest` marker should fire. If both do, this is a client
registration-timing behaviour, not a product defect, and the only real bug here is that our docs and
`status` imply a freshly-installed hook is immediately live.

**Refuted along the way — do not retry:** a missing `.claude-plugin/plugin.json` (added to both the
staging dir and the cache; no change); a malformed command string (runs correctly under `bash` and
`sh`, exit 0); a `hooks` declaration in the manifest (agy-autotrain has none either); marketplace/plugin
root separation (the test plugin replicates it and still does not fire).

> **Cleanup owed:** the `hooktest` diagnostic plugin is still registered as a control for the restart
> test. Remove with
> `claude plugin uninstall hooktest@hooktest-mp` and `claude plugin marketplace remove hooktest-mp`.

**Also wrong, and shipped:** `docs/operator-manual.md` states *"Claude references the staging dir in
place; it does not copy it."* It does copy it —
`~/.claude/plugins/installed_plugins.json` records
`installPath: ~/.claude/plugins/cache/flaui-mcp-marketplace/flaui-mcp/<version>`. Editing the staging dir
has no effect until reinstall. Pre-existing claim from the installer rework; repeated here unverified.

**Lesson for the gate, not just the bug:** every test we wrote asserts what the installer *stages*.
None asserts what the client *executes*. `status` reporting "wired" is our own bookkeeping. Only a real
session can close that gap, and that is the one check that could not run until after release.

---

## Agent-adoption reliability — make correct usage the STRUCTURAL default ✅ DELIVERED (2026-07-27)

> **Shipped on `feat/agent-adoption-activation`.** Both AAs are implemented as four mechanisms, plus a
> packaging precondition that turned out to block the whole thing: `PluginArtifactWriter` staged only
> four files, so **the plugin shipped no hooks at all** — every hook mechanism reached 0% of installed
> users. Fixing distribution came first.
>
> | | Mechanism | Result |
> |---|---|---|
> | **M0** | Fix the broken tool-load line | The documented `ToolSearch` line named `mcp__flaui-mcp__*`, but under plugin registration the tools are `mcp__plugin_flaui-mcp_flaui-mcp__*` — it **matched nothing**. Now lists both prefixes; `select:` ignores names it cannot match. |
> | **M1** | `SessionStart` activation hook | Compiled-in payload + `activation-payload` verb + generated `hooks.json`. Fires on `startup\|clear\|compact`. |
> | **M2** | Decision-point frontmatter | Rewritten question-shaped: *what is on screen, is an app responding, what a background terminal shows*. |
> | **M3** | Traps in tool descriptions | The launcher-not-the-program trap stated as an **imperative** in `desktop_list_windows` + `desktop_read_terminal_tab`. |
>
> **The AA2 hypothesis above was half right.** M3 is indeed the cheapest, highest-coverage lever. But
> AA2's other half — the *"read me fresh before driving"* gate — was **rejected as circular**: an agent
> that doesn't read the skill won't read a notice telling it to. M0 also had to come first: without it,
> every other mechanism pointed at a load line that returned nothing.
>
> **Measured, so it stays settled:** the activation hook costs ~0.5 s and blocks the first turn, but that
> is **below the noise floor** of a ~35 s session start. A `cmd /c type` variant (62 ms) and a light Rust
> binary (62 ms) measure identically — the floor is Windows process creation, not the runtime — so the
> hook is **not worth optimising in any language**. See spec §5.2.
>
> ▶ Still open: the observational check (does an agent reach for the tools unprompted?) is a dogfooding
> gate, not a test, and can only be judged in live use.

*Motivated by a live dogfooding failure, 2026-07-26: a capable consuming agent (Claude) had flaui-mcp
available and the recipe documented, yet (a) did NOT reach for the tool — it asked the human to eyeball
agy's console instead — and (b) drove on a **truncated/remembered** copy of the skill, missed the
growth-region recipe, and "gave up" on an already-solved problem (finding a CLI agent in a bare-titled
Windows Terminal tab). Neither is a one-off agent quirk. Both are **activation/discoverability gaps the
product can close.** The current design silently assumes the agent will discover the tool and read the
skill as-is; when that assumption fails, the tool goes unused or misused with no signal. The fix is
structural — don't rely on agent discipline; make the right move the path of least resistance and the
wrong assumption impossible to hold. Each gets its own spec.*

### AA1 — Activation: surface the tool at the DECISION point, not only after the agent commits to driving

The agent under-reaches because the tools are **deferred** (must be `ToolSearch`-loaded → out of sight,
out of mind) and the skill activates on *"driving flaui-mcp"* — i.e. only once the agent has ALREADY
decided to drive. The moment that actually matters is earlier: *"I need to see/verify on-screen state,
confirm a message reached another app's console, or check whether an app is running."* Investigate:
- Broaden the skill's frontmatter `description` to trigger at that **decision point** (need-to-perceive
  the desktop / verify another app's state), not just the **action point** (already-driving).
- Ship a lightweight **hook** in the flaui-mcp plugin (precedent: agy-autotrain ships reminder hooks)
  that nudges *"verify on-screen state with flaui-mcp instead of asking the human"* when the agent is
  about to ask a human to visually confirm desktop state — plus a SessionStart one-liner that the desktop
  tools exist and when to load them.

### AA2 — Skill-freshness: make a stale/partial skill copy unable to cause the failure

The agent trusted a remembered/truncated skill instead of reading it as-is — and the skill's most
load-bearing trap (*"a WT tab title is a launcher HINT, never a filter — read every candidate"*) lives in
prose + the frequently-updated GROWTH region: exactly the parts most likely to be stale or cut from a
compacted context. Two complementary fixes:
- **A prominent "read me fresh before driving" gate** at the skill top: skills evolve and a context copy
  may be truncated — Read the current file (incl. its growth region) before driving; never rely on a
  remembered version. Optionally a **first-use PreToolUse hook** on the `desktop_*` tools that reminds
  once per session.
- **Hoist the highest-leverage traps into the TOOL DESCRIPTIONS** — the one surface an agent cannot skip
  or hold stale, because descriptions are always in-context whenever the tool is loaded. `desktop_list_windows`
  / `desktop_read_terminal_tab` should say, *in the description itself:* a WT tab title is the launcher,
  not the program — a bare `cmd.exe`/`PowerShell` tab may hide a CLI agent; read every candidate. An agent
  that never opened the skill still can't miss it. (Design tension: description length/noise vs.
  guaranteed visibility — encode only the few genuinely trap-class facts, not the whole skill.)

Both AAs serve one principle: **the failure was structural, not personal** — so close it in the product
(activation + un-skippable placement of load-bearing facts) rather than re-teaching each new agent
session. A future session should measure which lever moves the needle most (the honest hypothesis:
AA2's "traps in tool descriptions" is the cheapest, highest-coverage fix, since it needs neither a hook
nor the agent opening the skill).

---

## Opportunistic hardening (fold in when adjacent)

Not scheduled on their own — pick up when touching the surrounding code. None block anything.

- **Phase 3b-1 perception leftovers:** occlusion-aware capture (`PrintWindow`, vs the current
  focus-first screen-scrape); full-desktop *per-field* redaction for non-denied windows (denylist
  whole-window refuse is the floor); snapshot/diff *value*-change detection (needs opt-in per-node value
  reads — omitted from the default walk for STA perf); `desktop_wait_for_stable` scope-by-ref; the
  documented diff-identity limit on anonymous virtualized recycled rows (diff those by value/text).
- **Phase 7.1 clipboard:** delayed-render `WM_RENDERFORMAT` clipboard for a precise paste-consumption
  signal (vs the current best-effort restore-on-confirmed-consumption).
- **v0.13.0 micro follow-ups:** surface each `TabItem`'s `tabIndex` in `desktop_snapshot` (or have
  `desktop_read_terminal_tab` echo the `index→title` map) so ordinal selection isn't hand-counted
  (consumer-UX, from live smoke — design fork, agy-first before implementing); tool-level JSON-shape
  tests asserting `truncatedFrom` (`desktop_get_text`) and `Hint` (`desktop_list_windows`) surface
  through the anonymous projections.
- **Micro belt-and-suspenders:** redact descriptor `Name` for `IsPassword` controls (`Name` is empty
  for conformant password controls today, so no secret is stored — pure defense-in-depth).

---

## Formally dropped

Cut to keep scope honest. Dropping these clarifies **what this tool is not** — an automation *bridge*,
not a window manager, a security sandbox, or a multi-tenant remoting server.

| Dropped | Why |
| --- | --- |
| **Phase 10 #3** (third consumer-ergonomics change) | The Phase-10 spec itself calls it a likely-drop, "revisit after #2." #1 and #2 shipped; #3 never earned a plan. |
| **Native AOT / exe shrink** | Blocked by FlaUI's runtime COM interop + the MCP SDK's reflection tool discovery + STJ reflection serialization (would need source-gen JSON + source-gen tool registration first). Low value for a once-installed dev tool. |
| **Window arrangement** (`desktop_arrange_windows` tile/cascade) | Cosmetic scope creep; `desktop_window_transform` + `desktop_list_windows includeBounds` cover the real needs. Not a window manager. |
| **Shell / system integration** (shell execute, taskbar pinning) | Scope creep beyond UI automation. Clipboard — the high-value piece — already shipped. |
| **Raw window messaging** (`SendMessage`/`PostMessage`) | Brittle footgun; MSAA is surfaced via `LegacyIAccessiblePattern` and the vision/coordinate path is the zero-UIA fallback. |
| **Elevated-app automation** (higher-integrity broker + IPC) | Security-sensitive; needs an elevated helper. v1 documents `ACCESS_DENIED_INTEGRITY` instead. Not a privilege-escalation surface. |
| **App allow/deny guardrails** | General control is the goal; per-app guardrails are an opt-in hardening posture, not core. |
| **HTTP/SSE remote reachability** | A multi-connection server driving one physical mouse/keyboard is a focus-steal mirage (agy-first, 2026-07-03). Push event streaming — the one thing that seemed to need it — was decoupled onto stdio in Phase 8. Remains only about driving a remote/headless box; not pursued. |
| **Recording / codegen** of action sequences | Convenience layer, not core capability. |

Still genuinely deferred (not dropped): **Full DPI × OS × integrity test matrix in CI** (manual gate
today — CI runners are single-DPI/non-elevated).

---

## Shipped (v1) — provenance

Kept as the historical record; each line is one shipped increment. Full semantics live in the design
spec and the per-phase specs under `docs/superpowers/specs/`.

**Core surface (Phases 1–10):**

- **Phase 1 — Foundation** ✅ v0.1.x — window/session management, split query/action STA dispatcher,
  option-C ref engine, 5 window tools.
- **Phase 2 — Perception** ✅ v0.2.0 — `desktop_snapshot` (a11y tree + popup grafting), perception-security
  floor (credential denylist, always-on `IsPassword` redaction, off-screen cull, never-elevated warn).
- **Phase 3a — Pattern interaction** ✅ v0.3.0 — 9 core pattern actions + `desktop_set_focus`, cross-STA
  ref resolution, `--read-only-mode` flag.
- **Phase 3b-1 — Perception completion (read-only)** ✅ v0.4.0 — `desktop_screenshot`, `desktop_get_bounds`,
  `desktop_snapshot_stats`/`_diff`, `desktop_wait_for`/`_stable`, `desktop_get_focused_element`,
  `desktop_list_windows includeBounds`/`zOrder`.
- **Phase 3b-2 — Structured patterns + clipboard** ✅ v0.5.0 — `desktop_get_grid_cell`/`grid_select`,
  `desktop_get_text`, `desktop_clipboard_get`/`set`.
- **Phase 4a — Input safety foundation** ✅ v0.6.0 — 3-seam set, `InputGuard` (deny-list + per-window
  budget + audit), file-backed time-lease CLI (`unlock`/`lock`), elevation hard-fail. No input tools yet.
- **Phase 4b — Synthetic input** ✅ v0.7.0 — `desktop_type`/`key`/`click`/`click_at`/`drag`,
  `desktop_input_status`, `desktop_set_caret`/`select_text_range` (real Win32 `SendInput`, spike-validated).
- **Phase 4b.1–4b.3 — Typing robustness** ✅ v0.7.1 / v0.7.2 / v0.7.5 — inter-key pacing; typed-text
  `verify`; ValuePattern-aware verify remedy (`canSetValue` + `recommendedFallbackTool`).
- **Phase 5a — Ref hardening (INV-8)** ✅ v0.7.3a — strict RuntimeId-only writes, fail-closed lenient
  reads, break-glass `FLAUI_MCP_REF_STRICT=off`.
- **Phase 5b — `desktop_find` + scoped diff** ✅ v0.7.3 (+ v0.7.4 null-Name hotfix) — direct element query
  without a full walk; `desktop_snapshot_diff scope=<ref>`.
- **Phase 6 — RefRegistry eviction on close** ✅ v0.7.6 — `WindowInvalidated` push + on-access liveness sweep.
- **Phase 7 — `desktop_paste_text`** ✅ v0.7.7 — atomic clipboard-preserving Ctrl+V for reactive editors.
- **Phase 8 — `desktop_watch`** ✅ v0.8.0 — UIA event streaming over stdio MCP notifications (+ `desktop_drain_events`
  buffered fallback); no HTTP/SSE.
- **Phase 9 — Vision & opaque-app access** ✅ v0.9.0 — `desktop_wake_accessibility`/`release`/`list_wakes`
  (Chromium/Electron UIA wake) + `desktop_find_text`/`wait_for_text` (on-box OCR targeting).
- **Phase 10 — Consumer ergonomics** ✅ — first-class `selector` targeting (v0.10.0); audit trace + GDI
  intent overlay (v0.10.1); opt-in inline window handles `includeHandles` (v0.11.0). (#3 dropped, above.)
- **Consumer-lens capstone** ✅ v0.7.3 — read-only enforcement as a structural invariant
  (`ToolReadOnlyInvariantTests` asserts every `[McpServerTool]` declares exactly one of
  `ReadOnly`/`Destructive` and every `Destructive` tool short-circuits to `WriteBlockedReadOnly`).

**Session hygiene (SP1–SP4):** *no tool leaves the human's session worse than it found it.*

- **SP1 — Audit + invariant** ✅ — found exactly one live gap (`window_transform minimize` orphan) + one
  low-severity observability gap.
- **SP2 — Harden the gap** ✅ v0.11.2 — shared `RestoreForegroundAfterCollapse` for close + minimize;
  `desktop_focus_window` `foregroundGained` signal.
- **SP3 — Chaos harness** ⏸ shelved (YAGNI — its trigger, a *second* hygiene gap, never fired; design on record).
- **SP4 — Session Sentinel** 🚫 retired — a 100× spike observed zero async re-orphans; a lease-less healer
  isn't justified. Known blind spot on record: apps taking >500 ms to close can outrun SP2's spin-wait (SP4 spec §6).

**Human-attention + presence:**

- **SP-A — Foreground-lock legibility** ✅ v0.12.0 — enriched `targetNotForeground`, `desktop_wait_for_foreground`,
  attention flash + opt-in `autosound`, long-lease risk gate.
  ([spec](docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md))
- **SP-B — User-state presence** ✅ v0.12.0 — read-only, opt-in `desktop_user_state` (coarse
  active/nearby/away; never raw idle-ms); `flaui-mcp presence on|off`.
  ([spec](docs/superpowers/specs/2026-07-05-flaui-mcp-user-state-presence-design.md))

**Background-tab terminal reading:**

- **WT tab reading** ✅ v0.13.0 — `desktop_read_terminal_tab` composite (select → settle → read sibling
  `Custom→Text` buffer → restore active tab; Destructive, read-only-gated); `desktop_get_text`
  `fromEnd`/`truncatedFrom`; `desktop_list_windows` multiplexer `Hint`; `driving-flaui-mcp` recipe rewrite.
  Incident-driven (a background agy CLI misdiagnosed as "headless").
  ([spec](docs/superpowers/specs/2026-07-10-windows-terminal-tab-reading-design.md))

**Tooling:** the `driving-flaui-mcp` dogfood skill ✅ — teaches an agent to inspect/drive the desktop via
the installed server, empirically grounded and extended each phase.

---

## Perception — known limitations (documented, not bugs)

Surfaced by the Phase 2 adversarial review; the perception security floors are defense-in-depth, not an
injection cure. These are stable, documented behaviors — reference, not backlog.

- **Denylist is process-coarse.** Matches by process name, so it misses *browser-embedded* password
  managers (`chrome://settings/passwords` is process `chrome`) and *UWP* apps whose PID resolves to
  `ApplicationFrameHost.exe`. Always-on `IsPassword` redaction is the field-level backstop; a full
  allowlist remains an opt-in hardened posture (deliberately not default).
- **"Reveal password" defeats redaction.** When an app swaps a password field to plaintext (`IsPassword`
  toggles false), the value is — by the app's own declaration — no longer a secret; nothing at our layer
  can re-mask it.
- **Electron/Chromium a11y is off by default.** Chromium exposes its full UIA tree only when a screen
  reader is detected or it's launched with `--force-renderer-accessibility`; otherwise a snapshot is one
  large `Document` node. `desktop_wake_accessibility` hydrates it on demand (Phase 9); typed text into
  Chromium editors garbles like the new Notepad — `desktop_paste_text` is the reliable path. WinUI 3 /
  WPF / Qt expose proper UIA and are unaffected.
- **Popup detection is class-name-based.** `FindOwnerPopups` recognizes Win32 (`#32768`), WPF
  (`HwndWrapper*`/`Popup`), and `Menu`; it misses WinForms/Qt/Electron overlay classes.
- **No occlusion awareness.** UIA reports `IsOffscreen` but not "visible-but-covered by another window"
  — see the Phase 3b-1 occlusion-aware-capture item in Opportunistic hardening.

## Notes

- Items originally marked "Both reviews" were independently flagged by both external reviewers
  (Grok + a second architect pass), raising confidence they were real.
- The v1 line was drawn to reach a **working, resilient server fast**, then add reactive/perception
  superpowers once the core was proven. With v1 feature-complete, the emphasis shifts to
  *provable correctness* (Track A) before new surface (Track B).
