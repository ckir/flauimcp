# flaui-mcp Driving Curriculum — Design

**Goal:** Move flaui-mcp driving-skill improvement from ad-hoc dogfooding to a repeatable **training
curriculum** of common Windows tasks that systematically gathers driving knowledge, feeds the existing
`flaui-learn` → `flaui-curate` loop, and ultimately **graduates proven heuristics into the shipped skill
seed** so both consuming agents (Claude Code and the agy/Antigravity peer) — and end users — drive better.

**Consuming agents (both must benefit).** flaui-mcp's `desktop_*` tools are driven by BOTH Claude and the
agy peer (agy is registered against the same server; a live agy instance was observed driving it this
session). The `driving-flaui-mcp` skill is therefore a *shared* asset. This design was produced as an
AGY-FIRST collaboration: agy was consulted as a co-consumer and its input is folded in below (with two
mutual corrections recorded in §8).

---

## 1. Purpose & success criteria

- **Purpose:** produce a structured, re-runnable set of Windows driving tasks whose execution surfaces
  real quirks/traps, captured as structured observations, curated into the skill, and eventually shipped.
- **Success (v1):** a curriculum doc exists; a single manual run produces N structured observations that
  `flaui-curate` folds into GROWTH or routes to `fix-the-tool`; the run reveals at least the known trap
  classes reproducibly.
- **Success (end state):** the `driving-flaui-mcp` skill reads like a **"minefield map"** — organized by
  app framework, with a ranked targeting ladder and pre-call predictability — with the strongest,
  verified rules **graduated into the hand-authored SEED and shipped** in a release.

## 2. Execution model — two-tier (both agents endorse)

Synthetic input (type/click/keys) needs a human lease + the physical console; read-only inspection needs
neither. The suite mirrors that safety split:

- **Autonomous read-only tier** — `desktop_list_windows` / `snapshot` / `find` / `get_text` /
  `wake_accessibility` / `watch`. Lease-exempt, **non-disruptive**, runs **unattended, anytime**.
  *(Solo-panel correction: `desktop_read_terminal_tab` is lease-exempt too but is **Destructive** — it
  visibly switches tabs and is blocked in `--read-only-mode` — so it is NOT unattended-safe. Treat it as a
  supervised / solo-console read: check `desktop_user_state` first and don't flip a present human's tabs.)* agy's driving bottleneck is human latency, so **this tier is maximized**: the bulk of
  targeting/comprehension coverage (find bounds across frameworks, poll for state, verify selector logic)
  costs zero human attention.
- **Supervised input tier** — `desktop_type` / `set_value` / `paste_text` / `key` / `click` / `select` /
  `drag`. Requires a human-granted lease (`flaui-mcp unlock`, `--allow-shells` for terminals) at the
  physical console. Run only while a human is present.

**Context economy (execution rule — mandatory).** Desktop payloads (a11y trees from `snapshot`,
`read_terminal_tab` buffers, `find`/`list` arrays) are large; a linear multi-task run overflows the
driver's own context (this design's session hit ~80% from exactly that). So **each curriculum task runs in
a FRESH SUBAGENT**: the subagent does the bulky driving — every snapshot/tree/buffer stays in *its* context
— and returns ONLY the compact 4-field observation (§5) to the main thread, which stays lean across the
whole run. Corollaries: run a **bounded batch per session** (the curriculum drains monotonically, like the
inbox); the read-only tier's large snapshots especially belong in subagents.

## 3. Artifact — a curated markdown curriculum, NOT a harness

**`.claude/flaui-mcp/training-curriculum.md`** (alongside `observations.md`, feeding the same loop). It is
a human/agent-readable list of parameterized scenarios — deliberately **not** a custom automated runner or
a scoring harness. Rationale (agy): a complex runner obfuscates the raw interaction an agent must
*experience* to learn a quirk; both agents read a markdown spec, pick a task, execute step-by-step, and
report. Tool-**code** correctness is tested elsewhere (§7), not here.

**Per-task YAML frontmatter** (so either agent can filter and self-dispatch):

```yaml
- id: <kebab-slug>
  tier: read-only | input          # read-only runs unattended; input needs a lease
  requires_lease: false | true     # true => also note --allow-shells if a terminal sink
  target_app: <notepad | explorer | settings | chrome | terminal | ...>
  framework: Win32 | WinUI | WPF | Electron/Chromium | Terminal | Unknown
  trap_class: <one of §4>
  steps: |                         # concrete desktop_* steps to attempt
  observe: |                       # what to watch for / what would be a finding
```

## 4. Coverage matrix — tool surface × trap classes

Tasks are chosen to cover the tool surface crossed with these trap classes (mine, plus agy's additions
marked †, plus what was validated live this session ‡):

- **Virtualized lists/grids** † — the a11y tree holds only rendered items ("doesn't-exist-yet"); reach an
  item via `scroll → re-read → evaluate`. *Task: select the ~500th item in a huge Explorer folder / large
  list.* (Generalizes the off-screen-tab catch-22 ‡ found today: `select` and `scroll_into_view` both
  refuse off-screen elements; recovery is `focus_window` + keyboard, e.g. Ctrl+9 "last tab".)
- **Context menus as separate top-level popup windows** † — right-click menus spawn as their own window,
  not a child. *Task: right-click a file → Properties (find the ephemeral popup window).*
- **Dynamic/loading UI** † — placeholder/ghost elements, `IsEnabled=false`/`IsOffscreen=true` during a
  fetch; test wait-for-state (`desktop_wait_for` / `wait_for_text`) robustness.
- **Focus-stealing / z-order** † — re-assert `focus_window` (check `foregroundGained`) before a critical
  input when another window may steal foreground.
- **Reactive editors** ‡ — Win11 Notepad RichEdit garbles `desktop_type`; use `desktop_set_value`
  (ValuePattern) or `desktop_paste_text`. Verified live.
- **Opaque Chromium/Electron** — `snapshot` returns one empty `wakeable:true` node; `wake_accessibility`
  first, else `find_text`/`click_at`.
- **Terminals & background TUIs** ‡ — `read_terminal_tab`; a tab title is a fast hint but **not** a filter
  (an app that doesn't set its title hides behind the launcher name, e.g. nano under a "PowerShell" tab).
- **Foreground-lock**, **save/open common dialogs**, **clipboard round-trip**, **drag-drop**.

## 5. Loop integration — structured observations (one change to the loop)

When a curriculum task triggers `flaui-learn`, the observation is forced into a **structured shape** (agy)
rather than a free-text anecdote:

> **App-Framework · Trigger-Condition · Failure-Mode · Recovery/Workaround**

`flaui-curate` then applies its existing triage with one sharpened rule: **an observation with no clear
workaround is NOT promoted to GROWTH — it routes to `fix-the-tool` as a tool defect** (+ a regression
test), consistent with the current driver/deterministic triage. Observations *with* a workaround become
driver heuristics (GROWTH).

**Enforcement (hardening — agy Defect 1):** the 4-field shape must be *mandated*, not merely suggested, or
it degrades to free text across sessions. `flaui-learn` is a markdown **skill** (not a tool with typed
arguments — correcting agy's "tool schema" framing), so the plan updates the **`flaui-learn` skill's
capture rule** to require the fixed template `App-Framework · Trigger · Failure-Mode · Recovery` (and to
reject a bare free-text anecdote), and each curriculum task's `observe:` field pre-structures those four
fields so the capturing agent fills a template rather than composing prose.

## 6. Terminal outcome — graduate to SEED and ship

The GROWTH region is machine-owned and capped (≤30 lines). The curriculum's *end state* is to promote the
strongest, verified rules OUT of GROWTH into the skill's **hand-authored SEED floor**:

1. Curriculum runs accumulate verified heuristics in GROWTH.
2. On the GROWTH cap or when a rule is proven, `flaui-curate` appends it to
   `.claude/flaui-mcp/graduation-candidates.md` (the existing graduate-to-floor hook).
3. A human folds graduated rules into the **SEED** of `driving-flaui-mcp/SKILL.md` — organized as the
   "minefield map" (by framework) with the ranked targeting ladder.
4. `build-plugin.ps1` snapshots the enriched skill into the plugin; a flaui-mcp **release** ships it to
   both agents' installed copies and to end users. This is the user's stated intent: *use the gathered
   knowledge as seed and ship it.*

**Drain (hardening — agy Defect 2):** graduation must not leave GROWTH pinned at its ≤30-line cap.
`flaui-curate` already regenerates GROWTH wholesale each run **minus any rule now stated in the SEED floor**
(its "don't duplicate the floor" rule), so a graduated rule is **removed from GROWTH on the first curate
run after a human folds it into the SEED**. In the interim (candidate appended, not yet folded), curate's
existing cap-breach procedure (compress/merge/supersede, then drop the lowest-leverage rule) keeps GROWTH
≤30 — so there is no drop-everything stall; the graduation→fold→dedup drain is the intended
capacity-recovery path and the plan states it explicitly.

## 7. Non-goals

- **No scoring/regression harness for the driving skill.** Grading agent driving is out of scope; tool
  **code** correctness stays in flaui-mcp's xUnit suite (reached via `fix-the-tool` entries + tests).
  (This drops an earlier "light pass/fail" idea — agy's correction, accepted.)
- **No new tool code as part of this design.** The curriculum may *surface* tool defects (routed to
  `fix-the-tool`), but building those fixes is separate work.
- **No unattended synthetic-input runner.** The input tier stays human-supervised until the roadmap's
  dedicated-hardware runner (A1b) exists.

## 8. Target skill shape & the two mutual corrections

**Shape the skill should evolve toward** (agy's "happy" criteria):
- **Minefield map** — organized by app-type / UI-framework (know immediately: Chromium ⇒ wake first).
- **Ranked targeting ladder** — a fallback order for resolving a control.
- **Predictability** — know *before* calling a tool whether it's likely to hang/fail in this UI context,
  so the workaround is applied pre-emptively.

**Corrections recorded** (per driver-guidance: verify what the peer volunteers):
- *agy → me:* drop the scoring harness (accepted; §7).
- *me → agy:* agy proposed a **"Tree Path" selector** as a fallback; flaui-mcp has **no** tree-path
  selector. The real ladder against flaui-mcp's actual API is: **`automationId` → `name`+`controlType`
  (+`scope`) → a fresh `desktop_snapshot` ref → coordinate tools (`find_text`/`click_at`) for opaque
  surfaces.** The spec uses this real ladder.

## 9. v1 scope & first step

Draft `training-curriculum.md` with **~3–5 read-only tasks + ~2 lease-gated input tasks** targeting the
traps above (virtualized list, context-menu popup, opaque wake, terminal tab-find, reactive-editor input,
focus-steal). **Run it once manually**, capture structured observations, let `flaui-curate` fold/route
them — *then* decide whether any thin dispatch helper is worth adding. No automation until the raw
interactions have been felt.

## 10. Approval gate — joint consumer sign-off (governance)

Because the deliverable is a skill that **both agents consume**, approval is NOT the usual
implementor/reviewer defect-gate alone. The **design** and the resulting **implementation plan** must each
pass a **joint consumer sign-off**: both consuming agents — **Claude Code** and the **agy peer** — must
approve **as consumers** ("will this actually make *me* drive better?"), not merely as implementors hunting
defects. Either consumer's substantive objection **blocks progression** until reconciled.

Mechanically:
- The **AGY-AFTER adversarial panel** carries agy's verdict, and that verdict must include an explicit
  **consumer acceptance** — a GO only if agy, as a driver of these tools, accepts the artifact serves its
  own driving — in addition to any defect findings.
- **Claude records its own consumer sign-off** (same question, its own perspective).
- Disagreement is **negotiated, not folded-or-dismissed** (verify claims by measurement; the peer can be
  confidently wrong).
- The **operator (user) holds the final gate** — the consumer sign-offs inform, they do not replace, the
  user's decision.
- This joint gate **re-runs at the spec stage and again at the plan stage**, before any implementation.
- **Un-summarizable verdict protocol (hardening — agy Defect 3; anti-gaming, anti-deadlock):** each
  consumer emits a raw, verbatim verdict token — `[VERDICT: GO]` or `[VERDICT: BLOCK — REASON: <string>]`.
  Claude (the aggregator) is **forbidden from paraphrasing a BLOCK or from proceeding** while any consumer's
  raw output contains `[VERDICT: BLOCK]`; a BLOCK **halts and escalates to the operator immediately**.
  Exactly **one** substantive negotiation exchange is allowed on a disagreement; if still unresolved, it
  escalates to the operator (who holds the final gate) — never an unbounded loop.
