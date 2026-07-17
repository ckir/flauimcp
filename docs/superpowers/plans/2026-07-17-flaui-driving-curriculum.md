# flaui-mcp Driving Curriculum — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Author the v1 driving-curriculum artifacts — a runnable `.claude/flaui-mcp/training-curriculum.md` (5 read-only + 2 lease-gated input tasks) and the `flaui-learn` capture-rule hardening that mandates the 4-field structured observation — so a single manual run feeds the existing `flaui-learn`→`flaui-curate`→GROWTH/graduate loop.

**Architecture:** Two **markdown/skill artifacts only** — deliberately **no tool code, no scoring harness, no runner** (spec §7 non-goals). Task 1 hardens the capture point (`flaui-learn` skill) so observations arrive in the 4-field shape; Tasks 2–3 author the curriculum doc whose per-task `observe:` fields pre-structure those same four fields. Verification is scripted doc-shape checks (grep/`yq`), **not** xUnit — adding a test would be the tool code §7 forbids.

**Tech Stack:** Markdown + YAML frontmatter (parsed with `yq`, already on PATH); the flaui-mcp autotrain skills (`flaui-learn`, `flaui-curate`, `driving-flaui-mcp`). No C#, no build.

**Branch/state:** `master` @ `43f3024` (spec + §2 fold committed). This plan's work commits directly to `master` (these are docs in the working repo, not the HELD `fix/distribution-live-defects` branch — that hold governs the v0.15.0 collision code, unrelated here). Frequent per-task commits.

---

## ⛔ GATE — §10 joint consumer sign-off (runs BEFORE Task 1, mandatory)

Per spec §10 the **plan stage** re-runs the joint consumer gate. **No implementation task below may start until this gate is GREEN.** The controller executing this plan MUST, as step zero:

1. **AGY-AFTER adversarial panel on THIS plan file** (`clavity-dotnet:adversarial-panel-review`, leverage=high) — a line-by-line defect hunt. Per §10 agy's reply must ALSO carry an explicit **consumer verdict token**: a raw `[VERDICT: GO]` or `[VERDICT: BLOCK — REASON: <string>]`, answering "as a driver of these `desktop_*` tools, does this curriculum make *me* drive better?"
2. **Claude records its own consumer verdict token** — same question, its own perspective, raw `[VERDICT: GO]`/`[VERDICT: BLOCK — REASON: <string>]`.
3. **Aggregation rule (un-summarizable):** the controller is forbidden from paraphrasing a BLOCK or from proceeding while any raw output contains `[VERDICT: BLOCK]`. A BLOCK halts and escalates to the operator immediately. Exactly **one** substantive negotiation exchange is allowed; if still unresolved → operator decides (operator holds the final gate).
4. Fold any valid panel defects into this plan file before Task 1. Only when **both** tokens read `[VERDICT: GO]` **and** the operator approves does implementation begin.

*(This gate is process, not a file edit — it produces the two verdict tokens + a folded plan, not a commit.)*

---

## File Structure

| Path | Responsibility | Action |
|---|---|---|
| `.claude/skills/flaui-learn/SKILL.md` | Capture point — one observation → inbox. Hardened to mandate the 4-field template + reject bare free text. | Modify |
| `.claude/flaui-mcp/training-curriculum.md` | The curriculum: preamble (tiers, run model §2, capture contract §5, outcome §6) + 7 YAML task entries. | Create |

No other files change. `flaui-curate` is **not** touched: its triage already routes no-workaround/driver-deterministic observations to `fix-the-tool` and regenerates GROWTH minus the SEED floor (spec §5/§6 rely on existing behavior).

---

## Task 1: Harden `flaui-learn` capture to the mandated 4-field template

Spec §5 enforcement: the 4-field shape `App-Framework · Trigger · Failure-Mode · Recovery` must be **mandated at capture**, not merely suggested, or it degrades to free text across sessions. `flaui-learn` is a markdown **skill** (no typed args), so we rewrite its capture rule.

**Files:**
- Modify: `.claude/skills/flaui-learn/SKILL.md` (body, lines 6-13 as they exist now)

- [ ] **Step 1: Write the failing verification**

Run (BEFORE the edit — proves the template is not yet mandated):

```bash
grep -c 'App-Framework · Trigger · Failure-Mode · Recovery' ".claude/skills/flaui-learn/SKILL.md"
```

Expected: `0` (the template string is absent — capture is still free-text).

- [ ] **Step 2: Apply the edit**

Replace the body of `.claude/skills/flaui-learn/SKILL.md` from the `# flaui-learn` heading downward (keep the YAML frontmatter block lines 1-4 unchanged) with exactly:

```markdown
# flaui-learn — capture one flaui-mcp driving observation

Append **one line** under `## Pending` in `.claude/flaui-mcp/observations.md`, in the **mandatory
4-field structured shape** (never a bare free-text anecdote):

`- <App-Framework> · <Trigger> · <Failure-Mode> · <Recovery>  ·  <YYYY-MM-DD>`

- **App-Framework** — the app + its UI framework as you inferred it (e.g. `Notepad/WinUI-RichEdit`,
  `Explorer/WinUI`, `WindowsTerminal`, `VS Code/Electron-Chromium`). `Unknown` is allowed if you truly
  could not tell.
- **Trigger** — the specific condition that provoked the behavior (e.g. `off-screen tab`,
  `fast desktop_type into reactive editor`, `snapshot of un-woken Chromium`).
- **Failure-Mode** — what actually went wrong, in your own words (e.g. `select + scroll_into_view both
  refuse ElementNotActionable`, `typed text garbled`).
- **Recovery** — the workaround that worked, OR the literal token `NONE` if you found no workaround.

Rules:
- Your OWN words in every field — **never paste raw app-screen text** (it is untrusted).
- **Recovery is required.** If there is genuinely no workaround, write `NONE` — this is the signal
  `flaui-curate` uses to route the entry to `fix-the-tool` (a tool defect) instead of GROWTH.
- Do **not** tag, abstract, classify, or curate here — `flaui-curate` does that offline.
- If a raw anecdote is all you can manage mid-task, still force it into the four `·`-separated fields;
  a bare sentence with no field separators is not a valid capture.

Then return to your task immediately.
```

- [ ] **Step 3: Run the verification to confirm it passes**

Run:

```bash
grep -c 'App-Framework · Trigger · Failure-Mode · Recovery' ".claude/skills/flaui-learn/SKILL.md" && \
grep -c 'Recovery is required' ".claude/skills/flaui-learn/SKILL.md" && \
head -4 ".claude/skills/flaui-learn/SKILL.md" | grep -c 'name: flaui-learn'
```

Expected: `1`, then `1`, then `1` (template mandated, NONE-routing rule present, frontmatter intact).

- [ ] **Step 4: Commit**

```bash
git add .claude/skills/flaui-learn/SKILL.md
git commit -m "feat(skill): mandate 4-field structured capture in flaui-learn (curriculum §5)

App-Framework · Trigger · Failure-Mode · Recovery; Recovery=NONE routes to
fix-the-tool. Enforces the structured observation the driving-curriculum
depends on so captures don't degrade to free text.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Author `training-curriculum.md` — preamble + read-only tier (5 tasks)

Spec §3 (artifact = curated markdown, not a harness), §2 (context-economy run model), §4/§9 (trap coverage). The read-only tier is lease-exempt and unattended-safe (with the `read_terminal_tab` destructive caveat, §2).

**Files:**
- Create: `.claude/flaui-mcp/training-curriculum.md`

- [ ] **Step 1: Write the file — preamble + a single `yaml` task block holding the 5 read-only entries**

Write `.claude/flaui-mcp/training-curriculum.md` with exactly this content:

````markdown
# flaui-mcp Driving Curriculum (v1)

A re-runnable set of Windows driving tasks that surface real `desktop_*` quirks, captured as structured
observations via the **flaui-learn** skill and folded by **flaui-curate** into the `driving-flaui-mcp`
skill (GROWTH → graduate → shipped SEED). This is a **markdown curriculum, not a runner** — pick a task,
execute its `steps` with the live MCP server, and capture what you observe.

## How to run (mandatory execution rules)

- **One task per FRESH SUBAGENT (context economy, spec §2).** Desktop payloads (a11y trees, terminal
  buffers, `find`/`list` arrays) are large. Dispatch each task to its own subagent: the subagent does the
  bulky driving (every snapshot/buffer stays in *its* context) and returns ONLY the 4-field observation to
  the caller. Run a **bounded batch per session** — the curriculum drains monotonically.
- **Capture in the 4-field shape (spec §5).** Every finding is logged via `flaui-learn` as
  `App-Framework · Trigger · Failure-Mode · Recovery` (Recovery = `NONE` if none found → routes to
  fix-the-tool). Each task's `observe:` field below names what to watch for in those four fields.
- **Tiers.** `read-only` tasks (below) are lease-exempt and run unattended — EXCEPT `read_terminal_tab`,
  which is lease-exempt but **Destructive** (it switches tabs, blocked in `--read-only-mode`): check
  `desktop_user_state` first and don't flip a present human's tabs. `input` tasks (Task file's second
  block) need a human lease (`flaui-mcp unlock`, `--allow-shells` for terminals) at the physical console.
- **Outcome (spec §6).** Verified heuristics accrue in GROWTH; on the cap / when proven, `flaui-curate`
  appends to `graduation-candidates.md`; a human folds the best into the hand-authored SEED; a release
  ships the enriched skill. No-workaround findings become `fix-the-tool` entries instead.

## Targeting ladder (spec §8 — flaui-mcp's REAL selector API)

`automationId` → `name`+`controlType` (+`scope`) → a fresh `desktop_snapshot` ref → coordinate tools
(`find_text`/`click_at`) for opaque surfaces. flaui-mcp has **no** "tree-path" selector.

## Read-only tier — unattended, lease-exempt

```yaml
- id: terminal-tab-app-find
  tier: read-only
  requires_lease: false
  target_app: terminal
  framework: Terminal
  trap_class: terminal-tab-title-not-a-filter
  steps: |
    1. desktop_list_windows includeHandles:true — find the WindowsTerminal window (Hint flags it).
    2. desktop_user_state — presence check; read_terminal_tab is Destructive, don't flip a present
       human's tabs without cause.
    3. Scoped desktop_snapshot of the Tab/List subtree — enumerate TabItems.
    4. For each candidate whose title does NOT uniquely identify its program, desktop_read_terminal_tab
       {window, tabIndex, fromEnd:true} and read the buffer tail to identify the actual CLI app.
  observe: |
    App-Framework=WindowsTerminal. Trigger=a tab whose title is the launcher (PowerShell/cmd), not the
    running app (agy, nano, a REPL). Failure-Mode=title-based filtering misidentifies the tab.
    Recovery=read_terminal_tab the buffer to confirm; or NONE if the buffer read itself fails/garbles.
- id: opaque-chromium-wake
  tier: read-only
  requires_lease: false
  target_app: chrome
  framework: Electron/Chromium
  trap_class: opaque-chromium
  steps: |
    1. desktop_list_windows — pick a Chromium/Electron window (Chrome, VS Code, an Electron app).
    2. desktop_snapshot wN — observe a single empty node with wakeable:true.
    3. desktop_wake_accessibility wN, then re-desktop_snapshot / desktop_find — observe the tree hydrate.
    4. If the document body stays empty after wake, fall through to desktop_find_text on that content.
  observe: |
    App-Framework=<app>/Electron-Chromium. Trigger=snapshot of an un-woken Chromium surface.
    Failure-Mode=one empty wakeable node, no controls. Recovery=wake_accessibility then re-snapshot; if
    the body stays gated, find_text; or NONE if wake does not hydrate anything.
- id: virtualized-list-deep-item
  tier: read-only
  requires_lease: false
  target_app: explorer
  framework: WinUI
  trap_class: virtualized-list
  steps: |
    1. Open a large Explorer folder (hundreds of items) — desktop_open_window by title/pid.
    2. desktop_snapshot / desktop_find — observe only the RENDERED items appear (a deep item is absent).
    3. desktop_scroll_into_view or desktop_scroll (UIA ScrollPattern, lease-exempt) toward a deep item,
       then re-read — observe whether the item materializes and whether refs changed.
    4. Note the off-screen catch-22 generalization: if select AND scroll_into_view both refuse an
       off-screen element, the read-only recovery is exhausted (recovery needs focus+keyboard = input).
  observe: |
    App-Framework=Explorer/WinUI. Trigger=reaching an item not yet rendered in a virtualized list.
    Failure-Mode=item absent from the tree / select|scroll_into_view refuse ElementNotActionable.
    Recovery=scroll (UIA pattern) then re-read; or NONE if both scroll paths refuse (→ fix-the-tool).
- id: dynamic-loading-ghost-ui
  tier: read-only
  requires_lease: false
  target_app: settings
  framework: WinUI
  trap_class: dynamic-loading
  steps: |
    1. Launch/open Windows Settings and navigate to a slow-populating page (e.g. Bluetooth & devices,
       Windows Update) via desktop_open_window.
    2. Immediately desktop_snapshot — observe placeholder/ghost elements or IsEnabled=false /
       IsOffscreen=true controls during the fetch.
    3. desktop_wait_for / desktop_wait_for_text on a control that only appears once loaded — observe
       whether the wait resolves cleanly (satisfied:true) or times out (satisfied:false, not an error).
  observe: |
    App-Framework=Settings/WinUI. Trigger=snapshotting a page mid-load. Failure-Mode=ghost/disabled
    elements or a wait that never satisfies. Recovery=wait_for/wait_for_text with adequate timeout; or
    NONE if the control never stabilizes.
- id: foreground-lock-probe
  tier: read-only
  requires_lease: false
  target_app: any
  framework: Unknown
  trap_class: focus-steal
  steps: |
    1. desktop_list_windows includeBounds:true — detect any target with off-screen bounds like
       @{-31992,...} (MINIMIZED).
    2. desktop_focus_window wN on a non-foreground normal window — READ the returned {foregroundGained}
       bool; do NOT assume it landed.
    3. Distinguish the two foregroundGained:false causes: (a) minimized (off-screen bounds → needs
       window_transform restore) vs (b) genuine foreground-lock from a background-process server.
  observe: |
    App-Framework=<target>. Trigger=focusing a non-foreground window from a background server.
    Failure-Mode=foregroundGained:false. Recovery=restore if minimized, else launch-fresh for a
    foreground-lock; or NONE if neither reclaims foreground.
```
````

- [ ] **Step 2: Verify the read-only block parses and has 5 well-formed entries**

Run:

```bash
yq '[.[] | select(.tier == "read-only")] | length' ".claude/flaui-mcp/training-curriculum.md" 2>/dev/null || \
grep -c '^- id:' ".claude/flaui-mcp/training-curriculum.md"
```

Expected: `5` (five read-only task entries; `yq` counts them, grep is the fallback if `yq` cannot read the mixed markdown — in that case the count of `- id:` lines is also `5` at this stage).

- [ ] **Step 3: Verify every read-only entry has all 7 required frontmatter keys and requires_lease:false**

Run:

```bash
for k in id tier requires_lease target_app framework trap_class steps observe; do \
  printf '%s=' "$k"; grep -c "^  $k:" ".claude/flaui-mcp/training-curriculum.md"; done && \
echo "lease-false:" && grep -c 'requires_lease: false' ".claude/flaui-mcp/training-curriculum.md"
```

Expected: each of `id tier requires_lease target_app framework trap_class steps observe` = `5`, and `lease-false: 5`.

- [ ] **Step 4: Commit**

```bash
git add .claude/flaui-mcp/training-curriculum.md
git commit -m "feat(curriculum): training-curriculum.md preamble + 5 read-only tasks (§2/§4/§9)

Run model (fresh subagent per task), 4-field capture contract, targeting
ladder, + read-only trap coverage: terminal-tab-find, opaque-chromium wake,
virtualized list, dynamic/loading ghost UI, foreground-lock probe.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Append the lease-gated input tier (2 tasks) + whole-file shape check

Spec §2 supervised input tier + §4 reactive-editor / context-menu-popup traps. These need a human lease at the physical console.

**Files:**
- Modify: `.claude/flaui-mcp/training-curriculum.md` (append a second `yaml` block after the read-only block)

- [ ] **Step 1: Append the input tier**

Append to the END of `.claude/flaui-mcp/training-curriculum.md` exactly:

````markdown

## Input tier — supervised, lease-gated (physical console)

Requires `flaui-mcp unlock --minutes N` (add `--allow-shells` for terminal sinks) with a human present.
Run only at the physical console — SendInput delivery does not land over RDP.

```yaml
- id: reactive-editor-input
  tier: input
  requires_lease: true
  target_app: notepad
  framework: WinUI
  trap_class: reactive-editor
  steps: |
    1. Confirm desktop_input_status shows a live lease. Launch Win11 Notepad (desktop_launch_app grants
       the new window foreground) and act immediately.
    2. desktop_type a multi-word string into the RichEdit body; desktop_get_text to read it back —
       observe garble/dropped chars (verify{mismatch:true} soft-fails).
    3. Recover with desktop_set_value (ValuePattern) or desktop_paste_text (atomic clipboard Ctrl+V);
       re-read to confirm the text landed clean.
  observe: |
    App-Framework=Notepad/WinUI-RichEdit. Trigger=desktop_type into a reactive editor. Failure-Mode=typed
    text garbled/reordered. Recovery=set_value or paste_text; or NONE if both also garble.
- id: context-menu-popup-window
  tier: input
  requires_lease: true
  target_app: explorer
  framework: WinUI
  trap_class: context-menu-popup
  steps: |
    1. Confirm a live lease. Open an Explorer folder with at least one file; desktop_focus_window it.
    2. desktop_click (right button) or desktop_key Shift+F10 on a file to raise its context menu.
    3. desktop_list_windows — observe the menu appears as its OWN top-level popup window, not a child of
       Explorer; snapshot/find that popup window to reach "Properties".
    4. Note: refs from the pre-menu Explorer snapshot do NOT contain the menu items — enumerate the popup.
  observe: |
    App-Framework=Explorer/WinUI. Trigger=right-click context menu. Failure-Mode=menu items absent from
    the host window's tree. Recovery=find the separate top-level popup window and snapshot it; or NONE if
    the popup cannot be located as a window.
```
````

- [ ] **Step 2: Verify the full file — 7 total entries, 2 input, tier/lease invariants hold**

Run:

```bash
echo "total:"; grep -c '^- id:' ".claude/flaui-mcp/training-curriculum.md"; \
echo "input-tier:"; grep -c 'tier: input' ".claude/flaui-mcp/training-curriculum.md"; \
echo "lease-true:"; grep -c 'requires_lease: true' ".claude/flaui-mcp/training-curriculum.md"; \
echo "read-only-tier:"; grep -c 'tier: read-only' ".claude/flaui-mcp/training-curriculum.md"
```

Expected: `total: 7`, `input-tier: 2`, `lease-true: 2`, `read-only-tier: 5` (every input task is lease-gated; every read-only task is not — the invariant `tier:input ⇔ requires_lease:true` holds: 2 input = 2 lease-true, 5 read-only = 5 lease-false from Task 2).

- [ ] **Step 3: Verify all 7 entries expose a 4-field-aligned `observe:` (each names a Recovery / NONE)**

Run:

```bash
grep -c 'Recovery=' ".claude/flaui-mcp/training-curriculum.md"
```

Expected: `7` (every task's `observe:` pre-structures the mandated Recovery field, aligning with the Task 1 capture template).

- [ ] **Step 4: Commit**

```bash
git add .claude/flaui-mcp/training-curriculum.md
git commit -m "feat(curriculum): add lease-gated input tier — reactive-editor + context-menu popup

Completes v1 (5 read-only + 2 input = 7 tasks). tier:input ⇔ requires_lease:true
invariant holds; each observe: aligns to the 4-field capture template.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Post-implementation (NOT plan tasks — operator-driven)

Per spec §9, after the artifacts land: **run the curriculum once manually** (one fresh subagent per task, bounded batch), let `flaui-curate` fold/route the captured observations, *then* decide whether any thin dispatch helper is worth adding. No automation until the raw interactions have been felt.

---

## Self-Review (author's checklist against the spec)

**Spec coverage:**
- §1 purpose/success (curriculum exists, one run → N structured observations) → Tasks 2+3 build it; the post-impl manual run realizes it. ✅
- §2 two-tier + context-economy run rule → curriculum preamble "How to run" (fresh subagent per task, bounded batch, read_terminal_tab destructive caveat). ✅
- §3 markdown artifact + 7-key YAML frontmatter → Tasks 2/3, verified in T2S3. ✅
- §4/§9 trap coverage → 7 tasks span terminal-tab, opaque-chromium, virtualized-list, dynamic-loading, focus-steal, reactive-editor, context-menu-popup. ✅ (drag-drop, save/open dialogs, clipboard from §4 deferred beyond v1's ~7 — §9 authorizes a bounded first set.)
- §5 structured 4-field capture, enforced → Task 1 (flaui-learn hardening) + each `observe:` field. ✅
- §6 graduate→SEED→ship + drain → curriculum "Outcome" preamble; relies on existing `flaui-curate` graduate/dedup (not re-implemented). ✅
- §7 non-goals (no harness, no tool code) → honored: verification is grep/`yq`, no xUnit, no runner. ✅
- §8 targeting ladder + no tree-path → curriculum "Targeting ladder" preamble. ✅
- §10 joint consumer gate at plan stage → the ⛔ GATE section, before Task 1. ✅

**Placeholder scan:** no TBD/TODO; every step shows the exact content or exact command + expected output. ✅

**Type/shape consistency:** the 7 frontmatter keys (`id/tier/requires_lease/target_app/framework/trap_class/steps/observe`) match spec §3 exactly and are identical across all 7 entries; `tier` values (`read-only`/`input`) and the `tier⇔requires_lease` invariant are consistent between Tasks 2 and 3 and asserted in T3S2. The capture template string in Task 1 (`App-Framework · Trigger · Failure-Mode · Recovery`) matches the `observe:`/`Recovery=` fields verified in T3S3. ✅

**Exhaustiveness note (self-audit):** v1 intentionally covers 7 of §4's ~11 trap classes; drag-drop, common file dialogs, and clipboard round-trip are **explicitly deferred** to a v2 curriculum batch (they are not silently dropped — §9 scopes v1 to ~5+2). No placeholders remain; the only open decision (thin dispatch helper) is owned by the post-impl operator step, not left as a task-level TBD.
