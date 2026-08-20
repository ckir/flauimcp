# FlaUI.Mcp Roadmap

Design spec: [`docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md`](docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md)

## Status: v1 feature-complete (2026-07-11, v0.13.0) · **Track A complete — the v1.0 gate is met (2026-07-29)**

The full phased v1 plan has shipped — window management, hybrid perception (a11y tree +
screenshot/coordinates), pattern-based interaction, synthetic input behind the safety stack,
ref-resolution hardening, direct `desktop_find`, push event streaming, vision/opaque-app OCR + wake,
first-class `selector` targeting, inline window handles, session-hygiene healing, the human-attention
toolset, coarse presence, and background Windows-Terminal-tab reading. The tool surface an agent needs
to *perceive and drive an arbitrary Windows desktop app* is present and dogfooded.

The roadmap therefore pivots from **"phases toward v1"** to **two forward tracks plus a formal
drop-list.** Track A finishes the road to a stamped **v1.0**; Track B is curated post-v1.0 features.

**Track A closed on 2026-07-29** — A1a delivered, and A1b (unattended runner) and A2 (code signing) were
both dropped rather than deferred. Two consequences worth stating plainly, because they are permanent
stances and not waiting states: **the product ships unsigned**, and **the Desktop suite is gated
locally, not in CI**. Everything below Track A is now post-v1.0 work.

## v1 scope (in the spec)

General agent control of arbitrary Windows desktop apps via FlaUI/UIA3 + the
official MCP C# SDK. Hybrid perception (a11y tree + screenshot/coordinates),
explicit multi-window handles, stdio + HTTP transports, option-C ref engine.

**Resilience cornerstones:** split query/action STA dispatcher (blocking-`Invoke`
can't freeze the server), popup grafting into snapshots, per-snapshot ref
scoping, connection-lifecycle cleanup, DPI-aware coordinate contract.

---

## Track A — v1.0 Release Candidate Path

**Completing Track A is what stamps the official v1.0.** This is a productionization item, not a feature:
it makes the already-complete v1 surface *provably correct*.

### A1 — Continuous interactive CI (Desktop/UIA + synthetic input)

The green CI badge proves only the *headless* half; the `Category=Desktop` suite — UIA + real
`SendInput`, the product's entire reason to exist — is maintainer-run at manual smoke time. **A1a is the
whole of Track A, and therefore the whole of the v1.0 gate.**

#### A1a — Desktop suite reliably green **locally** ✅ **DELIVERED (2026-07-29)**

**Definition of done met: `109 passed / 109`, plus the `PopupGrafting` half `1/1`, `0 skipped`,** run
twice consecutively on a physical console with an ambient VS Code present — so the hermeticity case is
the one that was measured, not a clean-room. Suite time 9.99 min.

> **⚠️ The previous entry here was WRONG, and the error is worth keeping.** It said the remaining work
> was *"only to **validate** it green"*. The first real run measured **6 failed / 102 passed / 108**.
> This was repair work, not validation. A claim that a suite is green because its known blockers were
> fixed is not evidence that it is green — only running it is.

The six, and only one was the flake the entry assumed:

| # | Test | Actual cause |
|---|---|---|
| 1 | `DesktopWakeTests.Waking_hydrates_the_tree_while_held` | Chromium hydration is a **ramp, not a step**; the fixed `Delay(1500)` sampled inside it |
| 2 | `PresenceDesktopTests.Real_idle_source_reports_active_right_after_input` | Asserted a **human** had typed within 60s while synthesising nothing — so it failed whenever nobody had touched the machine, which is the state the suite requires |
| 3 | `WaitForStableTests.Structure_is_stable_despite_a_live_ticker` | **Arithmetic, not flakiness.** One full walk costs ~3.0s, so `wait_for_stable` floors at ~12.5s against a 5000ms budget — unreachable by 2.5× even against a static tree |
| 4,5 | `FindTests` ×2 | The tests confused `AutomationId` with **Name**. A `ListBoxItem`'s UIA Name is its `Content`, so items were `A/B/C/NamedOnly` and `contains "Item"` matched nothing. **Wrong since authored** — Desktop is not a CI job, so nothing caught it |
| 6 | `WaitForTests.Delayed_control_becomes_satisfied_with_a_ref` | The fixture **overflowed its own clamped window**, so the bottom of the UIA tree was spatially culled: the second `DupRow` GroupBox was gone entirely and `Row1Btn`'s text survived on a 2px sliver |

**#6 was the root defect** — one ~890 DIP column in a window the work area clamps. Fixed once, at the
fixture, by a three-column layout against a two-dimensional budget derived from a 1366×768 @150% floor
machine, with the window clamped to `Min(constant, WorkArea)` in both axes. A new
`FixtureIntegrityTests` guard now fails loudly if any descendant escapes the window's bounds again.

**Three defects nobody was looking for** came out alongside: a test that **could not fail** (it asserted
only a timeout under a budget one poll already overran, so it would have passed against a frozen
window); a containment guard that would have **passed vacuously** if its tree walk threw; and teardown
that waited on the developer's **entire VS Code installation** — 4s → 59s per test, ~2.75 min per run.

**AGY-CAPSTONE: GREEN at round 7**, folding **21 verified defects out of two files**. In every round up
to the sixth, the *previous round's fixes* were a main source of the next round's findings. Roughly
three findings were refuted by measurement, and twice a correct finding arrived with a wrong or
incomplete fix — so each proposed fix was traced through its own case matrix before being committed.

**Still true and NOT a defect:** the suite is not in CI and requires a **physical console** (`SendInput`
does not deliver over RDP) plus an **input lease** (`flaui-mcp unlock --minutes N --allow-shells`), and
the operator must step away. `0 skipped` is part of the gate: several tests are `[SkippableFact]`
lease-guarded and xUnit exits 0 on a skip, so a lease-less run reports a clean exit while bypassing
every assertion that matters. Run it from the cockpit (`K`).

**Track A is therefore complete, and the v1.0 gate is met** — stamping v1.0 is a release decision, not a
remaining engineering task.

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

## ✅ CLOSED (v0.18.0) — the activation hook was never broken. Claude Code loads plugin hooks only at client startup.

*Opened 2026-07-27 as a 🔴 shipped defect; **closed the same day by the predicted experiment**.
M1 works. M0, M2 and M3 were never in doubt.*

**What looked like a defect:** immediately after releasing 0.18.0, the `SessionStart` activation hook did
not fire — across several new sessions, in a directory with no `.claude/settings.json`. Every gate we own
said green: `status` reported `wired`, `claude plugin list` showed v0.18.0 **enabled**, the cached
`hooks/hooks.json` carried the right entry and matcher, and the command ran standalone to valid JSON.

**What it actually was.** A throwaway control plugin (`hooktest`), built to mirror a known-working
plugin's layout exactly, **also failed to fire**. That killed every structural hypothesis at once and
exposed the real pattern:

| plugin | registered | fired |
|---|---|---|
| `clavity-agy-autotrain` | earlier client lifetime | yes |
| `clavity-dotnet` | earlier client lifetime | yes |
| `flaui-mcp-marketplace` | upgraded during the current lifetime | **no** |
| `hooktest-mp` | minutes earlier, same lifetime | **no** |

**Confirmed by experiment.** After a full client quit-and-relaunch, **both** the `hooktest` control marker
and flaui-mcp's activation payload were present in the model's context — the payload matching
`ActivationPayload.cs` byte-for-byte, delivered by the hook whose command names the installed exe. So:

> **Claude Code registers plugin hooks only at client startup. A hook installed or upgraded during a
> client's lifetime stays inert until that client is quit and relaunched. A new session is not enough.**

Not a product defect. The shipped artifact was correct the whole time.

**The residual defect was ours, and it was false assurance.** Two surfaces told the operator the hook was
live when it was not:

- `flaui-mcp status` printed a bare `wired`. It can only ever inspect a file we just wrote — it has no
  way to ask the running client what it loaded. agy named this precisely: *epistemic overreach*, the
  vocabulary of a live connection describing our own bookkeeping.
- `docs/operator-manual.md` mentioned restarting only inside a conditional branch, and "restart" did not
  distinguish a new session from quitting the client.

**Fixed** — the wording now names the boundary everywhere the claim is made: the `status` success string,
the `install` output, and the operator manual. Retaining `wired` was deliberate: four of the five states
already begin with `staged`, so collapsing the success case into `staged` would have blurred it into the
failure family and dropped the signal that a SessionStart entry actually names our verb.

**Refuted along the way — do not retry:** a missing `.claude-plugin/plugin.json` (added to both the
staging dir and the cache; no change); a malformed command string (runs correctly under `bash` and
`sh`, exit 0); a `hooks` declaration in the manifest (agy-autotrain has none either); marketplace/plugin
root separation (the control replicates it and still did not fire). The `hooktest` control has been
uninstalled and its marketplace removed.

**Also wrong, and shipped — now corrected:** `docs/operator-manual.md` stated *"Claude references the
staging dir in place; it does not copy it."* It copies it —
`~/.claude/plugins/installed_plugins.json` records
`installPath: ~/.claude/plugins/cache/flaui-mcp-marketplace/flaui-mcp/<version>`. Editing the staging dir
has no effect until reinstall. A pre-existing claim from the installer rework, repeated unverified.

**Lesson for the gate, not just the bug.** Every test we wrote asserts what the installer *stages*. None
asserts what the client *executes*, and no test can — the boundary is outside our process. That gap is
now handled by **honest wording** rather than a false green: `status` states what it measured and names
what it cannot see. The one check that would have caught this is a real session, and it could not run
until after release.

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

### The post-v0.20.0 defect-elimination decomposition (SP0–SP4) — canonical index

**Written down here on 2026-07-30 because it existed NOWHERE in this repo.** An audit found the item
numbering and the SP→item mapping lived only in one session's local agent-memory file: every SP spec cites
"item 3" or "item 7" without any document defining the list, so losing that memory would have left no way to
reconstruct which bullet is item 4 versus item 8. The numbering below is now the repo's own record.

Scope of the increment: **the 2 filed defects + ALL 8 opportunistic items = 10, plus item 11 added by SP4 and item 12 promoted from a captured anomaly**. Sequence between
subprojects does not matter; what matters is that **`docs/fix-the-tool-backlog/` is EMPTY before v1.0.0 is
stamped** — v1.0.0 ships with no known defects.

| # | Item | SP | Status |
|---|---|---|---|
| 1 | `wait-for-cull-disagrees-with-find` (filed defect) | SP1 | ✅ fixed — backlog file deleted in `7802736` |
| 2 | `launch-starves-on-ambient-single-instance` (filed defect) | SP1 | ✅ fixed — backlog file deleted in `5892ddc` |
| 3 | `wait_for_stable` scope-by-ref | SP2 | ✅ shipped + MEASURED (see the item-3 entry below) |
| 4 | snapshot/diff value-change detection | SP4 | ⬜ not started |
| 5 | terminal tab ordinal discovery | SP2 | ✅ shipped as the new `desktop_list_terminal_tabs` |
| 6 | JSON-shape tripwires | SP0 | ✅ merged (`015f23c`) |
| 7 | redact descriptor `Name` for `IsPassword` | SP2 | ✅ RETIRED as invalid; the real guarantee is now pinned |
| 8 | occlusion-aware capture (`PrintWindow`) | SP4 | ⬜ not started |
| 9 | per-field redaction | SP3 | ✅ shipped — merged `ef17ed9` (`--no-ff`) |
| 10 | delayed-render clipboard (`WM_RENDERFORMAT`) | SP4 | ⬜ not started — the estimate-blower |
| 11 | redaction completeness (A1 pixel-mask fail-open · A5 focused-window title · A2 stats rename · A6 token constant) | SP4 | ✅ shipped — merged `079aebd` (`--no-ff`). Capstone GREEN after 7 rounds; test audit complete. 16 accepted boundaries ledgered in `docs/coverage-debt.md` |
| 12 | the repo's 0-warning gate does not enforce itself | — | ✅ shipped — release-tooling subproject. `dotnet build FlaUI.Mcp.slnx -c Release` is INCREMENTAL and does not re-report warnings for up-to-date projects. MEASURED during SP4: it printed `0 Warning(s)` on a tree where `--no-incremental` printed `2 Warning(s)`. Fixed by a new root `Directory.Build.props` setting `TreatWarningsAsErrors` (`54b1dc5`), which fixes it MECHANICALLY rather than by discipline — MSBuild never marks a FAILED project up to date, so an error re-reports on every build. MUTANT PROVEN: an unused local (CS0219) turns `Build succeeded` into `Build FAILED`. `BuildPropertySweepTests` (`f43d546`) forbids any `.csproj` or nested props file from overriding it — nested props MEASURABLY win over the root, so a csproj-only sweep would be trivially bypassable |
| 13 | bare `catch` blocks swallow CRITICAL failures repo-wide | — | ⬜ not started — MEASURED at SP4 capstone round 4: **108** bare catches across 20+ files in `src/` can swallow `OutOfMemoryException`. SP4 filtered every catch on its own pixel path and both `ToolResponse` boundaries; the rest are untouched. Repo-wide refactor across input, watch, session and geometry code. Ledgered as AB-12 |
| 14 | `CollisionMarker.Record()` has no cross-process lock | — | ⬜ not started — `Record()` reads the marker, merges, and rewrites the whole file (`CollisionMarker.cs:62-91`) with no `Mutex` and no file lock. `WriteAtomically` (`:114-121`) prevents a TORN file, not a lost update: two installers that both read the same baseline each write their own entry and the second overwrites the first, so one user's disabled plugin is never restored. CONFIRMED by reading the code during the D1 review (spec `2026-08-20-release-tooling-design.md`, round-2 finding 19). Requires two simultaneous installs, which is why it has never been observed. Pre-existing and deliberately NOT fixed in the D1 subproject — filed here rather than silently widening that scope. |
| 15 | `ClaudeSkillDeployer.Deploy()` is dead production code | — | ⬜ not started — MEASURED: `grep -rn "\.Deploy()" --include=*.cs` matches only `ClaudeSkillDeployerTests.cs` and `InstallStatusClaudeTests.cs`, never `src/`. Production calls only `.Remove()` (`CliRouter.cs:316`, on every install) because the skill now ships INSIDE the marketplace plugin (`PluginArtifactWriter.cs:126` extracts it to `skills/driving-flaui-mcp/SKILL.md`). So `Deploy()` and the `~/.claude/skills/flaui-mcp` layout it writes are no longer reachable from any shipped path, while a full test suite still pins their behaviour — which reads as if the mechanism were live. Found while checking README staleness during the release-tooling subproject; the stale README line it caused was fixed there, this is the code half. Deleting a public method plus its test suite was deliberately not folded into that branch. |
| 16 | `PopupRootCoverageTests` ARRANGE step is FLAKY on the full Desktop gate | — | ⬜ not started — OBSERVED 2026-08-20 running the final Desktop gate for the release-tooling branch: `A_popup_element_is_returned_exactly_once_across_roots` failed at ARRANGE (`PopupRootCoverageTests.cs:48` — the context menu never opened within 2.5s), then passed 3/3 in isolation and 157/157 on a full re-run. The test DIAGNOSES its own failure mode in the thrown message: the right-click is COORDINATE-BASED, so a sibling `TestApp` window stacked at the same default position swallows it. So this is a fixture-collision flake, not a product defect — but it costs a ~13-minute false failure on the v1.0 Desktop gate, which is the gate a release is judged on. Fix direction: stop opening the menu by coordinate (drive it through the element, or move the fixture window off the default position before right-clicking) rather than widening the 2.5s window, which does not address the cause. |

Also fixed en route, though never one of the ten: `value-and-find-paths-miss-desktop-level-popups`
(deleted in `94e932a`).

⚠ **Items 4, 8, 9 and 10 are HARDENING, not defects.** The "no known defects at v1.0.0" bar is therefore
**not** gated on finishing SP3/SP4 — it is gated on emptying the backlog directory.
✅ **That bar is MET: `docs/fix-the-tool-backlog/` holds no defect files** (only `_template.md`, the blank
filing form the `flaui-curate` skill fills — scaffolding, never delete it).

The bar was briefly UNMET when `install-removes-conflicting-marketplace-copy-and-never-restores-it` was
filed — the installer deregistered a user's conflicting marketplace copy while announcing it would restore
it. Fixed in the release-tooling subproject and its file deleted per the repo convention that fixing a
defect deletes its entry. ⚠ **Its filed diagnosis was WRONG**, and that correction is worth more than the
filing: it blamed the disable/restore machinery, but MEASURED, `claude plugin disable` worked perfectly and
the marker was written correctly. The destroyer was an idempotency sweep in `ClaudePluginRegistrar` running
`claude plugin uninstall` with the BARE plugin name, which matched the user's copy moments after the remedy
had carefully disabled and recorded it. Both subsystems were individually correct; the defect existed only
in their sequence. Before it, `negative-timeout-disables-the-sta-watchdog` was fixed the same way.

- **Phase 3b-1 perception leftovers:** occlusion-aware capture (`PrintWindow`, vs the current
  focus-first screen-scrape); full-desktop *per-field* redaction for non-denied windows (denylist
  whole-window refuse is the floor); snapshot/diff *value*-change detection (needs opt-in per-node value
  reads — omitted from the default walk for STA perf); `desktop_wait_for_stable` scope-by-ref; the
  documented diff-identity limit on anonymous virtualized recycled rows (diff those by value/text).
- **Phase 7.1 clipboard:** delayed-render `WM_RENDERFORMAT` clipboard for a precise paste-consumption
  signal (vs the current best-effort restore-on-confirmed-consumption).
- ~~**v0.13.0 micro follow-ups:** surface each `TabItem`'s `tabIndex` in `desktop_snapshot` (or have
  `desktop_read_terminal_tab` echo the `index→title` map) so ordinal selection isn't hand-counted
  (consumer-UX, from live smoke — design fork, agy-first before implementing).~~ — **DONE (SP2, item
  5).** Both original options were rejected after measurement: a `desktop_snapshot` ordinal is not
  usable as a `tabIndex`, because five filters sit between the raw child array and an emitted
  snapshot node and four of them can drop a `TabItem` (`SnapshotEngine.cs:62` popup dedup, `:65`
  IsOffscreen, `:66-70` CullToWindowBounds, `:101-104` MaxDepth). Shipped a NEW read-only tool
  instead, `desktop_list_terminal_tabs`: lists a Windows Terminal window's tabs as
  `{ tabs: [{ index, title, active }], activeTabIndex }` WITHOUT selecting any (works in
  `--read-only-mode`). `TerminalTabReader.List` shares `EnumerateTabs` with `Run`, so the index
  spaces match **by construction**, not by agreement. `ReadOnly = true`, uses `ToolResponse.Guard`
  (not `GuardWrite`).
- ~~**Tool-level JSON-shape tests**~~ — **DONE (SP0).** `truncatedFrom` (`desktop_get_text`) is pinned
  through the anonymous projection in `ToolProjectionShapeTests`; `Hint` (`desktop_list_windows`) in
  `ListWindowsProjectionShapeTests`. The two surfaces disagree by design and both halves are now pinned:
  the anonymous projections EMIT their nulls (`ToolResponse` sets no `DefaultIgnoreCondition`), while
  `WindowInfo.Hint` carries `WhenWritingNull` and is OMITTED, keeping its PascalCase record name.
- ~~**Micro belt-and-suspenders:** redact descriptor `Name` for `IsPassword` controls.~~ — **RETIRED
  (SP2).** Premise was false: `RefRegistry` falls back to Name+ControlType as the ref identity key when
  `AutomationId` is absent (`RefRegistry.cs:181-182`) and the cached fast path compares the Name (`:334`),
  so redacting the descriptor `Name` would make an `IsPassword` element with no `AutomationId`
  permanently unresolvable — breaking exactly the controls this item targeted. `PerceptionManager.cs:580`
  already documented this. The descriptor `Name` is already never echoed (`RefRegistry.cs:206-209`); that
  guarantee is now pinned by `PasswordRedactionTestsHeadless`.
- **Desktop-level popup coverage is structural, not test-proven.** SP1 routed `find` and
  `wait_for(valueEquals)` through `PopupFinder.SearchRoots`, so both now reach popups at either level —
  but this host's WPF context menu is a window CHILD (measured: `viaWindowRoot=1, viaPopupRoots=1`), so
  the fixture exercises cross-root DEDUP and cannot reproduce desktop-level blindness at all. The fix is
  the same structural routing every other perception path already used, and `PopupRootCoverageTests`
  deliberately claims no blindness test. To close it for real, the fixture needs a Win32 `#32768` menu or
  an older `HwndWrapper` popup host that lands at the desktop level. Recorded here because the backlog
  entry it came from was retired when the code shipped.
- **A genuinely hung window still consumes an action slot permanently — KNOWN, BY DESIGN, and NOT what the
  timeout guard fixes.** Stated here because the two are easy to conflate. The `timeoutMs` guard
  (`AutomationDispatcher.ClampActionTimeout`) closes the *caller-input* vector: no argument can now make the
  watchdog wait forever. It cannot reclaim a slot lost to a REAL COM/UIA block — the worker thread blocks
  inside the native call, so its `finally { Interlocked.Decrement(...) }` never runs, and five such calls
  exhaust `MaxPendingActions` for the life of the process. That is inherent to abandoning a blocked STA call
  (a thread parked in COM cannot be killed safely), and the class docstring already flags it as bounded "until
  Phase 4's full action budget". Surfaced by the AGY-CAPSTONE Boundary Smuggler seat, which confirmed the
  input vector IS closed. Not filed as a defect: it needs a genuinely unresponsive window, not attacker-
  controlled input.
- **`wait_for_stable` scope-by-ref (`scopeRef`) — SHIPPED in SP2, and MEASURED. The poll got ~8x cheaper;
  the tool did NOT get usable at its default budget.** Measured on the WPF TestApp window (95 whole-window
  nodes, scoped to a depth-4 subtree), driving `WaitCoordinator` directly:
  · unscoped poll **~3956 ms/poll** · scoped poll **~479 ms/poll** ⇒ **~8.3x** off the polling phase.
  · a **settled** scoped wait (quietMs 400, pollIntervalMs 100) finished in **5704 ms** over 6 walks —
    **over the 5000 ms default budget.**
  **The honest negative, which is the useful part:** rooting the poll does not make a settled scoped wait
  fit the default budget, because the settle path takes a final **whole-window** snapshot using the static
  `PollOptions` (`WaitCoordinator.cs:160`, `RootRef` null), so one full walk (~3956 ms ≈ **80% of the
  5000 ms budget**) is re-paid at the very end. Two scoped polls on top guarantee the overshoot. So
  `scopeRef`'s real value is the polling phase — a subtree that would never have settled inside the budget
  now can, and large trees benefit most — not end-to-end latency at the default. The remaining lever is the
  `CacheRequest` work below, **not** more scoping. (Independently corroborated by an agy consult, which
  reached the same reading from the same lines.)
  **Known sharp edge, PRE-EXISTING and not introduced here:** a settled `wait_for_stable` has always ended
  with a durable snapshot, and `BeginSnapshot` clears a window's refs (`RefRegistry.cs:36`, class docstring
  `:8`), so the caller's own `scopeRef` is dead after a successful call — a second identical call throws
  `RefNotFound`. This is the standing ref-lifetime contract (`docs/agent-contract.md`: refs are bound to a
  snapshot, "Take a fresh snapshot"), now visible on a new parameter. Verified pre-existing: the final
  settle snapshot is byte-identical at `2bdb609:142`, before item 3.
  **NOT A DEFECT, because the recovery path already ships and is cheap:** `desktop_find` re-acquires a usable
  ref for the same element WITHOUT a whole-window walk — it runs a targeted UIA query and registers the match
  **additively**, deliberately not superseding held snapshot refs (`PerceptionManager.cs:478` docstring,
  `:623` "ADDITIVE, cached"). So the idiom is wait → find, and the two tools compose. The only real hazard was
  that nothing SAID so; `desktop_wait_for_stable`'s description now does.
  **Returning a re-resolved `newScopeRef` from `wait_for_stable` was considered and REJECTED** (agy proposed
  it, then withdrew it on reading `FindAsync`): it would duplicate `desktop_find`'s job inside a wait tool and
  conflate two orthogonal responsibilities, to save a call that is already cheap.
- **Batch the walk's per-node property reads (`CacheRequest`) — MEASURED, and the single biggest lever
  on this tool's latency.** Attribution on a warm 98-node WPF window: desktop/popup scan 593 ms · single-
  call tree enumeration 421 ms · full build 6098 ms ⇒ **per-node property traffic is ~90%+ of the walk**,
  ~58 ms/node against ~34 cross-process reads per node (22 scalar properties + 12 pattern probes). Caveat
  on that split, stated because it is easy to over-read: the engine walks with per-parent
  `FindAllChildren`, so the delta also contains ~100–200 ms of enumeration-shape difference, not property
  reads alone. This settles an attribution that had been argued both ways — the desktop scan is ~10%, not
  the dominant term.
  Approach (agy-consulted): one `CacheRequest` carrying all 22 properties + 12 patterns, `Activate()`d
  **tightly** around a per-parent `FindAll(TreeScope.Children)`, elements in `AutomationElementMode.Full`,
  and the engine reading **Cached** accessors — keeping the mid-walk cull so a hidden 10k-item list is
  never fetched. Hazards, all first-class: a single missed `.Current`/`.Value` accessor silently falls
  back to a live cross-process read and quietly restores the cost (detect by asserting walk time drops to
  the ~100 ms order, not 1–2 s); `AutomationElementMode.None` would break the promise that refs stay
  resolvable after the walk; `TreeScope.Descendants` + cache defeats culling; parallelising the walk
  deadlocks providers serialized on their own UI thread. Precedent + the known trap already live in
  `Watch/Uia3EventSource.cs:69-90` — `CacheRequest.Activate()` is thread-local, scope it tight.
  Not free: cached values are captured at fetch time, so this trades a little staleness for a large
  latency win — weigh it against `wait_for`'s "never report a wrong belief" contract before adopting.
- **`desktop_find` pays a RuntimeId read per element while a popup is open.** Cross-root dedup needs an
  identity for every candidate, and a RuntimeId read measured ~1.0 ms/node on this host — so a find over
  a large window with a menu open costs seconds it would not otherwise. Already gated to the multi-root
  case, which is the cheap half of the fix. The cheaper remaining idea, not attempted because it changes
  `PopupFinder`'s contract: it finds popups by two paths, and Path-2 popups are *window children* whose
  elements the window enumeration already returned — so if `SearchRoots` said which path found each root,
  find could skip Path-2 roots outright and drop the dedup entirely. Verify that claim before relying on
  it; the trade is correctness (a duplicated ref) against latency, and correctness won.
- **Item 9 — GATES GREEN at `a23c08e`.** Headless **848/0/0** (Release, 0 warnings) · Desktop **150/0/0** ·
  PopupGrafting **1/0/0**, both Desktop halves with **0 skipped**, on a physical console under a lease.
  Against the 140/0/0 Desktop baseline that is +10 facts from SP3; Task 13's three measurement classes are
  excluded from the routine gate by `Category=Measurement` and run on demand. This pass was also the FIRST
  execution of `fa71796` (the `Legacy` `selected` gap fix, committed at Task 5 and never run until now) —
  green, as expected: it closes a hole for the future rather than changing behaviour today, because nothing
  in the WPF fixture is ever `Selected`.
- **Item 9 (per-field redaction, SP3) — MEASURED, three ways (Task 13).** Spec §4.4's "zero-cost default
  path" **survives** contact with a measurement. Process homogeneity **does not** — it is recorded below as
  an unproven assumption, not as a result. Common conditions for every figure: WPF TestApp, whole-window
  walk, default `SnapshotOptions`, **79 nodes**, median of 12 samples after 3 warm-ups, one RDP-attached
  machine. ⚠ The plan says "the 95-node walk", the item-3 entry above says 95, the `CacheRequest` entry
  says 98. At default options it measures **79** — the measured number is recorded, not the inherited one.

  **(a) Default path vs the PRE-SP3 baseline.** Baseline = the branch point `3b0ab8d`, built in a throwaway
  worktree running an identical harness. Verified comparable first: `git diff 3b0ab8d..HEAD --
  test/FlaUI.Mcp.TestApp/` is **empty**, so both trees walk the same UI, and both reported 79 nodes.
  · pre-SP3 **median 2536.3 ms** (min 2502.2 · max 2960.2)
  · SP3, no rules **median 2565.4 ms** (min 2474.9 · max 3058.6)
  ⇒ **+29.1 ms (+1.1%) at the median, but −27.3 ms at the MINIMUM.** The sign flips with the statistic, and
  no systematic cost can make the best-case sample *faster* — so this is run-to-run noise. **No measurable
  regression on the default path.**

  **(b) Worst-case rule set.** 64 **global** rules built deliberately **not to match** — that is the
  ceiling, not a lenient case: `Classify` returns on the first match, so a matching set exits early and
  would flatter the figure. Both arms interleaved in one process so drift cancels.
  · no rules **median 2593.0 ms** · 64 rules **median 2614.0 ms** ⇒ **+21.0 ms · 1.008x.**
  The delta sits *inside* the 64-rule arm's own spread (min 2539.5, max 3382.7). Consistent with the
  `CacheRequest` entry above: the walk is ~90% cross-process property traffic, so rule evaluation is not a
  visible term even at the ceiling.

  ⚠ **Method note, learned the hard way.** The first (a) comparison read **+57 ms** and looked like a real
  regression. It was the **harness shape** — the SP3 arm was interleaved with 64-rule walks and inherited
  their allocation state, while the baseline ran back-to-back. Re-running the SP3 arm in the baseline's
  exact shape removed it. Two rules fall out: measure the two arms in the *same* shape, and report median
  **with** min/max — a lone mean would have let one 3382.7 ms outlier inflate (b) roughly fourfold.

  **(c) Process homogeneity — SUPPORTED, NOT PROVEN. Tracked below as standing risk H1.** Measured twice
  (2026-08-04 and 2026-08-17), 13 and 7 live windows, including a woken Chromium at 160 nodes:
  **0 name-heterogeneous windows.** Measured by process **NAME**, not PID — both mechanisms that depend on
  this key on the name, so PID heterogeneity alone proves nothing (a Chromium window spans several PIDs
  that are all `msedge`, and both mechanisms stay correct there). Pinned by
  `test/FlaUI.Mcp.Tests/Perception/ProcessHomogeneityMeasurementTests.cs`, which measures and asserts no
  policy.
- **H1 — the whole-window denylist and SP3's per-walk `processName` hoist rest on ONE unproven assumption.**
  Promoted from a captured anomaly on 2026-08-17; owner **SP4 / post-v1.0 hardening**. Both mechanisms
  decide from the **root** window's process: `PerceptionPolicy.IsDenied(procName)` refuses a whole window,
  and SP3 reads `processName` once per walk and applies it to every node. If a window's nodes are **not**
  all the root's process, content owned by a **denied** process embedded in an **allowed** window is served.
  ⚠ **This predates SP3** — it is a property of the shipped denylist, not of the redaction feature.
  The measurement above did not find heterogeneity, but **could not reach the two shapes that would produce
  it**, so it is not evidence of absence: no WebView2 host was installed (`msedgewebview2` content inside a
  differently-named host app), and the UWP `ApplicationFrameHost` / `SecHealthUI` windows exposed **0 nodes**
  even after `desktop_wake_accessibility`, because they were suspended. A future embedded-host change
  therefore breaks **both** mechanisms at once. To settle it, measure on a machine with a WebView2 host or a
  running UWP app; if heterogeneous, fix the denylist in its own commit and resolve `processName` per HWND
  boundary.
  **Unchanged by SP4 (spec §2, explicitly out of scope).** A5 makes the window-title surface consistent with
  `desktop_list_windows`, which is what allows "should window titles be redactable" (`titlePattern`) to be
  asked once, coherently, later — but neither touches the homogeneity assumption.

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
