# Phase 5 — Consumer Confidence (design spec)

- **Date:** 2026-06-30 · **Revised:** 2026-07-01 (F-F version renumber after v0.7.1/v0.7.2 shipped; v0.7.0 execution gate now satisfied)
- **Status:** SPEC (forward-writable; intent + contracts + invariants). NOT a line-level plan.
- **Execution gate (SATISFIED 2026-07-01):** Phase 4b (v0.7.0) had to be MERGED to master before any
  line-level TDD plan could be written — every input-layer item extends the 4b
  `InputGuard`/`InputTools`/Win32 leaf. **v0.7.0 is merged and master is now at v0.7.2**, so that
  fabricated-precision risk is discharged and the input-layer line-level plan is writable.
  `desktop_find`/durable-refs depend only on already-merged perception.
- **Provenance:** consumer-driven. Authored by the agent that dogfoods this server, answering "what
  would make you, the consumer, happy?" AGY-FIRST consult (cascade `f36d82b9`) on scope/artifact/
  sequencing + the preview oracle-leak finding folded below. User direction: full ambitious scope,
  single combined spec, cost irrelevant.

---

## 0. Why

Two classes of pain dominate real driving, and one trust gap:

1. **I plan blind.** I cannot know whether an input action will be allowed (lease/deny-list/interlock/
   budget) until I fire it and read the failure. Multi-step input plans fail mid-sequence.
2. **I target expensively.** Refs follow tree order not value and go stale on re-snapshot; to locate
   one control I read and parse the whole snapshot tree. This is the biggest token/turn sink.
3. **Trust gap:** the real `SendInput` leaf — the most correctness-critical code — is the least
   CI-tested (one live-RDP spike + review). "Input works" is currently a belief, not an observation.

Phase 5 fixes all three and rounds out the input surface, **without weakening any guardrail**. The
security model still treats the agent as the untrusted component; every new capability respects the
lease / deny-list / session / budget / audit pipeline. Non-negotiable: no self-unlock, no guardrail
relaxation (§6).

---

## 1. Scope & tool surface

| Tool / change | Kind | Group | Gate |
|---|---|---|---|
| `desktop_find` | NEW (read) | A targeting | read-only; PerceptionPolicy + redaction |
| Durable refs (descriptor re-bind) | CHANGE (read) | A targeting | read-only |
| `desktop_input_preview` | NEW (read) | A planning | ref-path lease-free / coord-path lease-gated; verdict-only |
| `desktop_hover` | NEW (input) | B surface | full InputGuard |
| `desktop_scroll_wheel` | NEW (input) | B surface | full InputGuard |
| `desktop_paste` | NEW (input) | B surface | full InputGuard + read-only-mode block |
| `modifiers[]` param on click/click_at/drag (modified-click) | CHANGE (input) | B surface | full InputGuard; atomic single-batch hold |
| Post-action hit identity on click/drag/click_at | CHANGE (input) | C loop-close | returned ONLY post-fire under lease |
| `desktop_snapshot_diff` scope=ref + opt-in act-and-diff | CHANGE (read) | C loop-close | read-only |
| Minimized: `isMinimized` flag + opt-in `restore` | CHANGE | D robustness | restore = window-manage gate |
| `desktop_wait_for_dialog` | NEW (read) | D robustness | read-only |
| `desktop_reacquire` (by title/pid/automationId) | NEW (read) | D robustness | read-only |
| `flaui-mcp selftest --input` / `--perception` | NEW (CLI) | E trust | console-only; release gate |

Total new tools: ~8 + 1 CLI verb + several contract enhancements.

---

## 2. Security invariants (the spine — referenced by each capability)

- **INV-1 — one sink, one gate.** Every synthetic-input verb (existing type/key/click/click_at/drag
  **and** new hover/scroll_wheel/paste + the modified-click `modifiers[]` param) flows through
  `InputGuard.Authorize`
  (elevation → lease → deny-list/interlock → session → budget → audit) before its leaf. No new tool
  introduces a sink that bypasses the pipeline.
- **INV-2 — preview is not a resolution oracle (agy finding, CONFIRMED).** A coordinate dry-run is a
  resolution oracle: `WindowFromPhysicalPoint` + `GetWindowThreadProcessId` + `GetClassName` all work
  **cross-integrity**, so an unleased agent sweeping pixels could enumerate elevated processes and map
  the spatial bounds of secure surfaces (UAC consent, credential prompts, lock screen). Therefore:
  (a) `desktop_input_preview` returns the **verdict enum only** — never resolved `ProcessName`/
  `WindowClass` strings; (b) the **ref-path** preview is lease-free (that element is already inside my
  perception scope — snapshot already applied the deny-list + redaction); (c) the **coordinate-path**
  preview is **lease-gated** (controller refinement beyond agy's verdict-only minimum — removes even
  the spatial-sweep side-channel, at near-zero cost since locked-mode planning uses refs, not raw
  pixels). See fork F-A.
- **INV-3 — coordinate identity only post-action under lease.** The real click/drag/click_at tools MAY
  return the resolved hit identity (so I can verify what I hit), but ONLY after firing under a valid
  lease. Never pre-action, never unleased — that is the same oracle as INV-2.
- **INV-4 — audit stays event-only.** New actions are audited by type + magnitude (hover point,
  scroll notches, paste **length**), never payload content (paste text, typed text — payloads can BE
  secrets).
- **INV-5 — find inherits the perception floor.** `desktop_find` and descriptor re-resolution apply
  the same `PerceptionPolicy` deny-list + `IsPassword` redaction as `desktop_snapshot`. Find cannot
  surface a denied/credential window's children, and cannot become a back-door enumeration of denied
  processes.
- **INV-6 — read-only-mode.** All input-firing additions are blocked in `--read-only-mode`
  (`WriteBlockedReadOnly`). `find`/`preview`/`diff`/`wait_for_dialog`/`reacquire` are read-only and
  permitted.
- **INV-7 — selftest can never self-unlock (agy R2 BLOCKER, CONFIRMED).** `flaui-mcp selftest --input`
  MUST authorize its own input via a strictly **isolated** lease that the long-running MCP server
  **ignores** — an in-memory `ILeaseProvider` (or a distinct, server-unread path) scoped to the
  selftest process only. It MUST NOT write the global lease file. Rationale: the agent has shell
  access; if selftest wrote the real lease, the agent could run it to grant *itself* a lease and
  bypass the human-in-the-loop (violating the no-self-unlock non-goal, §6). Selftest validates the
  leaf in-process; the global lock is untouched.
- **INV-8 — input never implicitly rebinds a stale ref (agy R2 BLOCKER, CONFIRMED).** Durable-ref
  descriptor re-binding (§3.2) is a **read-only** affordance. The input-firing tools
  (type/key/click/click_at/drag/hover/scroll_wheel/paste) MUST NOT silently rebind: if the
  exact cached UI node is gone, the input tool fails `REF_STALE` and forces an explicit re-`find`/
  re-snapshot. Rationale: UI virtualization recycles `AutomationId`s (a list-item id that pointed to
  "Edit" can point to "Delete" after a scroll); a silent rebind during an input action would fire a
  destructive event on the wrong control. Deterministic ≠ same logical element for writes.
- **INV-9 — paste re-verifies like type (agy R2, CONFIRMED).** `desktop_paste` performs the same
  atomic pre-send root re-verify (`GetForegroundWindow() == target.Root`, fail-closed on mismatch)
  immediately before the `Ctrl+V`, exactly as `desktop_type`. A focus race between set-clipboard and
  paste would otherwise dump (possibly secret) clipboard content into an unintended window. Caveat:
  paste necessarily exposes its payload on the global clipboard for the interval until overwritten —
  documented as an inherent property of the paste mechanism (use `type` for secrets you don't want
  transiting the clipboard).

---

## 3. Capability designs

### 3.1 `desktop_find` — query for a ref (Group A)

**Contract:** `find(window, query, max=20, scope=null)` where
`query = { automationId?: string, name?: { eq|contains }, controlType?: string, enabledOnly?: bool }`,
`scope` = an optional ancestor ref to search within. Returns
`{ matches: [{ ref, automationId, name, controlType, bounds, isOffscreen, isEnabled, hasFocus }], totalMatches, isTruncated }`
in tree order, capped at `max`. **Truncation is signalled** (`isTruncated`/`totalMatches`, agy R3) —
a silent cap is misleading; the agent narrows its query when truncated. `bounds` is a physical-pixel
rect `[x, y, w, h]`; absent UIA strings are `null` (not empty-string). `isEnabled`/`hasFocus` are
included (agy R5) so the agent can tell a grayed/validation-disabled control from an actionable one
without a doomed click or an extra scoped snapshot.

**Atomic find-and-act (agy R5 — saves the find→act round-trip under INV-8):** the **element-targeted**
input tools (`type`/`click`/`hover`/`scroll_wheel`/`paste`) MAY accept a `query` (identical schema to
`find`'s) **in place of** a `ref`. (The coordinate tools `click_at`/`drag` take `xPct/yPct`, not a ref,
so the query form does not apply to them.) The tool resolves the query fresh at action time and
fires **only if it matches exactly one element**; ambiguous (>1) or empty → fail
(`RefStaleAmbiguous`/no-match), never a guess. This is NOT a stale-ref rebind (INV-8 holds — there is
no cached node being silently retargeted; it is a fresh, unambiguous, fail-closed resolution) and it
collapses the "tree shifted → re-`find` → act" two-turn tax into one atomic call.

- Resolves on the query STA via a UIA condition built from `query`; mints refs exactly like snapshot
  (same RefRegistry, same descriptor capture for durability per §3.2).
- INV-5: a denied/credential subtree yields no children; password values never returned (find returns
  identity + bounds, not values — values stay with `get_text`/`get_grid_cell` which already redact).
- No match → empty list (NOT an error). `window` invalid → `WindowNotFound`. Ambiguity is fine — find
  is allowed to return many; the caller narrows.
- **Why:** kills the "read the whole tree to locate one control" token sink. This is the single
  highest-leverage read-only item.

### 3.2 Durable refs (Group A)

Today a ref resolves via a captured descriptor but goes stale on tree shift. Enhancement: each ref
captures a **stable fingerprint** (AutomationId, ControlType, Name, and an ancestor-AutomationId
path). On resolve, if the exact cached node is gone, attempt a **deterministic** descriptor re-bind
within the same window before declaring `REF_STALE`:
1. exact AutomationId (+ControlType) match → bind;
2. else AutomationId path + ControlType → bind;
3. else unique (Name + ControlType) → bind;
4. ambiguous (>1 candidate) or none → `REF_STALE_AMBIGUOUS` (re-snapshot/`find`).

Strictly deterministic, no fuzzy guessing across ambiguity (a wrong rebind on an input target is a
correctness hazard). Reduces my re-mint churn after benign UI updates.

**INV-8 governs the write side:** this re-bind is a **read-only** affordance only. A ref handed to an
input-firing tool whose exact cached node is gone fails `REF_STALE` (no implicit rebind) — recycled
`AutomationId`s under UI virtualization make a "deterministic" rebind a destructive-action hazard
(type into the wrong field). Rebind serves `desktop_find`/`desktop_snapshot`/reads; writes re-find
explicitly.

### 3.3 `desktop_input_preview` — dry-run (Group A, the planning win)

**Contract:** `desktop_input_preview(actions)` where `actions` is an **array of FLAT action objects**
`{ kind, window?, ref?, xPct?, yPct?, endXPct?, endYPct?, chord?, textLength? }`, `kind ∈
{ "type","key","click","click_at","drag" }`, the remaining fields flat-optional and validated per
`kind` (e.g. `kind:"click"` needs `window`+`ref`; `kind:"click_at"` needs `window`+`xPct`+`yPct`). A
single-step preview is just a one-element array. Returns a **parallel array** of results, one per
action.

This shape is deliberately a **flat array of flat objects** — it gives the R5 batch win (a whole plan
pre-validated in ONE turn) while staying clear of the R3 LLM-schema trap (the trap is *nested
polymorphic* `oneOf`/discriminated-union args, NOT a homogeneous array of flat objects). No nested
objects, no `oneOf`.

**DECOMPOSED verdict (agy R5 BLOCKER — the masking paradox).** The real pipeline checks lease BEFORE
the deny-list, so a naive preview returns `"locked"` for every target while input is locked — masking
the very `denied`/`interlocked` classification the agent wants to pre-validate. Preview therefore
evaluates the **target classification INDEPENDENTLY of lease** and returns the legs separately:
`{ targetVerdict, leaseState, sessionState, budgetState, wouldFire, needsShellsCap?, secondsRemaining? }`
where `targetVerdict ∈ { "allowed","denied","interlocked","elevated","invalid" }`
(`elevated` = the Phase-4a elevated-server hard-fail / `AccessDeniedIntegrity`),
`leaseState ∈ { "active","locked" }`, and `wouldFire = (targetVerdict=="allowed" && leaseState=="active"
&& session ok && budget ok)`. An unleased agent thus sees "this target *would* be allowed — you need a
lease" vs "denied regardless of lease," which is the planning signal.

**Security reconciliation with INV-2 (no oracle re-opened):** exposing `targetVerdict` to an unleased
agent is safe only on the **ref-path** — that element is already inside the agent's perception scope
(it came from a snapshot that already applied the deny-list + redaction), so its classification leaks
nothing new. The **coordinate-path** preview stays **lease-gated** (INV-2c); its independent
classification is therefore only ever available to an already-leased agent, who could simply try the
action. No unleased coordinate sweep, no identity strings (INV-2) — only the verdict legs.

- Resolves the `ActionTarget` exactly as the real tool would (ref-path via `RunOnRefForInputAsync` +
  `ResolveElementTarget`; coordinate-path via `HitTestRoot`), then runs the InputGuard decision
  pipeline in a **side-effect-free** evaluator: **no SendInput, no budget consume, no audit-as-action**
  (a preview MAY emit a distinct `preview` audit event, length-free, for traceability).
- INV-2: verdict enum only; ref-path lease-free; coordinate-path lease-gated.
- **Workflow it unlocks:** while input is LOCKED I validate an entire ref-path sequence
  (every step's verdict), a human runs `flaui-mcp unlock` **once**, and I execute a pre-validated plan
  — instead of discovering a denial on step 4.
- **Implementation note (for the future plan):** `InputGuard` needs a pure `Evaluate(target, action,
  hasShells)` that returns the verdict without consuming budget or firing — the existing
  `ActionPolicy.Classify` covers the deny-list leg; extend to a full read-only pipeline pass that also
  reports lease/session/budget state.

### 3.4 Input surface additions (Group B — all under InputGuard, INV-1/4/6)

- **`desktop_hover(window, ref | xPct,yPct)`** — synthetic mouse-move to the resolved point (reveals
  tooltips / hover menus). Gated (it manipulates the real cursor and can dismiss menus / trigger
  hover-activate). `pathUsed: ref|coordinate`. Re-verify root before move like the click path.
- **`desktop_scroll_wheel(window, ref | xPct,yPct, notches, horizontal=false)`** — synthetic wheel for
  canvases/web-views that don't expose UIA scroll. Gated. Magnitude (notches) audited, INV-4.
- **`desktop_paste(window, ref, text)`** — atomic: set clipboard (CF_UNICODETEXT, existing Win32
  clipboard path) → focus `ref` → **re-verify root** (INV-9) → Ctrl+V — under ONE guard
  authorization. Cheaper + safer than set-clipboard + key as two calls that clobber the clipboard.
  Audit length only (INV-4). The Ctrl+V fires through the same leaf with the same fail-closed
  pre-send root re-verify as `desktop_type` (INV-9) so a focus race can't dump the clipboard into the
  wrong window. Prior clipboard restoration: fork F-C (lean reversed to *clobber + document* per agy
  R2 — a CF_UNICODETEXT-only restore destroys rich formats; see F-C).
- **Modified-click via a flat `modifiers[]` param (NOT a `key_hold` primitive)** — the common need is
  "Shift-click", "Ctrl-click", "Shift-drag". Realize it as an additive optional `modifiers[]` string
  param on `desktop_click`/`click_at`/`drag` (the existing `desktop_key` already handles key chords).
  The modifiers are pressed and released **inside the single atomic `SendInput` INPUT[] batch** that
  performs the click — press-mods → click → release-mods, one OS call — so key state is **never held
  across an async / MCP-call boundary.** This (agy R3+R4): (a) avoids the nested-`thenActions[]`
  discriminated-union schema that is a reliable LLM tool-calling hallucination trap (flat string array
  vs nested polymorphic AST); (b) eliminates the stdio-lifecycle stuck-key hazard — a host hard-kill /
  pipe-closure (which `ProcessExit` would NOT survive) cannot strand a modifier, because nothing is
  held between calls. `Win` is still **banned** as a modifier (holding it pops OS shell UI).
  The rarer "hold a key across several discrete tool calls" pattern is intentionally **out of scope**
  — it is exactly the hazardous, kill-stranding case. Fork F-B RESOLVED to this design.

### 3.5 Loop-closing (Group C)

- **Post-action hit identity:** `desktop_click`/`click_at`/`drag` return the resolved hit
  `{ processName, windowClass, controlType? }` of what they actually targeted — ONLY after firing
  under a valid lease (INV-3) — so I verify I hit the thing I meant, not the gap beside it.
- **Scoped diff / act-and-diff:** extend `desktop_snapshot_diff` with `scope=ref` (diff only a
  subtree), and add an opt-in `returnDiff=true` flag on the input tools that returns a post-action
  scoped diff near the target (costs one snapshot; off by default). Lets me confirm an action landed
  cheaply instead of re-perceiving the whole tree.

### 3.6 Robustness (Group D)

- **Minimized:** `desktop_snapshot`/`find` gain an explicit `isMinimized` flag in the result (so I
  notice instead of silently getting a thin root-only tree) + an opt-in `restore=false` param that, if
  true, restores the window first (a window-manage op — gated like `desktop_window_transform`, not the
  input lease) then snapshots.
- **`desktop_wait_for_dialog(window, timeoutMs)`** → ref of the modal/popup that appears — a
  composable convenience over `wait_for` + popup snapshot for the common "click → dialog → act" flow.
  **Anti-spoof binding (agy R2):** the matched dialog MUST be bound to the host `window` — it must be
  **owned** by the host HWND (`GetWindow(GW_OWNER)` / owned-window chain) **or** share the host's
  process id. A bare "wait for any new top-level popup" is rejected: a malicious background process
  popping a look-alike credential prompt at the same instant would otherwise hijack the flow. `window`
  is therefore required (not optional).
- **`desktop_reacquire(byAutomationId | byPid)`** → rebind a window handle without a full re-list, so
  a server restart / handle invalidation doesn't strand me; durable refs (§3.2) then survive within
  the new handle. **Anti-spoof (agy R2):** `byTitle` is **dropped** as a key — a window title is
  trivially spoofable by any unprivileged process. `byPid` MUST additionally verify process
  **creation time + executable path** (PID values are recycled by the OS). Preferred key is the
  window's stable `AutomationId`/runtime identity where available.

### 3.7 Trust capstone — `selftest` + release gate (Group E)

- **`flaui-mcp selftest --input`** (console-only): launches the bundled TestApp, ensures a short
  lease, and runs end-to-end assertions on the real leaf — type→read-back, key→assert, click→assert
  focus, click_at→assert hit, drag→assert drop, hover→assert tooltip, scroll→assert position. Emits a
  machine-readable PASS/FAIL report; **exits non-zero on failure.**
- **`flaui-mcp selftest --perception`**: snapshot/find/get_text/get_bounds against TestApp, assert
  known values (headless-capable subset).
- **Release gate (agy R4 — not a checkbox):** `selftest` MUST emit a strongly-typed JSON report
  (environment fingerprint: machine/session, OS build, DPI, monitor geometry; the exact
  commit/version under test; per-assertion pass/fail; UTC timestamp). The release workflow script
  **ingests and validates** that report before permitting `git tag` — checking it is fresh, matches
  the commit/version being tagged, and is all-green. A human runs `selftest --input` on a real console
  (the CI box is headless RDP and cannot), but the *gate* is the machine-validated artifact, not a
  hand-attested checkbox (which degrades to theatre). This is the single item that most closes the
  consumer trust gap.

---

## 4. Sequencing & dependencies

- **HARD DEP (now SATISFIED):** Phase 5 execution required v0.7.0 (4b) merged to master — **done;
  master is at v0.7.2 (2026-07-01).** Preview, all input additions, post-action identity, and
  `selftest --input` extend the merged 4b pipeline; their line-level plans are now writable.
- `desktop_find` + durable refs depend only on merged perception (Phase 2/3) — implementable first.
- **Suggested execution order once unblocked:** find/durable-refs → preview → input surface
  (hover/scroll_wheel/paste/modified-click `modifiers[]`) → loop-closing → robustness → `selftest`
  gate **last** (it validates everything above).
- **Artifact path:** this single combined spec now → multi-lens AGY-AFTER (done, §7) → (v0.7.0 has
  merged) a line-level TDD plan (likely split into execution waves) → subagent/main-thread execution
  per the capability-gating rules.

---

## 5. Open forks (for the AGY-AFTER multi-lens review + user)

- **F-A — coordinate-path preview:** lease-gated (controller lean; removes the spatial side-channel)
  vs verdict-only-but-lease-free (agy's minimum).
- **F-B — modified actions:** RESOLVED (revised in R3/R4) → a flat optional `modifiers[]` param on
  `click`/`click_at`/`drag` (modifiers pressed+released inside the single atomic `SendInput` batch;
  never held across an MCP-call boundary; `Win` banned). The earlier `key_hold(holdKeys[],
  thenActions[])` scoped-batch design is **withdrawn** (nested polymorphic arg = LLM-schema trap; a
  cross-call hold is stdio-kill-unsafe). Open only: whether `Alt` should also be disallowed as a
  modifier (it can surface menu bars).
- **F-C — `desktop_paste` clipboard restoration:** RESOLVED (lean REVERSED by agy R2) → **clobber +
  document**, do NOT restore. A CF_UNICODETEXT-only restore would silently destroy any rich clipboard
  formats (images/files/HTML) the user had copied; a correct restore needs a full all-formats
  `OleGetClipboard` clone (disproportionate). Open only if a full-format clone is later deemed worth
  it.
- **F-D — selftest release gate:** RESOLVED (agy R4) → `selftest` emits a machine-validated JSON
  report (env fingerprint + commit + per-assertion result) that the release script ingests before
  tagging; human runs it on a console, but the gate is the artifact, not a checkbox. Open only:
  self-hosted console runner (full automation) as a later upgrade.
- **F-E — minimized `restore`:** RESOLVED (owner, 2026-06-30) → **default `false`**. Snapshot/find stay
  non-mutating by default; the `isMinimized` flag means the agent is never surprised by a thin tree and
  opts into `restore` only when it actually wants the window brought up.
- **F-F — version target:** RESOLVED (owner, 2026-06-30) → **incremental** (agy's challenge accepted).
  **RENUMBERED 2026-07-01:** the original v0.7.1/v0.7.2 targets were consumed by releases that shipped
  after this spec was authored (v0.7.1 = paced `desktop_type`; v0.7.2 = typed-text verify), so each
  Phase 5 wave shifts up one minor:
  **v0.7.3** = read-only targeting (`find`, durable-refs, scoped `diff`) — ships the biggest token/turn
  win first, smallest risk. **v0.7.4** = `preview` (read-only, but depends on the merged input
  pipeline). **v0.8.0** = the input-firing additions (paste, hover, scroll_wheel, modified-click) +
  loop-closing + robustness + the `selftest` gate. Bounds regression radius and the public-README/
  installer bump burden; gets me my highest-leverage tool (`find`) without waiting on the input surface.
- **F-B — `Alt` as a held modifier:** RESOLVED (owner, 2026-06-30) → **allow `Alt`** (keep only `Win`
  banned). Because `modifiers[]` are pressed+released inside the single atomic `SendInput` batch
  (never held across a call), a transient `Alt`-down/up bracketing one click is bounded to microseconds
  — the menu-surfacing risk doesn't persist or trap. `Alt`-click is a real interaction (design tools,
  selection modifiers) I want. Documented caveat: an app may still flash an accelerator hint. `Win`
  stays banned (its shell UI is more disruptive and out of the app's control).

---

## 6. Non-goals (guardrails held)

No lowering of the lease / deny-list / interlock / elevation-hard-fail guardrails. No agent
self-unlock. No remote / networked / cross-machine input. No OCR / vision targeting (separate
concern). No new tool that bypasses `InputGuard`. Preview/find never become identity oracles
(INV-2/INV-5).

---

## 7. Review posture

This spec drives real Phase 5 builds + a v0.8.0 release → it qualifies for **multi-lens AGY-AFTER**
(not single-pass). Lens log (each round hunts a new flaw-class with the running already-folded list):

- **Round 1 — AGY-FIRST fork consult (general-adversarial):** preview resolution-oracle → folded as
  INV-2/INV-3.
- **Round 2 — relentless adversarial security auditor (DONE):** 8 findings, all verified true and
  folded — INV-7 (selftest self-unlock hijack, BLOCKER), INV-8 (durable-ref rebind on input, BLOCKER),
  INV-9 (paste root re-verify), wait_for_dialog host-binding, reacquire byTitle dropped + byPid
  start-time/path verify, key_hold process-exit key-up sweep + `Win` ban, F-C lean REVERSED to
  clobber+document. NIT (preview enum spatial residue) noted as already closed for unleased agents by
  INV-2(c) lease-gating; an optional coarsening of the coordinate verdict to `allowed`/`blocked` is
  recorded for the API round.
- **Round 3 — API / wire-contract pedant (DONE):** folded — flat `modifiers[]` param replaces the
  nested-`thenActions[]` `key_hold` (LLM schema trap, BLOCKER); flat `preview` schema (no union);
  `elevated` added to the verdict enum; `find` returns `totalMatches`/`isTruncated` + `bounds` as a
  physical-px rect + null conventions. Error-code naming follows existing `ToolErrorCode` PascalCase
  (`RefStaleAmbiguous`, `WindowNotFound`). Post-action `hit` field confirmed additive/back-compat.
- **Round 4 — release / ops (DONE):** folded — `selftest` emits a machine-validated JSON report the
  release script ingests (F-D, no checkbox theatre); flat `modifiers[]` also eliminates the
  stdio-hard-kill stuck-key hazard (`ProcessExit` doesn't survive SIGKILL). agy CHALLENGED the
  single-v0.8.0 lean → F-F now recommends incremental releases (read-only → preview → input; version
  numbers renumbered 2026-07-01 to v0.7.3 → v0.7.4 → v0.8.0, see F-F). CHANGE rows confirmed additive / non-breaking.
- **Round 5 — UX / operator (agent-consumer) (DONE):** folded — preview verdict **decomposed**
  (targetVerdict independent of leaseState, `wouldFire`) to fix the masking paradox (BLOCKER) while
  honoring INV-2 (ref-path safe / coord-path lease-gated); preview accepts a **batch** (flat array →
  array of verdicts, one turn); input tools optionally accept a `query` for atomic find-and-act
  (exactly-one-or-fail; respects INV-8, saves a round-trip); `find` adds `isEnabled`/`hasFocus`.
- **Round 6 — holistic coherence (DONE — STOP):** found only **internal scar tissue** from the
  editing (the `key_hold` ghost in F-B/INV-1/INV-8/§4; coordinate tools wrongly listed for the
  `query` form; the preview single-vs-array schema clash) — all folded. agy explicitly cleared the
  security model: **no new regressions** — refs are opaque server-side handles (an unleased agent
  cannot fabricate a ref to an unperceived UIPI window, so decomposed-preview `targetVerdict` is
  airtight per INV-2/INV-5), and `query` find-and-act resolves through the same PerceptionPolicy
  filter as `find`. No NEW flaw-class surfaced.

STOP: the coherence pass surfaced zero new defects (only self-introduced contradictions, now fixed)
and zero security regressions → the lens has stopped finding. Full rotation complete
(adversarial→security→API→ops→UX→coherence); per-round live findings R2:8, R3:5, R4:3, R5:4, R6:0-new.
Artifact ready for the user review gate.
