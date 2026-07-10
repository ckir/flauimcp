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

> `"Multiplexed terminal — only the active tab is shown. Snapshot to see tabs; see skill driving-flaui-mcp."`

**Keep it short (change #1 from consumer review):** `desktop_list_windows` is called frequently, and a
verbose recipe string repeated on every listing is token-noise in the caller's context. The full recipe lives
in the skill (§5.2), not in the tool output. The hint is one short sentence that points there. (Both consumers
— Claude and agy — flagged this; agy: "verbose hints in a top-level enumeration tool destroy context windows
with redundant token noise.")

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
- **Recognition set.** Start with exactly `WindowsTerminal`. Generalizing to other multiplexers (WezTerm,
  etc.) is deferred until a second real case appears (scope-creep guard).
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
7. **Prune candidates via `fullProperties` before activating (change #4).** Before defaulting to
   activate-and-read every tab to disambiguate, run `desktop_snapshot fullProperties:true` and check whether
   the `TabItem`s carry a distinguishing `AutomationId` / `HelpText` (tooltip). If a stabler discriminator
   exists, use it to narrow candidates and avoid O(N) disruptive tab switches. Only fall back to
   activate-and-read for tabs that remain ambiguous. *(The implementation plan must verify empirically whether
   WT's `TabItem`s actually expose a usable `AutomationId`/`HelpText`; if they don't, the recipe says so and
   activate-and-read stays the only path.)*
8. **Mandatory restore-on-failure — treat restore as `finally` (change #5, from agy).** Record the
   originally-active `TabItem` up front, and **always** re-select it when done — **including on any error or
   timeout mid-enumeration** (e.g. a parse failure or an `ActionBlockedPending` on tab 2). The recipe must
   never leave the user stranded on a tab they weren't on. Restore is non-negotiable cleanup, not a
   happy-path step.
9. **Read the LATEST output, not the head (change #2).** `desktop_get_text` truncation keeps the head and the
   pane returns ~the viewport (§3.5), so to capture a peer's most-recent reply, read from the **end** — use
   `desktop_get_text ... fromEnd:true` (§5.4) or pass a viewport-sized `maxLength` and use the tail of the
   result.

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
  the first; `truncated:true` then means content was dropped from the **head**. Default `false` preserves
  today's behavior exactly (no breaking change).
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
7. **Restore is guaranteed on failure.** If the recipe errors mid-enumeration (e.g. an `ActionBlockedPending`
   or a read failure on a later tab), the user's originally-active tab is still restored. (Verify by injecting
   a mid-flow failure and confirming the active tab is unchanged from the start.)
8. **Hint is short.** The `desktop_list_windows` hint on a `WindowsTerminal` entry is a single short pointer
   sentence, not the full recipe.

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
