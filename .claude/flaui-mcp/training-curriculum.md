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
  `App-Framework · Trigger · Failure-Mode · Recovery` (Recovery = `NONE` if none found — a signal
  `flaui-curate` weighs when deciding routing). Each task's `observe:` field below names what to watch
  for in those four fields.
- **Tiers rest on TWO independent axes — don't conflate them:**
  - **Lease axis** (`requires_lease`): only SendInput tools (`type`/`set_value`/`paste_text`/`key`/
    `click`/`drag`) need a human lease (`flaui-mcp unlock`). UIA-pattern tools are lease-EXEMPT.
  - **Destructive axis** (blocked in `--read-only-mode`, and visibly perturbs the UI): every
    state-changing tool is `Destructive`. Crucially, several UIA tools are **lease-exempt yet
    Destructive** — `read_terminal_tab`, `scroll`, `scroll_into_view`, `select`, `focus_window`,
    `window_transform`. They need no lease but are **blocked under `--read-only-mode`** and disturb a
    watching human.
  - **Rule:** a `lease-exempt`-tier task (`requires_lease:false`) is genuinely unattended-safe ONLY if it
    ALSO has `destructive:false` — i.e. it uses pure reads (`list_windows`/`snapshot`/`find`/`get_text`/
    `wake`/`watch`/`wait_for*`). The unattended filter is therefore **`tier==lease-exempt AND
    destructive==false`**, never `tier` alone. A `lease-exempt`-tier task with `destructive:true` invokes
    a lease-exempt-but-Destructive tool and is **supervised / solo-console**: check `desktop_user_state`
    first, don't perturb a present human, and it will fail under `--read-only-mode`.
- **`input` tasks** (second block) need a human lease (`flaui-mcp unlock`, `--allow-shells` for
  terminals) at the physical console.
- **Outcome (spec §6).** Verified heuristics accrue in GROWTH; on the cap / when proven, `flaui-curate`
  appends to `graduation-candidates.md`; a human folds the best into the hand-authored SEED; a release
  ships the enriched skill. No-workaround findings become `fix-the-tool` entries instead.

## Targeting ladder (spec §8 — flaui-mcp's REAL selector API)

`automationId` → `name`+`controlType` (+`scope`) → a fresh `desktop_snapshot` ref → coordinate tools
(`find_text`/`click_at`) for opaque surfaces. flaui-mcp has **no** "tree-path" selector.

## Lease-exempt tier — no unlock needed (pure-read tasks unattended; destructive-tool tasks supervised)

```yaml
- id: terminal-tab-app-find
  tier: lease-exempt
  requires_lease: false
  destructive: true
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
  tier: lease-exempt
  requires_lease: false
  destructive: false
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
  tier: lease-exempt
  requires_lease: false
  destructive: true
  target_app: explorer
  framework: WinUI
  trap_class: virtualized-list
  steps: |
    1. Open a large Explorer folder (hundreds of items) — desktop_open_window by title/pid.
    2. desktop_snapshot / desktop_find — observe only the RENDERED items appear (a deep item is absent).
    3. desktop_scroll_into_view or desktop_scroll (UIA ScrollPattern, lease-exempt) toward a deep item,
       then re-read — observe whether the item materializes and whether refs changed.
    4. Note the off-screen catch-22 generalization: if select AND scroll_into_view both refuse an
       off-screen element, the lease-exempt recovery is exhausted (recovery needs focus+keyboard = input).
  observe: |
    App-Framework=Explorer/WinUI. Trigger=reaching an item not yet rendered in a virtualized list.
    Failure-Mode=item absent from the tree / select|scroll_into_view refuse ElementNotActionable.
    Recovery=scroll (UIA pattern) then re-read; or NONE if both scroll paths refuse (→ fix-the-tool).
- id: dynamic-loading-ghost-ui
  tier: lease-exempt
  requires_lease: false
  destructive: false
  target_app: settings
  framework: WinUI
  trap_class: dynamic-loading
  steps: |
    1. Attach to an already-open Windows Settings on a slow-populating page (e.g. Bluetooth & devices,
       Windows Update) via desktop_open_window (by title/pid — it attaches read-only, it does NOT launch;
       open the page by hand first if needed, so this task stays destructive:false).
    2. Immediately desktop_snapshot — observe placeholder/ghost elements or IsEnabled=false /
       IsOffscreen=true controls during the fetch.
    3. desktop_wait_for / desktop_wait_for_text on a control that only appears once loaded — observe
       whether the wait resolves cleanly (satisfied:true) or times out (satisfied:false, not an error).
  observe: |
    App-Framework=Settings/WinUI. Trigger=snapshotting a page mid-load. Failure-Mode=ghost/disabled
    elements or a wait that never satisfies. Recovery=wait_for/wait_for_text with adequate timeout; or
    NONE if the control never stabilizes.
- id: foreground-lock-probe
  tier: lease-exempt
  requires_lease: false
  destructive: true
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
