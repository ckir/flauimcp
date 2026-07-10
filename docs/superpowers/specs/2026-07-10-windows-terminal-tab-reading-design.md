# Reading a background Windows Terminal tab (and another agent's TUI)

**Date:** 2026-07-10
**Status:** Approved design (spec) — ready for implementation planning
**Supersedes:** the "Proposed capability / Suggested tool shape" section of `TOFIIX.md` §1
**Consulted:** agy (Antigravity peer) — recommended and re-confirmed the Hybrid approach on all open forks

---

## 1. Problem

A Windows Terminal (WT) window is **one top-level UIA window that multiplexes many tabs**, each a
separate ConPTY child process. This breaks the naive "one window = one thing on screen" assumption that
`desktop_*` callers make:

- `desktop_list_windows` reports the WT window as a **single** entry whose `Title` is only the **active
  tab's** title. The other tabs are invisible in that output.
- Only the **active** pane's text buffer is realized in the UIA tree (as `Custom → Text`). A non-active
  tab's buffer is virtualized away entirely until its tab is selected.

**The harm this caused (the motivating incident):** an agent in another session tried to read the reply of
a peer CLI (`agy`) that was running in a **non-active** WT tab, saw no buffer for it, and wrongly concluded
"agy was running headless." It was not — agy was live in a background tab. The agent trusted a partial,
title-level view of a multiplexed window and drew a false conclusion.

## 2. Key discovery — this is a discoverability gap, not a capability gap

The primitives to do this **already exist and were validated live against the real desktop (2026-07-10):**

| Step | Existing tool | Verified behavior |
|---|---|---|
| Enumerate tabs | `desktop_snapshot` | Walks `Window → Tab → List → TabItem[]`; each `TabItem` carries `SelectionItem`, a child `Text` (title), and a `Button "Close Tab"`. |
| Activate a background tab | `desktop_select` (UIA `SelectionItemPattern.Select`) | Switched tabs successfully; the newly-active pane's buffer became realized. |
| Read the now-active buffer | `desktop_get_text` on the `Custom → Text` pane | Returned the real background-tab content (read a live `agy` CLI buffer). |
| Restore the user's tab | `desktop_select` on the original `TabItem` | Re-selected the originally-active tab; `desktop_list_windows` confirmed the restore. |

Because the capability is present, the fix is **orchestration + discoverability**, not new UIA plumbing.

## 3. Findings from live validation that shape the design

These were observed live and are the reason the design takes the shape it does.

1. **`desktop_select` is lease-exempt.** The entire read-a-background-tab flow worked with the synthetic-input
   lease **locked** (`leaseStatus:"locked", shells:false`). `desktop_select` is a UIA *pattern* call, not
   synthetic input, so it needs no lease and no `shells` capability. The current driving skill's claim that
   you must "click the `TabItem` — needs the `shells` lease" is **wrong**: `desktop_select` is the correct
   primitive and `desktop_click` (which *would* need the shells lease) is the wrong one.
   *(`desktop_select` is still marked `Destructive`, so it is blocked in `--read-only-mode` — see §6.)*

2. **`TabItem.Name` is ambiguous — title-match is unsafe, not merely brittle.** The live WT window had **two**
   tabs both named `C:\WINDOWS\system32\cmd.exe ` that were **two different `agy` instances** (v1.1.0 in
   `~/Development/Rust/clavity` vs v1.1.1 in `~/Development/c#/flauimcp`). `TabItem.Name` reflects the
   **launcher** (`cmd.exe`), not the running program. Consequences:
   - A `selector:{name:"cmd.exe"}` resolves to `AmbiguousMatch`.
   - There is **no title that uniquely locates a specific agent.** The only reliable way to identify which
     tab hosts which program is to **activate it and read its buffer.**
   - A dedicated `titleMatch`-based tool would *confidently return the wrong tab's data.*

3. **`desktop_select` can report `ActionBlockedPending` even when the switch succeeded.** One selection returned
   `{"error":"ActionBlockedPending","message":"...likely opened a modal dialog"}`, yet a follow-up snapshot
   showed the tab **had** switched and there was **no** modal. This is a timeout artifact of a slow pattern
   call. Recovery is to **snapshot and check actual state** — **never blind-retry** (a retry could double-toggle
   tabs and land on the wrong one).

4. **Refs fully change on every tab switch.** After a `desktop_select`, all prior `eN` refs for that window are
   stale; the active-pane content node is a **new** ref each time. Always re-snapshot after switching.

5. **`desktop_get_text` truncation keeps the HEAD, and TextPattern returns ~the visible viewport.** Validated
   live: a `maxLength:300` read returned exactly the same **prefix** as a `maxLength:8000` read of the same
   pane — so truncation keeps the first N chars and **drops the tail**. The pane's `TextPattern` returns
   roughly the **visible viewport** top-to-bottom (earlier scrollback that had scrolled off was absent), and a
   live terminal auto-scrolls to the bottom on new output. Consequence for the motivating use case ("read the
   peer's reply" = read the **latest** output): a small `maxLength` truncates the latest lines away. The
   consumer needs to read from the **end** of the pane text — see §5.4.

## 4. Approaches considered

- **A. Dedicated WT-aware tools** (`desktop_list_terminal_tabs` / `desktop_activate_terminal_tab` /
  `desktop_read_terminal_tab` with `restoreFocus`), as originally sketched in `TOFIIX.md`. **Rejected.** It
  hard-codes the WT tree shape (`Window→Tab→List→TabItem`, `Custom→Text`), which WinUI can shift across WT
  releases (brittle), **and** it leans on title-match, which finding §3.2 proves is ambiguous (unsafe). A
  macro-tool would confidently return wrong data.
- **B. Skill/recipe only** — no code change. **Rejected as insufficient on its own:** `desktop_list_windows`
  would still emit no signal that a WT window hides other tabs, so discovery keeps depending on the agent
  already knowing to look — exactly what failed in the motivating incident.
- **C. Hybrid (CHOSEN)** — a small, robust code hint on `desktop_list_windows` **plus** a rewritten skill
  recipe. Uses only existing primitives (resilient to WT UI drift), and the recipe forces buffer-level
  disambiguation (safe against title ambiguity). agy independently recommended C and re-confirmed it against
  the validation evidence.

## 5. Chosen design (Hybrid)

### 5.1 Code — a static capability hint on `desktop_list_windows`

When a listed window's `ProcessName == "WindowsTerminal"`, attach a **short static** hint string to that
window's entry — a *pointer*, not the recipe. e.g.:

> `"Multiplexed terminal — this shows ONLY the active tab; a WT window is NOT evidence a program is absent/headless. Snapshot to enumerate tabs; see skill driving-flaui-mcp."`

**Keep it short, but carry the anti-pattern nugget (change #1, refined by panel round 2: Mechanism Gamer).**
`desktop_list_windows` is called frequently, so the hint must stay to roughly one sentence — a verbose recipe
repeated on every listing is token-noise (agy: "verbose hints in a top-level enumeration tool destroy context
windows"). **But** the hint is the *only* code-side nudge an agent that never opens the skill will see, and the
original incident was exactly such an agent. So the hint must still carry the one load-bearing warning — *this
is only the active tab; a WT window is not evidence of "headless"* — not merely a bare "see the skill" pointer.
The full recipe still lives in the skill (§5.2), not in the tool output.

**Contracts / constraints:**

- **Pure Win32, no UIA.** The hint is keyed on the process-name string that `ListWindowsAsync` already
  computes (`SafeProcessName`, `WindowManager.cs`). It must **not** trigger a UIA tree walk. `desktop_list_windows`
  is deliberately pure-Win32/non-blocking (see the comment at `WindowManager.cs:73-75`); a UIA walk could block
  on an unresponsive window. Therefore **no live tab-count** — a count would require walking the tree. (agy Q2:
  "Static hint... mixing a UIA walk into a top-level enumeration tool is a severe performance anti-pattern.")
- **Shape.** Add one optional, null-when-absent field to the `WindowInfo` record (`WindowManager.cs:15-19`),
  following the existing `Bounds`/`ZOrder`/`Handle` pattern
  (`[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`). Suggested name: `Hint`. It is
  emitted only for recognized multiplexer processes and is `null` (omitted from JSON) for every other window,
  so existing output is unchanged for non-WT windows.
- **Recognition set (agy panel: Activation Auditor).** Match a **set**, not one literal: `WindowsTerminal`
  **and** `WindowsTerminalPreview` (the Preview channel is a distinct process). Measured on the current build:
  `.NET Process.ProcessName` returns `"WindowsTerminal"` with **no** `.exe` suffix, so exact-match on the bare
  name is correct today — but treat the recognition set as a small, documented, easily-extended list (a future
  rename or a new multiplexer just adds an entry). Generalizing to non-WT multiplexers (WezTerm, etc.) stays
  deferred until a second real case appears (scope-creep guard).
- **Tool description.** Extend the `desktop_list_windows` tool description (`WindowTools.cs`) to mention that a
  `Hint` may accompany multiplexer windows.

### 5.2 Skill — rewrite the "reading another agent's TUI" recipe

Rewrite the **"Terminals & reading another agent's TUI"** section of
`.claude/skills/driving-flaui-mcp/SKILL.md` to correct and complete it:

1. **Use `desktop_select`, not click; note it is lease-exempt.** Replace the current
   "click its `TabItem` — needs the `shells` lease" text. Activation is `desktop_select` on the target
   `TabItem` (UIA `SelectionItem.Select`), which works even while input is locked. `desktop_click` is the
   wrong primitive here (it *would* need the shells lease).
2. **The full recipe:** `desktop_snapshot` (enumerate `TabItem`s) → `desktop_select` target tab →
   **re-snapshot** (refs change!) → `desktop_get_text` the active `Custom → Text` pane →
   `desktop_select` the **originally-active** `TabItem` to restore. Record which tab was active up front.
3. **Enumerate ALL tabs and read EACH candidate.** `TabItem.Name` is the launcher, not the running program;
   identically-titled tabs can be **different** agents. To locate a specific program (e.g. a particular `agy`),
   activate and read each candidate — **never stop at the first title match**, and never trust a
   `selector:{name}` to disambiguate (it returns `AmbiguousMatch`).
4. **`ActionBlockedPending` is not a failure.** A `desktop_select` may report it while the switch actually
   succeeded. Recover by snapshotting and checking real state; **do not blind-retry.**
5. **Anti-pattern (the fix for the original harm):** *A `WindowsTerminal` window is never evidence that a
   program is "headless" or absent. One visible buffer means one active tab. Enumerate the tabs before
   concluding anything about what is or isn't running.*
6. **Prefer the programmatic channel; tab-reading is a FALLBACK (change #3).** When a peer exposes a proper
   channel (e.g. `agy` via clavity / `agy_ask`, or `agentmemory` IPC), use it — it's non-disruptive and
   unambiguous. Reading a peer by flipping WT tabs **visibly disrupts the user** and should be reserved for
   TUIs with **no** API. State this at the top of the recipe so agents don't tab-flip when a clean channel
   exists. (Both consumers agreed; agy: "documented as a fallback only when programmatic channels are
   unavailable.")
7. **No cheap tab discriminator exists — verified (change #4, resolved).** Live inspection with
   `fullProperties:true` showed WT `TabItem`s carry **no `AutomationId` and no `HelpText`** (both empty). So
   there is **no** stabler discriminator to prune candidates with: **activate-and-read is the only way to tell
   which tab hosts which program**, and a tab's only reasonably-stable identity is its **ordinal index** in the
   tab strip (with the caveats in item 8).
8. **Enumerate/anchor by CONTROL-TYPE structure, not WinUI ids (agy round 2: Dependency Cynic).** Do **not**
   mandate anchoring on `automationId:"TabView"/"TabListView"` — those are WinUI-internal ids subject to the
   very cross-version drift §4 rejected Option A for. Anchor on the **UIA control-type structure**
   (`Tab → List → TabItem[]`), which is drift-resistant; the container `AutomationId`s may be used as an
   *optional accelerator* only. If the expected control-type structure isn't found (an unrecognized WT/WinUI
   layout), the recipe must **report "unrecognized terminal layout"** and stop — never mis-act on a guessed
   tree.
9. **Restore identity: ordinal + verification, best-effort — treat restore as `finally` (change #5; agy
   round 1: FATAL fix; agy round 2: Axiom Breaker).** You **cannot** restore by re-using the pre-switch
   `TabItem` ref (stale after any switch — §3.4), by title alone (ambiguous — §3.2), or by an `AutomationId`
   (none exists — item 7). Ordinal index is the best identity but is **not** sufficient alone: a concurrent
   **drag-reorder** or a simultaneous **add+close** shifts ordinals while leaving the count unchanged. So:
   record the originally-active tab's **ordinal index _and_ its title/identity** up front; on restore,
   re-snapshot, re-enumerate `TabItem`s, and **verify the tab at the recorded ordinal still matches the
   recorded identity** before selecting it. Restore runs in a `finally`-equivalent (attempted on **any**
   mid-enumeration error/timeout) and is **best-effort with honest reporting**: if the count changed, the
   ordinal no longer matches the recorded identity, or `select`/`snapshot` errors during restore, do **not**
   silently continue or blindly select the ordinal — **report** which tab is now active and that restore did
   not confidently complete. Bounded (retry at most once); never an unbounded loop.
10. **Settle after activating — but NOT via `wait_for_stable` (agy round 2: Mechanism Gamer).** A
    freshly-activated pane may not have rendered/auto-scrolled to the bottom yet, so an immediate read can
    catch stale mid-scrollback text. **`desktop_wait_for_stable` is the WRONG tool here** — it keys on
    *structural* changes, and a terminal repainting its *text* buffer fires no structural event, so the wait
    returns instantly and "settles" nothing. Instead **re-read the buffer after an adequate delay and compare**
    (two reads that agree = settled); a sub-frame delay (e.g. <50 ms) will just return the same stale string,
    so the delay must be large enough for the ConPTY auto-scroll to land. *(The implementation plan must
    confirm what `desktop_wait_for_stable` actually keys on; if it does detect text changes on some builds,
    revisit — but do not assume it.)*
11. **Read the LATEST output, not the head (change #2) — and know its ceiling.** `desktop_get_text` truncation
    keeps the head and the pane returns ~the viewport (§3.5), so to capture a peer's most-recent reply, read
    from the **end** — `desktop_get_text ... fromEnd:true` (§5.4) or a viewport-sized `maxLength` sliced at the
    tail. **Hard limit (agy panel + Claude, verified):** TextPattern returns only ~the visible viewport, so a
    reply that has **scrolled above the visible region is unrecoverable** via get_text/fromEnd — the scrollback
    is not in the tree. Read promptly, and prefer the programmatic channel (item 6) precisely because it has no
    such ceiling.

### 5.3 Defer dedicated WT tools

Do **not** build `desktop_list_terminal_tabs` / `desktop_activate_terminal_tab` / `desktop_read_terminal_tab`
now. Revisit only if the recipe proves too error-prone in practice — and if revisited, any such tool must
disambiguate by **reading buffers**, not by matching titles.

### 5.4 Code — a minimal `fromEnd` option on `desktop_get_text` (change #2)

The core use case is reading a peer's **latest** reply, but `desktop_get_text` truncation keeps the **head**
(§3.5), so a small `maxLength` drops exactly the recent lines the caller wants. Add a minimal, general option
to read from the end of the `TextPattern` text:

**Contracts / constraints:**

- **Gate on the empirical finding.** §3.5 already established (live) that truncation keeps the head — so the
  option is warranted. The implementation plan re-confirms this against the shipped build before adding the
  option (cheap, zero-disruption: two reads of one active pane at different `maxLength`).
- **Shape.** Add `fromEnd: bool = false` to `DesktopGetText` (`ContentTools.cs`) and the underlying
  `PerceptionManager` text read. When `true`, return the **last** `maxLength` chars of the pane text instead of
  the first. Default `false` preserves today's behavior exactly (no breaking change).
- **Don't overload `truncated` (agy round 1: Protocol Pedant).** The existing `truncated` bool cannot tell the
  caller *which* end was dropped. Add an explicit indicator so the contract is self-describing — e.g. a
  `truncatedFrom: "head" | "tail" | null` field (`null` when not truncated; `"tail"` for the default
  head-keeping read, `"head"` for a `fromEnd` read). The caller must not have to re-derive the dropped end from
  its own input argument.
- **`truncatedFrom` is about `maxLength`, NOT about scrollback completeness (agy round 2: Blindspot Auditor).**
  `truncatedFrom` describes truncation of the returned text relative to **the pane's TextPattern content
  (≈ the viewport)** — it does **not** vouch that the returned text is the program's complete recent output. A
  reply that scrolled above the viewport (§5.2.11 / §6 ceiling) is already gone before `maxLength` is applied,
  so `truncatedFrom:"head"` on a `fromEnd` read must **not** be read as "I captured the true tail of the
  program." Document the two as orthogonal so a caller never conflates "not truncated by maxLength" with
  "complete output captured."
- **Tail semantics caveat (agy panel: Protocol Pedant).** A `fromEnd` read will begin **mid-line**, and offsets
  are **UIA character units** (which differ from raw UTF-16 for non-BMP/emoji, per the existing
  `desktop_select_text_range` note), so the tail may start mid-grapheme. This is acceptable for reading recent
  output; document it so callers don't treat a mid-line start as corruption.
- **General, not WT-specific.** This is a plain TextPattern-reading affordance usable for any long text
  element (logs, consoles), not coupled to Windows Terminal. It stays `ReadOnly` / lease-exempt like the rest
  of `desktop_get_text`.
- **Rejected alternative:** synthetic `Ctrl+End` to snap the viewport before reading — it would re-introduce
  the `shells` lease requirement that §3.1 eliminated, and is app-specific. Do not use it.

## 6. Known limitation — `--read-only-mode`

`desktop_select` is `Destructive`, so it is blocked in `--read-only-mode`. Because activating a background tab
**visibly changes** which tab is shown (even though the recipe restores it afterward), reading a background tab
is **unavailable in `--read-only-mode` by design.** Read-only mode must guarantee zero visible side effects on
the user's desktop state; a transient-but-visible tab switch is a state mutation. (agy Q3: "Leave unavailable...
`Select()` visibly changes the active tab... warrants no exception.") This limitation is **documented**, not
worked around. The skill recipe must state it.

## 7. Acceptance criteria

1. **Hint present, non-blocking.** With a WT window open, `desktop_list_windows` returns a `Hint` on the
   `WindowsTerminal` entry and **no** `Hint` on other windows. The call remains pure-Win32 (no UIA walk, no tab
   count) and does not block on an unresponsive window. Existing JSON for non-WT windows is byte-unchanged.
2. **Recipe reads a background tab.** Following the rewritten skill recipe, starting from a WT window whose
   **non-active** tab runs a distinctive CLI: enumerate tabs, `desktop_select` the target, re-snapshot,
   `desktop_get_text` returns that tab's buffer — **with the input lease locked** (proves lease-exemption).
3. **Restore works.** After reading, re-selecting the original `TabItem` leaves the user's originally-active
   tab foreground (confirmed via `desktop_list_windows`).
4. **Disambiguation is honored.** Given two identically-titled tabs, the recipe distinguishes them by reading
   each buffer (not by title), and the skill text explicitly forbids stopping at the first title match.
5. **Read-only limitation documented.** The skill states that background-tab reading is unavailable in
   `--read-only-mode` and why.
6. **Latest-output reads work.** `desktop_get_text ... fromEnd:true` returns the tail of a pane's text (the
   most recent lines); `fromEnd:false`/omitted is byte-identical to today. A peer's most-recent reply is
   retrievable without reading the entire buffer.
7. **Restore is guaranteed-attempted on failure, and honest when it can't.** If the recipe errors
   mid-enumeration, restore is still attempted by **ordinal index** (not a stale ref / ambiguous name). Verify
   two cases: (a) inject a mid-flow failure (e.g. `desktop_select` a deliberately-invalid index, or fail the
   read on a later tab) and confirm the original tab is restored; (b) close the original tab mid-recipe and
   confirm the recipe **reports** that restore could not complete (names the now-active tab) rather than
   silently leaving the user elsewhere.
8. **Hint is short and matches the recognition set.** The `desktop_list_windows` hint is a single short
   pointer sentence (not the full recipe), and fires for both `WindowsTerminal` and `WindowsTerminalPreview`.
9. **Scrolled-off replies are a documented ceiling.** The skill states that a reply which scrolled above the
   visible viewport is not retrievable via `desktop_get_text`/`fromEnd` (scrollback isn't in the tree), and
   that the programmatic channel is preferred for that reason.
10. **Restore identity is ordinal.** The recipe records and restores the original tab by its tab-strip
    ordinal index (anchored on the `TabListView` container), never by a pre-switch ref or by title.

## 8. Out of scope

- Mapping a tab to its exact child PID (WT → `OpenConsole`/ConPTY → shell). Best-effort only; the reliable
  identifier is the **buffer content**, per §3.2.
- Multiplexers other than Windows Terminal (WezTerm, tmux-in-a-terminal).
- Any silent/foreground-preserving background read (fundamentally impossible — the buffer isn't realized until
  the tab is active).

## 9. References (verified against current code, 2026-07-10)

- `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` — `WindowInfo` record (lines 15-19), `ListWindowsAsync`
  (lines 65-89, note the pure-Win32/no-UIA guarantee at 73-75), `SafeProcessName` (line 318).
- `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` — `DesktopListWindows` tool + description (lines 23-27).
- `src/FlaUI.Mcp.Server/Tools/InteractionTools.cs` — `DesktopSelect` (lines 104-110), `Destructive`.
- `src/FlaUI.Mcp.Server/Tools/ContentTools.cs` — `DesktopGetText` (lines 64-83), `ReadOnly` / lease-exempt.
- `.claude/skills/driving-flaui-mcp/SKILL.md` — "Terminals & reading another agent's TUI" section (to rewrite).
- `TOFIIX.md` §1 — the original problem writeup (its "Suggested tool shape" is superseded by this spec).
