# SP4 — redaction completeness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close two content leaks (unmaskable screenshot pixels; the focused-window title carrying an element's Name), fix one mislabelled wire field, remove one string-literal coupling, and disposition two non-code items — all before the v1.0.0 contract freezes.

**Architecture:** Three independent source changes plus one test-guard change, sequenced so a guard is only ever tightened after the thing it guards is already correct. A1 extracts the mask-escalation DECISION into a pure function (`MaskEscalation`) with an injectable, lazy ancestor source, so the UIA-touching code shrinks to "read a rect, hand it to the decision". A5 replaces a UIA `Name` read with `GetWindowText` plus a guarded window-root fallback. A2 is a rename. A6 refactors seven string literals onto one constant and adds a Roslyn sweep rule that runs INDEPENDENTLY of the existing allowlist.

**Tech Stack:** C# / .NET 10, FlaUI.UIA3 5.0.0, xUnit, `Microsoft.CodeAnalysis.CSharp` (already referenced by the test project). Solution file is `FlaUI.Mcp.slnx` — there is no `.sln`.

**Source spec:** `docs/superpowers/specs/2026-08-18-sp4-redaction-completeness-design.md` (panel-hardened, 4 rounds, CLOSED).

**Branch point:** `master` at `ef17ed9` (SP3 merged). Every file, line and symbol cited below was read against that commit.

---

## Decisions this plan makes that the spec left open

The spec deferred two items to the plan (§11.5) and its case table did not reach three sites that the
implementation does. All five were consulted with the agy peer and ruled on by the operator on 2026-08-19.

| # | Question | Decision | Where it lands |
|---|---|---|---|
| D1 | Ancestor-walk mechanism (spec §11.5a) | Raw-view `TreeWalker` (`el.Automation.TreeWalkerFactory.GetRawViewWalker()`), matching the existing idiom at `SnapshotEngine.cs:265-278` | Task 4 |
| D2 | Does any in-repo consumer read `stats.redacted`? (spec §11.5b) | **Re-grepped against live code: four sites, all listed in Task 2.** No consumer outside them. The unrelated `Redacted` members on `VerifyRead`, `GridCellInfo` and `TextReadResult` are a DIFFERENT field and must NOT be renamed | Task 2 |
| D3 | A search root whose descendant enumeration throws | Strict on `roots[0]` (the window) — REFUSE; lenient on popup roots — skip. Mirrors `PerceptionManager.cs:583-596` and `:745-760`, which already draw this exact line | Task 4 |
| D4 | Does the refusal reach full-desktop capture? | **Yes.** Without it A1 is inert on the one capture mode DEF-2 just fixed | Task 5b |
| D5 | Does the refusal reach the `desktop_find_text` OCR path? | **Yes.** That path captures the same pixels and OCRs them, so suppressing the refusal would read back in plaintext exactly what the screenshot refused to show | Task 5b |

---

## File structure

**Created:**

| Path | Responsibility |
|---|---|
| `src/FlaUI.Mcp.Core/Perception/MaskEscalation.cs` | The pure escalation decision + the wire DTO for one escalating element. No UIA. |
| `src/FlaUI.Mcp.Core/Perception/AncestorRectSource.cs` | The production adapter: walks real ancestors on the raw view, memoizes per capture. The only UIA in the A1 path. |
| `test/FlaUI.Mcp.Tests/Perception/MaskEscalationTests.cs` | Headless pins for every row of the spec's A1 case table. |

⚠ **No new headless test file constructs a `WindowManager`.** Measured: every file that does carries
`[Trait("Category", "Desktop")]`, and the headless filter is what CI runs. A5's facts are therefore Desktop
facts, in Task 10.

**Modified:**

| Path | Change |
|---|---|
| `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` | A5: `ResolveFocusedWindowAsync` reads the window title, not the focused element's Name. |
| `src/FlaUI.Mcp.Core/Perception/SnapshotStats.cs` | A2: `Redacted` → `OsPasswordCount`. |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` | A2 tally; A1 mask collection + refusal + full-desktop propagation; A6 literal at `:210`. |
| `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs` | A6 literal at `:188`. |
| `src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs` | A6 literal at `:27`. |
| `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` | A1: new `RedactionUnmaskable` member. |
| `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs` | A1 metadata (`maskEscalations`, `escalated`) + description. |
| `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs` | A2 wire field + description; A6 attribute literal at `:19`. |
| `src/FlaUI.Mcp.Server/Tools/FindTools.cs` | A6 attribute literal at `:22`. |
| `src/FlaUI.Mcp.Server/Tools/WatchTools.cs` | A6 attribute literal at `:30`. |
| `src/FlaUI.Mcp.Server/Tools/ContentTools.cs` | A6 attribute literal at `:65`. |
| `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs` | D5: stop swallowing `RedactionUnmaskable` at `:104`. |
| `test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs` | A6 RULE 5 + its three pins; A5 allowlist rewrite. |
| `test/FlaUI.Mcp.Tests/Perception/RedactionStatsTests.cs` | A2 rename in the existing discriminating assertion. |
| `test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs` | A1 + A5 Desktop facts. |
| `docs/coverage-debt.md`, `docs/agent-contract.md`, `CHANGELOG.md`, `ROADMAP.md`, `.clavity/local-anomalies.md` | Dispositions. |

---

## Standing rules for every task

1. **Step 0 STATE-VERIFICATION is mandatory.** Open the cited file and confirm the pasted "current" code
   matches byte-for-byte before editing. If it differs, STOP and report `STATE_MISMATCH: <what>` — do not
   adapt.
   ⚠ **A STATE-VERIFY block quotes the file AS THE PRECEDING TASKS LEAVE IT.** For every block but one
   that is identical to the file on disk today, because no earlier task touched those lines. The single
   exception is Task 5b Step 1, which quotes the `AllMaskRectsAsync` member AFTER Task 4 Step 4a's rename,
   and says so at the block. A block must never quote code that NO task produces.
   ⚠ **Never quote code this plan adds as though it were already there.**
   ⚠ **EVERY line number in this plan is stated as of the BRANCH POINT, not as of the moment you reach it.**
   Task 4 alone moves everything below `PerceptionManager.cs:848` down by **198 lines** (Step 5 turns 10
   lines into 203; Step 4b turns 1 into 6). Any later task naming a line in that file is therefore naming a
   number that no longer exists. **Anchor on the member declaration or the quoted content, always** — the
   numbers are orientation, and a task that gives only a number is telling you where the code USED to be.
   This bit twice: once at +5 and once at +198, the second time pointing into the middle of the block the
   plan had just written. That sounds too
   obvious to write down, and it is written down because it was VIOLATED: five rounds of review edits
   anchored new code onto a line that appeared only inside Task 4's verify block, so the "current code" it
   asked the executor to confirm silently grew ~50 lines that exist nowhere. An executor would have
   reported `STATE_MISMATCH` at the first task that touches the mask walk and the plan would have been
   unexecutable. If you are EDITING this plan, check which block your anchor line lives in before you
   substitute.
2. **SHAPE-DIVERGENCE STOP.** If making the code compile would change the shape, type, or encoding of any
   value shown here — even trivially — STOP and report `[original] -> [yours] because <reason>`. "It
   compiles" is not a justification. Wire field names and types are contracts.
3. **No pre-existing test may be weakened** (spec §10.4). A rename or a construction fix forced by a
   signature change is allowed. Changing an assertion, an expected string, or an expected count is not.
4. **The build must stay at 0 warnings.** `dotnet build FlaUI.Mcp.slnx -c Release` must report
   `0 Warning(s)` and `0 Error(s)`.
5. **Headless gate after every task** — where "task" means a numbered `## Task N`. Task 5 is ONE task with
   two interior halves (5a, 5b); its gate runs at the end of 5b, and the tree is expected not to compile in
   between. No other task has an interior.
   `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
   Expected: `Passed!  - Failed: 0, Passed: <N>, Skipped: 0`. `N` is 870 at the branch point and grows as
   this plan adds facts; a DROP in `N` means a test was deleted, which rule 3 forbids.
6. **Do not run the Desktop suite between tasks.** It needs a physical console and an input lease and takes
   ~12 minutes; it runs once, at Task 10, and once more at the final gate.

---

## Task 0: branch off `master`

⚠ **The plan had NO branch step through fifteen panel rounds, and gate G6 says "merge to `master` with
`--no-ff`, matching SP0-SP3".** Executed as written from `master`, all eleven tasks would land directly on
`master` and G6 would have nothing to merge. Every seat reviewed the tasks and the gates; none reviewed the
precondition BEFORE Task 1. Recorded rather than quietly inserted, because "the reviewers all looked past
the same blind spot" is the useful part.

- [ ] **Step 1: Confirm the starting point**

```bash
git status --short && git branch --show-current && git log --oneline -1
```

Expected: clean tree, `master`, and a HEAD that includes this plan. If the tree is dirty, STOP — every
task's `git add` names exact paths and assumes nothing else is staged.

- [ ] **Step 2: Create the branch**

```bash
git checkout -b sp4-redaction-completeness
```

⚠ Two standing decisions apply to this branch and are NOT oversights:
- **Nothing is pushed this release.** `master` is far ahead of `origin/master` by the operator's explicit
  choice; do not push this branch or add a remote.
- **This project KEEPS merged branches.** `sp2-additive-perception` and `sp3-per-field-redaction` both
  still exist after their merges. Do not delete this one at G6, whatever a finishing script says.

---

## Task 1: A5 — `window.title` becomes the real window title

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs:611-627`

The defect: `ResolveFocusedWindowAsync` reads `focused.Properties.Name` — the focused ELEMENT's Name,
which is element content — and `PerceptionManager.GetFocusedElementAsync` (`:936`) puts it on the wire as
`window.title` via `SnapshotTools.cs:116`, with no classifier touchpoint.

- [ ] **Step 1: STATE-VERIFY**

Open `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` and confirm lines 611-627 are exactly:

```csharp
    public Task<(WindowHandle Handle, string Title, int Pid)?> ResolveFocusedWindowAsync() =>
        _dispatcher.RunQueryAsync<(WindowHandle, string, int)?>(() =>
        {
            var focused = _automation.FocusedElement();
            if (focused is null) return null;
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = focused.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            // True top-level window via Win32 GA_ROOT; fall back to the foreground window
            // (a focused element is always in it) when the element exposes no own HWND.
            hwnd = hwnd != IntPtr.Zero ? GetAncestor(hwnd, GA_ROOT) : GetForegroundWindow();
            int pid = -1; string title = "";
            try { pid = focused.Properties.ProcessId.ValueOrDefault; } catch { }
            try { title = focused.Properties.Name.ValueOrDefault ?? ""; } catch { }
            if (hwnd == IntPtr.Zero) return null;
            var handle = Register(_automation.FromHandle(hwnd).AsWindow(), pid);
            return (handle, title, pid);
        });
```

Also confirm `public string? WindowTitle(IntPtr hwnd)` exists at `:166` and returns `null` for an empty
caption. If either differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Replace the method body**

⚠ **The window-root `Name` read MUST stay physically inside `ResolveFocusedWindowAsync`.** Do not extract
it into a helper method. The Roslyn sweep keys its allowlist on `Type.Member`, and
`WindowManager.ResolveFocusedWindowAsync` is the key that exempts this read
(`RedactionSurfaceInventoryTests.cs:85`). Moving the read to a new member creates an unlisted member and
turns `SourceTree_EnforcesRedactionSurfaceInvariants` red.

```csharp
    public Task<(WindowHandle Handle, string Title, int Pid)?> ResolveFocusedWindowAsync() =>
        _dispatcher.RunQueryAsync<(WindowHandle, string, int)?>(() =>
        {
            var focused = _automation.FocusedElement();
            if (focused is null) return null;
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = focused.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            // True top-level window via Win32 GA_ROOT; fall back to the foreground window
            // (a focused element is always in it) when the element exposes no own HWND.
            hwnd = hwnd != IntPtr.Zero ? GetAncestor(hwnd, GA_ROOT) : GetForegroundWindow();
            int pid = -1;
            try { pid = focused.Properties.ProcessId.ValueOrDefault; } catch { }
            if (hwnd == IntPtr.Zero) return null;
            var root = _automation.FromHandle(hwnd).AsWindow();

            // A5: `title` is the OWNING WINDOW's title. It used to be `focused.Properties.Name` — the
            // focused ELEMENT's Name, i.e. element content, published as `window.title` with no classifier
            // touchpoint. Win32 first, for the reason this file already documents at :340: a UIA property
            // read on the query STA can block with NO timeout on an unresponsive window; GetWindowText
            // cannot.
            string title = WindowTitle(hwnd) ?? "";

            // Some frameworks draw their own title bar, leaving the Win32 caption empty while the visible
            // title exists only in the UIA tree; returning empty there would silently blind the agent to
            // those windows' titles. The WINDOW ROOT's Name IS the window's title — this is NOT a return to
            // the defect above, which read the FOCUSED ELEMENT's Name. It is ordered second because it is
            // the slower, blockable source, not because it is the wrong one.
            //
            // ⚠ EXCEPTION-GUARDED DELIBERATELY: this is a cross-process COM call and throws on an
            // unresponsive or tearing-down window. Unguarded, a harmless captionless window would crash
            // desktop_get_focused_element outright — reintroducing the exact unhandled-UIA failure mode that
            // moving to GetWindowText exists to escape. A throw degrades to an empty title, as an empty read
            // does.
            if (title.Length == 0) { try { title = root.Properties.Name.ValueOrDefault ?? ""; } catch { } }

            var handle = Register(root, pid);
            return (handle, title, pid);
        });
```

- [ ] **Step 3: NO test in this task — and why not**

A5's pins are Desktop facts and they land in Task 10. **Do not add a headless test here.**

⚠ **This was the plan's own first draft and it was wrong, so the reasoning is recorded rather than the
conclusion alone.** The draft added a headless `WindowTitleTests` asserting `mgr.WindowTitle(IntPtr.Zero)`
returns null. MEASURED against the repo: **every** file containing `new WindowManager` carries
`[Trait("Category", "Desktop")]`, and the only non-Desktop file touching UIA infrastructure
(`test/FlaUI.Mcp.Tests/Threading/AutomationDispatcherTests.cs`) constructs an `AutomationDispatcher` and
nothing more. A headless test constructing a `WindowManager` would be the first in this repo — and the
headless filter is what runs on the **GitHub CI runner**, so if that constructor needs a desktop the cost
is a red CI on `master`, discovered after the fact.

The assertion is worth keeping; it just belongs in the Desktop suite. It moves to Task 10 Step 2.

- [ ] **Step 4: Run the headless gate**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged (this task adds no test).

Note the sweep test (`SourceTree_EnforcesRedactionSurfaceInvariants`) must still be GREEN here: the
allowlist entry for this member is still in place and still suppresses the read. Its TEXT is now false and
gets rewritten in Task 9 — that is the correct order, and rewriting it earlier changes nothing mechanically.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs
git commit -m "fix(sp4): A5 - window.title is the WINDOW's title, not the focused element's Name

ResolveFocusedWindowAsync read focused.Properties.Name and published it as
window.title, so element content reached the wire with no classifier
touchpoint. Now GetWindowText (which cannot block on an unresponsive window,
unlike a UIA read on the query STA), falling back to the window ROOT's Name -
the correct source for a title - when the caption is empty. The fallback is
exception-guarded: unguarded it would crash desktop_get_focused_element on a
captionless tearing-down window, reintroducing the failure mode this change
exists to escape.

BREAKING (silent): window.title's VALUE changes meaning. Announced in
CHANGELOG, agent-contract.md and the tool description in a later commit.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: A2 — `redacted` becomes `osPasswordCount`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotStats.cs:6-16`
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:839-846`
- Modify: `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:99,108`
- Modify: `test/FlaUI.Mcp.Tests/Perception/RedactionStatsTests.cs:32`

`desktop_snapshot_stats` emits `redacted` (OS-password nodes only) beside `redactedCount` (all redacted
nodes). Two same-rooted names with different meanings mislead by default. Breaking, deliberately, and only
free before v1.0.0. No alias: an alias preserves exactly the ambiguity being removed.

**D2 — the complete consumer set, re-grepped against live code at `ef17ed9`.** These four sites and no
others:

| Site | What it is |
|---|---|
| `src/FlaUI.Mcp.Core/Perception/SnapshotStats.cs:14` | the record member (plus the doc comment at `:6-7`) |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:843` | the tally that produces it |
| `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:108` | the wire projection (plus the description at `:99`) |
| `test/FlaUI.Mcp.Tests/Perception/RedactionStatsTests.cs:32` | the mutually-discriminating assertion |

⚠ **Do NOT rename these — they are a DIFFERENT field with the same name.** `VerifyRead.Redacted`
(`src/FlaUI.Mcp.Core/Interaction/VerifyReader.cs:12`), `GridCellInfo.Redacted` and
`TextReadResult.Redacted` (`PerceptionManager.cs:945,948`), and their consumers in `PasteFlow.cs:77,106,109`,
`InputTools.cs:215,240` and `ContentTools.cs:36,39,81,84`. Those carry "this content was withheld" on a
single read; they are not counters and their wire names are not changing.

- [ ] **Step 1: STATE-VERIFY**

Confirm `SnapshotStats.cs` lines 6-16 are exactly:

```csharp
/// ⚠ <see cref="Redacted"/> keeps its original, OS-password-only meaning for back-compat with an
/// existing consumer already keyed off it. <see cref="RedactedCount"/> is the true total across BOTH
/// redaction sources (OS + rule).</summary>
public sealed record SnapshotStats(
    string SnapshotId,
    int Total,
    int Interactive,
    int Offscreen,
    int Redacted,
    int RedactedCount,
    IReadOnlyDictionary<string, int> ByControlType);
```

and that `PerceptionManager.cs:842-845` is exactly:

```csharp
        return new SnapshotStats(id, nodes.Count, nodes.Count(SnapshotEngine.IsInteractiveNode),
            nodes.Count(n => n.IsOffscreen), nodes.Count(n => n.Sensitivity.Source == RedactionSource.Os),
            nodes.Count(n => n.Sensitivity.Redact),
            nodes.GroupBy(n => n.ControlType.ToString()).ToDictionary(g => g.Key, g => g.Count()));
```

If either differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Rename the record member**

In `src/FlaUI.Mcp.Core/Perception/SnapshotStats.cs`, replace lines 6-8 (the ⚠ paragraph) and line 14:

```csharp
/// ⚠ <see cref="OsPasswordCount"/> and <see cref="RedactedCount"/> are DIFFERENT numbers and always were.
/// OsPasswordCount counts nodes the OS flagged IsPassword; RedactedCount counts every withheld node, from
/// either source. They were once called `Redacted` and `RedactedCount` — two same-rooted names for two
/// different quantities, which is a footgun by construction, so the ambiguous one was renamed before the
/// v1.0.0 contract froze it.</summary>
public sealed record SnapshotStats(
    string SnapshotId,
    int Total,
    int Interactive,
    int Offscreen,
    int OsPasswordCount,
    int RedactedCount,
    IReadOnlyDictionary<string, int> ByControlType);
```

- [ ] **Step 3: Run the build to find every break**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: FAIL, with `CS1061`/`CS0117`-family errors naming exactly `SnapshotTools.cs` and
`RedactionStatsTests.cs`. The construction in `PerceptionManager.Tally` is POSITIONAL and will NOT error —
it needs no change, and that is why the assertion in Step 6 is the only thing standing between a rename and
a silently swapped pair of counters.

If the compiler names any file NOT in the four-site table above, STOP and report it: the D2 grep was
incomplete.

- [ ] **Step 4: Update the wire projection**

In `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs`, replace line 108:

```csharp
            return ToolResponse.Ok(new { snapshotId = s.SnapshotId, total = s.Total, interactive = s.Interactive, offscreen = s.Offscreen, osPasswordCount = s.OsPasswordCount, redactedCount = s.RedactedCount, byControlType = s.ByControlType });
```

and replace the description at line 99, which currently reads:

```csharp
    [McpServerTool(ReadOnly = true), Description("Cheap orientation: control counts (total/interactive/offscreen/redacted/redactedCount) + a per-ControlType histogram, without the tree text. redactedCount is ALL redacted nodes; redacted counts OS password nodes only (kept for back-compat) - they are different numbers. Supply exactly one of window (fresh full walk — a fuller view than a pruned desktop_snapshot) or snapshotId (a prior cached snapshot, tallied as-snapshotted).")]
```

with:

```csharp
    [McpServerTool(ReadOnly = true), Description("Cheap orientation: control counts (total/interactive/offscreen/osPasswordCount/redactedCount) + a per-ControlType histogram, without the tree text. redactedCount is ALL redacted nodes (OS password fields AND operator rules); osPasswordCount is the OS-flagged subset only - they are different numbers. Supply exactly one of window (fresh full walk — a fuller view than a pruned desktop_snapshot) or snapshotId (a prior cached snapshot, tallied as-snapshotted).")]
```

- [ ] **Step 5: Update the tally's doc-facing name only where the compiler demands**

`PerceptionManager.Tally` (`:839-846`) constructs positionally and needs NO edit. Leave it byte-identical.
Confirm you did not touch it: `git diff --stat src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` must
report nothing for this task.

- [ ] **Step 6: Update the pinning assertion — IDENTIFIER ONLY**

In `test/FlaUI.Mcp.Tests/Perception/RedactionStatsTests.cs`, replace line 32:

```csharp
        Assert.Equal(1, stats.OsPasswordCount); // OS only — a DIFFERENT number from RedactedCount below
```

⚠ The `1` and the `2` on line 33 are load-bearing and must not change. The pair is mutually
discriminating: any implementation that conflates the two counters, or swaps them in the positional
constructor, fails. That is the whole reason the rename is safe.

- [ ] **Step 7: Run the headless gate**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged from Task 1.

- [ ] **Step 8: Prove the pin is non-vacuous (temporary LOGIC MUTANT)**

Swap the two counter expressions in `PerceptionManager.Tally` — put
`nodes.Count(n => n.Sensitivity.Redact)` in the 5th position and
`nodes.Count(n => n.Sensitivity.Source == RedactionSource.Os)` in the 6th.

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~RedactionStatsTests"`
Expected: FAIL, and specifically
`The_os_counter_keeps_its_meaning_while_redactedCount_counts_both_sources` must be the failing test.

**REVERT THE MUTANT** and re-run to confirm green before committing.

- [ ] **Step 9: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotStats.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Perception/RedactionStatsTests.cs
git commit -m "feat(sp4)!: A2 - desktop_snapshot_stats.redacted becomes osPasswordCount

BREAKING: the wire field \`redacted\` on desktop_snapshot_stats is renamed to
\`osPasswordCount\`; C# member SnapshotStats.Redacted becomes OsPasswordCount.
\`redactedCount\` is unchanged in name and meaning.

Two same-rooted names for two different quantities mislead by default. No
alias is kept - an alias preserves exactly the ambiguity being removed. Unlike
A5 this break is LOUD: a consumer reading \`redacted\` gets a missing field
immediately.

The existing assertion is mutually discriminating (1 vs 2), so a positional
constructor that swaps the counters fails; only the identifier changed.
Verified by mutant: swapping the two tally expressions turns
RedactionStatsTests red.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: A1 — the pure escalation decision

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/MaskEscalation.cs`
- Create: `test/FlaUI.Mcp.Tests/Perception/MaskEscalationTests.cs`

The rule (spec §3): *a redact-worthy element must contribute a usable mask rect; if it cannot, escalate to
its parent; if escalation would reach the window root, REFUSE the capture.* This task builds the decision
with no UIA in it. Task 4 wires it up.

- [ ] **Step 1: Write the failing tests**

Create `test/FlaUI.Mcp.Tests/Perception/MaskEscalationTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP4/A1 — the mask-escalation DECISION, with no UIA in it. Every row of the spec's case table
/// is a fact here. The ancestor source is a fake that counts its calls, so laziness and the depth cap are
/// observable rather than argued.</summary>
public class MaskEscalationTests
{
    private static readonly Rectangle Usable = new(10, 20, 30, 40);
    private static readonly Rectangle Other = new(1, 2, 300, 400);

    /// The captured region every fact below resolves against. Deliberately LARGER than Other, so no fact
    /// trips the blacks-out-the-capture rejection by accident - that rule gets its own dedicated facts.
    private static readonly Rectangle Capture = new(0, 0, 4000, 4000);

    /// A source that answers from a level->rect map and records every level it was asked for.
    private static Func<int, Rectangle?> Source(List<int> asked, params (int Level, Rectangle? Rect)[] answers)
        => level =>
        {
            asked.Add(level);
            foreach (var a in answers) if (a.Level == level) return a.Rect;
            return null;
        };

    [Fact]
    public void A_usable_own_rect_is_masked_precisely_and_never_asks_for_an_ancestor()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(Usable, Source(asked), Capture);

        Assert.False(r.Refused);
        Assert.False(r.Escalated);
        Assert.Equal(Usable, r.Rect);
        Assert.Empty(asked); // laziness: no ancestor fetched when the element answers
    }

    [Fact]
    public void An_unreadable_own_rect_escalates_to_the_first_usable_ancestor()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked, (1, Other)), Capture);

        Assert.False(r.Refused);
        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);
        Assert.Equal(new[] { 1 }, asked); // level 2 never requested once level 1 answered
    }

    /// <summary>A provider unable to report bounds may return zeros rather than throw. A zero-area mask
    /// paints nothing, which is a silent leak wearing a mask's clothes — so it is treated as unusable.</summary>
    [Fact]
    public void A_zero_size_own_rect_is_unusable_and_escalates()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(new Rectangle(5, 5, 0, 0), Source(asked, (1, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);
    }

    /// <summary>The case table's "parent rect also unusable" row: keep climbing. Note the decision cannot
    /// tell WHY a level was unusable — a throwing parent FETCH, a throwing bounds read, a zero-size rect
    /// and "there is no such ancestor" all present identically as null. That is what makes the
    /// parent-fetch refusal reachable without this function knowing anything about UIA.</summary>
    [Fact]
    public void An_unusable_ancestor_climbs_further_up_the_chain()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked, (1, null), (2, new Rectangle(0, 0, 0, 0)), (3, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);
        Assert.Equal(new[] { 1, 2, 3 }, asked);
    }

    /// <summary>Escalating to the window root and masking IT would return a SUCCESSFUL all-black
    /// screenshot, which is worse than an error: an agent hallucinates its contents or loops on it. A
    /// refusal forces a strategy change.</summary>
    [Fact]
    public void No_usable_ancestor_refuses_rather_than_returning_a_rect()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked), Capture);

        Assert.True(r.Refused);
        Assert.False(r.Escalated);
    }

    /// <summary>TERMINATION, not tuning. An uncapped climb on a provider with a structural cycle never
    /// returns, and this runs on the single query STA — a hang there wedges every LATER query, not just
    /// this capture. The cap must be a hard loop bound, and exhausting it refuses.</summary>
    [Fact]
    public void The_walk_stops_at_the_depth_cap_and_refuses()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked), Capture);

        Assert.True(r.Refused);
        Assert.Equal(MaskEscalation.MaxAncestorLevels, asked.Count);
        Assert.Equal(1, asked[0]);
        Assert.Equal(MaskEscalation.MaxAncestorLevels, asked[^1]);
    }

    /// <summary>`maskEscalations` counts ELEMENTS, not levels climbed: one element climbing three levels
    /// reports 1. The type enforces it — Escalated is a BOOL, so a per-level count is not representable in
    /// the decision's own output, and the caller can only add one entry per element.</summary>
    [Fact]
    public void Escalation_is_per_element_not_per_level()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked, (3, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(3, asked.Count);            // three levels were climbed
        Assert.IsType<bool>(r.Escalated);        // and the outcome is still one boolean
    }

    /// <summary>REGRESSION PIN for the second defect the panel caught, which round 2's own fix introduced:
    /// a WPF light-dismiss popup's host is a transparent FULL-SCREEN overlay, so its BoundingRectangle is
    /// the whole monitor while the visible popup is small. Treating "it is only a popup" as "masking it is
    /// safe" would return a successful all-black screenshot for the most common popup shape there is.
    ///
    /// An escalated rect that COVERS the captured region is not a mask - it is an all-black image wearing
    /// one - and it is rejected on geometry, so the walk keeps climbing and ultimately REFUSES.</summary>
    [Fact]
    public void An_escalated_rect_that_covers_the_capture_is_rejected_and_the_walk_continues()
    {
        var asked = new List<int>();
        var wholeScreen = new Rectangle(-100, -100, 9000, 9000); // contains Capture

        // Level 1 would black out everything; level 2 is a real container.
        var r = MaskEscalation.Resolve(null, Source(asked, (1, wholeScreen), (2, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);                 // NOT the screen-covering rect
        Assert.Equal(new[] { 1, 2 }, asked);
    }

    /// <summary>The same rule with nothing above it to fall back on: refuse rather than return the
    /// all-black image. This is the window-root case expressed purely as geometry.</summary>
    [Fact]
    public void A_capture_covering_rect_with_no_alternative_refuses()
    {
        var wholeScreen = new Rectangle(-100, -100, 9000, 9000);

        var r = MaskEscalation.Resolve(null, level => level == 1 ? wholeScreen : (Rectangle?)null, Capture);

        Assert.True(r.Refused);
    }

    /// <summary>⚠ THIS FACT REPLACED ONE THAT ASSERTED THE OPPOSITE, and the reversal is the point.
    ///
    /// An earlier revision DROPPED an ancestor that did not overlap the capture, reasoning that a secret
    /// inside it could not appear in the photograph. A panel seat showed the reasoning codifies a LEAK: UIA
    /// logical parents do not always enclose their visual children. A WPF tooltip, popup or drag adorner is
    /// routinely ON SCREEN while its logical parent - a scrolled-away button - is entirely off it. If the
    /// tooltip's own bounds throw, its off-screen parent "cannot appear in the capture", the mask is
    /// dropped, and the tooltip is photographed in the clear. The test that asserted the drop was asserting
    /// the bug.
    ///
    /// A non-overlapping candidate is now UNUSABLE: keep climbing toward one that does overlap, and refuse
    /// if none does.</summary>
    [Fact]
    public void An_ancestor_outside_the_capture_is_unusable_and_the_walk_climbs_past_it()
    {
        var asked = new List<int>();
        var faraway = new Rectangle(50_000, 50_000, 100, 100); // no overlap with Capture

        var r = MaskEscalation.Resolve(null, Source(asked, (1, faraway), (2, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);              // the OVERLAPPING ancestor, not the far-away one
        Assert.Equal(new[] { 1, 2 }, asked);
    }

    /// <summary>The same rule with nothing overlapping above it: REFUSE. Dropping here is what leaked.</summary>
    [Fact]
    public void An_element_whose_every_ancestor_misses_the_capture_refuses()
    {
        var faraway = new Rectangle(50_000, 50_000, 100, 100);

        var r = MaskEscalation.Resolve(null, level => level == 1 ? faraway : (Rectangle?)null, Capture);

        Assert.True(r.Refused);
    }

    /// <summary>Nothing IntersectsWith a degenerate rectangle, so a degenerate yardstick would discard
    /// every candidate. The decision REFUSES there rather than returning an unmasked capture.
    ///
    /// ⚠ This is REACHABLE in production, and a review round was right to ask. For a window- or
    /// element-scoped capture the caller falls back to the UNCLIPPED capture rect when the renderable
    /// intersection is degenerate — and that rect is itself degenerate for a zero-size window, which
    /// arrives here. (The full-desktop sweep is the path that returns early instead, because a window with
    /// no renderable overlap contributes no pixels to a virtual-screen capture.) Were this branch ever
    /// unreachable it would be a false-GREEN, so if a future change makes the caller always pre-filter,
    /// delete this fact rather than leaving it asserting a guard nothing can reach.</summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]        // Rectangle.Empty
    [InlineData(100, 50, 0, 30)]    // ⚠ NOT IsEmpty: Rectangle.Intersect compares with `>=`, so two rects
    [InlineData(100, 50, 30, 0)]    //   touching along an edge yield a degenerate rect at non-zero coords
    public void A_degenerate_yardstick_refuses_rather_than_dropping_every_mask(int x, int y, int w, int h)
    {
        var r = MaskEscalation.Resolve(null, Source(new List<int>(), (1, Other)), new Rectangle(x, y, w, h));

        Assert.True(r.Refused);
    }

    /// <summary>The rule applies to ESCALATED rects only. An element whose OWN rect covers the capture is
    /// genuinely that large, and masking it is correct rather than a degradation - refusing there would
    /// break a legitimate full-window redaction.</summary>
    [Fact]
    public void An_elements_OWN_capture_covering_rect_is_masked_not_refused()
    {
        var wholeScreen = new Rectangle(-100, -100, 9000, 9000);

        var r = MaskEscalation.Resolve(wholeScreen, Source(new List<int>()), Capture);

        Assert.False(r.Refused);
        Assert.False(r.Escalated);
        Assert.Equal(wholeScreen, r.Rect);
    }

    /// <summary>REGRESSION PIN for the defect an adversarial panel caught in this plan's first draft: the
    /// ancestor walk had NO root bound, and since GetParent succeeds at the window root and the root HAS
    /// valid bounds, escalation would have masked the whole window and returned a SUCCESSFUL all-black
    /// screenshot instead of refusing. Every headless fact still passed, because the fake source's chain
    /// ends in null and so described a boundary the real walk did not have.
    ///
    /// This fact pins the CONTRACT that made the fake lie: a source that reports the root as usable turns
    /// what should be a refusal into a mask, so the SOURCE owes the decision a null at the root.
    ///
    /// ⚠ It does NOT pin AncestorRectSource's actual bound. That needs a live element which fails to report
    /// bounds while its ancestors do not, and no fixture can stage one — the AB-1 limitation. The bound is
    /// therefore ledgered as accepted boundary AB-7, with this fact named as its compensation and its limit
    /// stated: if the root check were deleted from AncestorRectSource, no test goes red.</summary>
    [Fact]
    public void A_source_that_reports_the_root_as_usable_would_mask_instead_of_refusing()
    {
        var asked = new List<int>();

        // A source that keeps answering - i.e. one that failed to stop at the root.
        var unbounded = MaskEscalation.Resolve(null, level => { asked.Add(level); return Other; }, Capture);

        Assert.False(unbounded.Refused);   // this is the WRONG outcome, and it is what an unbounded walk gives
        Assert.True(unbounded.Escalated);

        // The bounded contract: the source must report the root as "no rect at this level".
        var bounded = MaskEscalation.Resolve(null, Source(new List<int>()), Capture);
        Assert.True(bounded.Refused);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~MaskEscalationTests"`
Expected: FAIL to BUILD — `CS0246: The type or namespace name 'MaskEscalation' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/FlaUI.Mcp.Core/Perception/MaskEscalation.cs`:

```csharp
using System;
using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One redact-worthy element whose OWN rect was unusable, so its mask was taken from an ancestor.
///
/// ⚠ The element's Name is NEVER carried here — that would leak the identity of the very thing the mask
/// exists to hide. AutomationId and ControlType are both already published for redacted elements by
/// desktop_find (BC-1 targetability), so emitting them withholds nothing that redaction withholds.
///
/// ControlType is present because AutomationId ALONE is a blindspot: many elements legitimately have none
/// — this repo's own fixture depends on that — so an id-only diagnostic degrades to ["", "", ""] on
/// exactly the legacy and flat-tree UIs where A1's refusal path fires hardest. An element with no
/// AutomationId contributes an empty string and still reports its ControlType.</summary>
public sealed record MaskEscalationEntry(string AutomationId, string ControlType);

/// <summary>The outcome of resolving ONE redact-worthy element to a mask rect. <see cref="Escalated"/> is a
/// BOOL, deliberately: the wire's `maskEscalations` counts ELEMENTS, not levels climbed, and a type that
/// cannot represent a level count cannot drift into reporting one.</summary>
public readonly record struct MaskResolution(Rectangle Rect, bool Escalated, bool Refused)
{
    public static readonly MaskResolution Refusal = new(default, false, true);
}

/// <summary>SP4/A1. THE escalation decision, extracted so it is testable without UIA: the UIA-touching code
/// shrinks to "read a rect, hand it to the decision".
///
/// One rule, not a taxonomy. The tempting design branches on WHY a rect is missing (threw / element
/// vanished / zero-size); that was rejected deliberately, because distinguishing them multiplies
/// exception-type handling on the one path whose whole job is to withhold, and one auditable rule is worth
/// more here than a precise one.
///
/// ⚠ The accepted cost, stated rather than implied: an element that vanished mid-capture is very likely no
/// longer painted, so escalating masks a parent region holding no secret. On an animating or transitioning
/// UI that produces occasional black boxes over benign content. This is a real usability cost on a common
/// transition, accepted because a teardown-race element may still be painted and withholding is the point.
///
/// WARNING, and this is the honest limit of the whole mechanism: ESCALATION IS BEST-EFFORT, NOT A
/// CONTAINMENT GUARANTEE. It assumes an ancestor's BoundingRectangle encloses its descendants' painted
/// pixels. That is USUALLY true and is not universally true: a child can paint outside its parent's bounds
/// through a negative margin, an absolute or canvas position, or a render transform. When it does, the
/// ancestor mask covers less than the element did and the uncovered part of the secret is photographed.
///
/// This cannot be checked from here. Escalation happens precisely BECAUSE the element's own rect is
/// unreadable, so its true extent is the one quantity unavailable at the moment the decision is made. It is
/// stated rather than fixed, and ledgered as AB-8, because a guarantee that is 99% true and documented as
/// absolute is worse than one that is 99% true and says so.</summary>
public static class MaskEscalation
{
    /// <summary>TERMINATION BOUND, not a tuning knob. An uncapped ancestor climb on a provider with a
    /// structural cycle never returns, and this runs on the single query STA — a hang there wedges every
    /// SUBSEQUENT query, not merely this capture. Exhausting the cap is treated exactly as reaching the
    /// window root: REFUSE. Do not remove this for being "unreachable in practice".
    ///
    /// 32 is BELOW the snapshot walk's default maxDepth of 40, which looks like an over-refusal risk and is
    /// not one — worth writing down, because the next reader will re-derive the worry. Cap-exhaustion and
    /// root-reaching produce the IDENTICAL outcome, so the cap can only refuse a capture that would
    /// otherwise have succeeded in one shape: a usable ancestor above level 32 with all 32 below it
    /// unusable. Any tree in that state is already pathological.</summary>
    public const int MaxAncestorLevels = 32;

    /// <summary>A rect is usable only with positive area. A provider unable to report bounds may return
    /// zeros rather than throw, and a zero-area rect paints nothing — a silent leak, not a mask.</summary>
    public static bool HasArea(Rectangle? r) => r is not null && r.Value.Width > 0 && r.Value.Height > 0;

    /// <summary>Resolve one redact-worthy element to the rect that should be painted black.
    ///
    /// <paramref name="ancestorRect"/> is a LAZY accessor: level 1 is the element's parent, level 2 its
    /// grandparent, and level n+1 is requested ONLY if level n was unusable. No pre-fetch — supplying a
    /// materialised ordered list would force every ancestor of every redact-worthy element to be fetched
    /// before the decision runs, a cross-process COM cost on a path already dominated by property traffic.
    ///
    /// ⚠ FAILURE IS INDISTINGUISHABLE FROM ABSENCE. Any failure to obtain an ancestor rect — the parent
    /// FETCH itself throwing, the bounds read throwing, or simply running past the window root — presents
    /// here as null. That equivalence is what makes the case table's parent-fetch refusal reachable without
    /// this function knowing anything about UIA.
    ///
    /// ⚠⚠ <paramref name="captureYardstick"/> IS THE SAFETY NET, AND IT IS WHY THIS FUNCTION TAKES GEOMETRY
    /// IT OTHERWISE WOULD NOT NEED. An ESCALATED rect that covers the whole captured region is not a mask — it
    /// is an all-black screenshot wearing one, and returning it successfully is the single outcome A1 exists
    /// to refuse instead. Checking that here, geometrically, catches every route to it at once and needs no
    /// knowledge of which element produced the rect:
    ///   · the window root (its rect IS the capture);
    ///   · a WPF light-dismiss popup, whose host is a transparent FULL-SCREEN overlay — its BoundingRectangle
    ///     is the whole monitor even though the visible popup is small, so "it is only a popup, masking it is
    ///     safe" is false for exactly the popups that are most common;
    ///   · the DESKTOP, if a walk ever escapes its root through an unreadable ancestor identity.
    /// ⚠ Read this next sentence carefully, because an earlier revision of it was WRONG and a review seat
    /// caught the lie: the window-root-versus-popup-root fork in AncestorRectSource **still exists and is
    /// still correct**. What changed is its STATUS. It was once the SOLE mechanism, and as the sole
    /// mechanism it was wrong in both directions — it refused maskable popups AND it masked whole monitors,
    /// because a WPF light-dismiss overlay IS a popup and IS the whole screen. This geometric rule is now
    /// the primary decision; the fork remains beside it as an identity stop that geometry cannot express
    /// (see BlacksOutTheCapture on why a MOVING window defeats geometry alone).
    ///
    /// The check applies ONLY to escalated rects. An element whose OWN rect covers the capture is genuinely
    /// that large, and masking it is correct rather than a degradation.</summary>
    /// <param name="captureYardstick">The region an escalated mask is judged against. MUST be
    /// non-degenerate (positive width AND height); the caller guarantees that, and this function fails
    /// CLOSED if the guarantee is broken. Normally the capture rect clipped to the renderable desktop;
    /// the unclipped rect when clipping would be degenerate. It is NOT necessarily equal to the rect that
    /// ends up captured.</param>
    public static MaskResolution Resolve(Rectangle? ownRect, Func<int, Rectangle?> ancestorRect,
                                         Rectangle captureYardstick)
    {
        if (HasArea(ownRect)) return new MaskResolution(ownRect.Value, Escalated: false, Refused: false);

        // ⚠ A DEGENERATE yardstick makes both loop tests meaningless — nothing IntersectsWith a rect of
        // zero width or height, so every candidate would be discarded and, under an earlier design that
        // DROPPED such candidates, the capture came back UNMASKED. The caller guarantees a non-degenerate
        // yardstick; if that guarantee is ever broken, fail CLOSED rather than turning a guard into a leak.
        //
        // ⚠ HOISTED ABOVE THE LOOP, and the position is the whole point. A review seat called this check
        // redundant, and it is RIGHT ABOUT THE OUTCOME: the overlap test below would reject every candidate
        // anyway and the loop would refuse at the cap. It is kept because "at the cap" means
        // MaxAncestorLevels ANCESTOR FETCHES first, each a cross-process COM round trip on the single query
        // STA, for a question already answered. But INSIDE the loop it did not actually deliver that — it
        // could not run until an ancestor with area had already been fetched, so the short-circuit
        // short-circuited nothing. The yardstick does not change between iterations; testing it once, here,
        // is the only position at which this check does what it is justified by.
        //
        // ⚠ Tested on EXTENTS, not Rectangle.IsEmpty. IsEmpty requires all four fields to be zero, while
        // Rectangle.Intersect compares with `>=` and so yields a zero-width rect at non-zero coordinates —
        // (100, 50, 0, 30) — for two rects that merely touch along an edge. IsEmpty is false there and the
        // leak would return.
        if (captureYardstick.Width <= 0 || captureYardstick.Height <= 0) return MaskResolution.Refusal;

        for (int level = 1; level <= MaxAncestorLevels; level++)
        {
            var r = ancestorRect(level);
            if (!HasArea(r)) continue;

            // ⚠⚠ A CANDIDATE THAT DOES NOT OVERLAP THE CAPTURE IS UNUSABLE — climb on. An earlier revision
            // DROPPED it instead, reasoning that a secret inside an off-capture ancestor cannot appear in
            // the photograph. **That reasoning codified a leak**, and the test that asserted it was
            // asserting the bug: UIA logical parents do not always enclose their visual children. A WPF
            // tooltip, popup or drag adorner can be visually ON SCREEN while its logical parent — a
            // scrolled-away button, say — is entirely off it. If the tooltip's own bounds throw, its
            // off-screen parent "cannot appear in the capture", the mask is dropped, and the tooltip is
            // photographed in the clear. Treating it as unusable keeps climbing toward an ancestor that
            // does overlap, and refuses if none does.
            //
            // The cost is accepted and is the fail-closed direction: an element-scoped capture whose
            // unrelated sibling escalates past it now refuses rather than guessing the secret is elsewhere.
            if (!r.Value.IntersectsWith(captureYardstick)) continue;

            // Covering the capture entirely is not a mask either — see BlacksOutTheCapture.
            if (BlacksOutTheCapture(r.Value, captureYardstick)) continue;

            return new MaskResolution(r.Value, Escalated: true, Refused: false);
        }

        return MaskResolution.Refusal;
    }

    /// <summary>An escalated mask that CONTAINS the captured region hides everything, so it is not a mask.
    ///
    /// ⚠⚠ THE YARDSTICK IS NORMALLY THE VISIBLE CAPTURE — the capture rect INTERSECTED with the renderable
    /// desktop — and that distinction is most of the correctness of this check.
    ///
    /// ⚠ It is NOT always clipped, and this function must not assume it is. Read the contract on the
    /// parameter, not this paragraph: the ONE case where the caller passes the UNCLIPPED rect is a
    /// window- or element-scoped capture of a target with no renderable overlap, where clipping yields a
    /// degenerate rect that nothing intersects — and judging every mask against THAT would drop them all
    /// and return an unmasked image. An earlier revision of this comment asserted the yardstick was always
    /// clipped while the caller had already been changed to sometimes pass the raw rect, which is exactly
    /// the kind of confidently-wrong invariant that gets a guard "simplified" away by the next reader. A MAXIMIZED window's UIA BoundingRectangle BLEEDS PAST the monitor — its
    /// invisible resize border gives it a negative origin and a width and height larger than the screen. A
    /// WPF light-dismiss overlay is exactly the monitor. Against the raw rect, `overlay.Contains(window)` is
    /// FALSE (because -8 &lt; 0), so the full-monitor mask sails straight past this guard and returns the
    /// all-black image it exists to reject. Intersecting the capture with the renderable desktop first
    /// removes the bleed and the comparison becomes the one that was meant.
    ///
    /// ⚠ Honest limit, stated because the next reader will look for a tolerance and should know there is
    /// deliberately none: this is exact containment, not "covers most of". A rect one pixel short on one
    /// edge passes and blacks out ~99.99% of the image. A percentage threshold would close that and would
    /// introduce a tuning knob on a withholding path, which this design refuses on principle — so the
    /// root-identity stop in AncestorRectSource is kept as well. Neither guard is sufficient alone:
    /// identity catches the exact root whatever its geometry, geometry catches everything identity cannot
    /// name.
    ///
    /// ⚠ If you are about to delete the identity stop as redundant — someone will, because in the common
    /// case this geometric check rejects the window root anyway — here is the case that makes it
    /// load-bearing, because it is not obvious. `captureBounds` is read ONCE at the start of the walk; an
    /// ancestor's rect is read DURING it. On a window that MOVES in between, the window root's live rect
    /// no longer contains the stale `yardstick`, this check PASSES, and the root is accepted as a
    /// mask — producing an image blacked out in the wrong place with the secret possibly still visible.
    /// The identity stop does not care where the window is.</summary>
    public static bool BlacksOutTheCapture(Rectangle mask, Rectangle captureYardstick)
        => mask.Contains(captureYardstick);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~MaskEscalationTests"`
Expected: `Passed!  - Failed: 0, Passed: 16`.

- [ ] **Step 5: Prove the pins are non-vacuous (three temporary LOGIC MUTANTS)**

Apply each mutant on its own, run the named command, confirm the NAMED test is the one that goes red, then
REVERT before applying the next.

| Mutant | Edit | Test that must go red |
|---|---|---|
| Drop the escalate branch | change `if (HasArea(r)) return new MaskResolution(r.Value, Escalated: true, Refused: false);` to `if (HasArea(r)) return new MaskResolution(r.Value, Escalated: false, Refused: false);` | `An_unreadable_own_rect_escalates_to_the_first_usable_ancestor` |
| Drop the root-refusal branch | change `return MaskResolution.Refusal;` to `return new MaskResolution(default, false, false);` | `No_usable_ancestor_refuses_rather_than_returning_a_rect` |
| Weaken the usability rule | change `HasArea` to `r is not null` | `A_zero_size_own_rect_is_unusable_and_escalates` |
| Drop the blacks-out guard | change `BlacksOutTheCapture` to `=> false` | `An_escalated_rect_that_covers_the_capture_is_rejected_and_the_walk_continues` |
| Apply the guard to the element's OWN rect too | move the `!BlacksOutTheCapture(...)` test onto the `HasArea(ownRect)` branch | `An_elements_OWN_capture_covering_rect_is_masked_not_refused` |
| Restore the outside-capture DROP | change the `IntersectsWith` guard to `return new MaskResolution(default, true, false);` | `An_element_whose_every_ancestor_misses_the_capture_refuses` |
| Use `IsEmpty` for the degenerate yardstick | change the extents test to `if (yardstick.IsEmpty)` | `A_degenerate_yardstick_refuses_rather_than_dropping_every_mask` (the two non-zero-origin rows) |

Run each as: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~MaskEscalationTests"`

⚠ Do NOT mutate the loop bound to `while (true)` — that hangs the suite rather than failing it. To pin the
cap, change `level <= MaxAncestorLevels` to `level <= MaxAncestorLevels + 1` and confirm
`The_walk_stops_at_the_depth_cap_and_refuses` goes red on the call count. Revert.

- [ ] **Step 6: Run the headless gate and commit**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total up by 16.

```bash
git add src/FlaUI.Mcp.Core/Perception/MaskEscalation.cs test/FlaUI.Mcp.Tests/Perception/MaskEscalationTests.cs
git commit -m "feat(sp4): A1 - the mask-escalation decision, as a pure function

A redact-worthy element must contribute a usable mask rect; if it cannot,
escalate to its parent; if no usable ancestor remains, REFUSE. Extracted with
no UIA in it so every row of the case table is a headless fact - including the
ones a fixture cannot stage, because a throwing parent fetch and an absent
ancestor present identically as null.

One rule, not a taxonomy: branching on WHY a rect is missing multiplies
exception handling on the one path whose job is to withhold. The accepted cost
- a vanished element over-masking a benign parent region on an animating UI -
is stated in the type's own doc rather than implied.

The depth cap is termination, not tuning: an uncapped climb on a cyclic
provider tree hangs the single query STA, which wedges every LATER query.

All 16 pins proven non-vacuous by logic mutants (drop the escalate branch, drop
the refusal branch, weaken the zero-area rule, widen the cap).

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: A1 — the ancestor adapter and the mask collection

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/AncestorRectSource.cs`
- Modify: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:848` (signature), `:862-871` (body), `:942` (record), `:884,888,899` (rename)
- Modify: `src/FlaUI.Mcp.Core/Perception/TextCaptureGeometry.cs:13` (rename)

- [ ] **Step 1: STATE-VERIFY**

Confirm `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` lines 861-871 are exactly:

```csharp
            var target = string.IsNullOrEmpty(@ref) ? (AutomationElement)win : _refs.Resolve(handle.Id, @ref!, PopupFinder.SearchRoots(win, desktop));
            var pw = new List<System.Drawing.Rectangle>();
            foreach (var rootEl in PopupFinder.SearchRoots(win, desktop))
            {
                // DEF-1: this read used to be RAW (`d.Properties.IsPassword.ValueOrDefault`) inside the inner
                // catch, so a provider that THREW yielded no rect — text redacted, PIXELS CAPTURED, while all
                // eleven text sites failed closed. SensitivityOf routes through the same classifier, which
                // fails closed on a throw AND honours configured rules, not just the OS password flag.
                try { foreach (var d in rootEl.FindAllDescendants()) { try { if (ElementContent.SensitivityOf(d, _classifier, procName).Redact) pw.Add(d.BoundingRectangle); } catch { } } } catch { }
            }
            return new CaptureGeometry(target.BoundingRectangle, pw, false, false, null);
```

Confirm line 942 is exactly:

```csharp
public sealed record CaptureGeometry(System.Drawing.Rectangle Bounds, IReadOnlyList<System.Drawing.Rectangle> PasswordRects, bool Minimized, bool Denied, string? DeniedProcess);
```

Confirm the private helper at `:667` is exactly:

```csharp
    private static T SafeRead<T>(Func<T> read, T fallback) { try { return read(); } catch { return fallback; } }
```

If any differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Add the error code**

In `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`, append a member after `OcrUnavailable` (the last entry):

```csharp
    OcrUnavailable,
    RedactionUnmaskable
}
```

Reusing `CaptureUnavailable` was rejected: it already means "locked/disconnected desktop", so a script
would have to scrape the message text to tell "desktop unavailable" from "cannot mask safely" — the same
output-contradicts-code defect SP3 fixed in `check-redaction-rules`. Appending is safe because
`ToolResponse` serializes `ex.Code.ToString()` (`ToolResponse.cs:21,33`), never the ordinal.

- [ ] **Step 3: Write the ancestor adapter**

Create `src/FlaUI.Mcp.Core/Perception/AncestorRectSource.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>SP4/A1. The production ancestor source for <see cref="MaskEscalation"/>: walks REAL ancestors
/// on the raw view and memoizes each resolved rect for the lifetime of ONE capture. This is the only UIA
/// in the escalation path.
///
/// RAW view, not control view, because the enumeration being masked is `FindAllDescendants()` with no
/// condition. PerceptionManager.cs:152 records why a TreeWalker was rejected for the SELECTOR walk — a
/// control-view/raw-view difference could change which nodes the count==1 guarantee sees. That reasoning
/// does not reach this walk: it counts nothing and decides no identity, it only hunts for an enclosing
/// rectangle. A node this walk sees that the enumeration did not is harmlessly skipped when its rect is
/// unusable, and harmlessly used as a mask when it is not.
///
/// ⚠⚠ THE WALK IS BOUNDED AT THE ROOT, AND THAT BOUND IS THE WHOLE FEATURE. An earlier draft of this type
/// climbed with GetParent and no stop condition. That draft was WRONG in the most dangerous possible way:
/// GetParent SUCCEEDS at the window root, and the window root HAS valid bounds — so escalation would have
/// returned the root's rect, masked the entire window, and handed back a SUCCESSFUL all-black screenshot.
/// That is precisely the outcome A1 exists to prevent, because an agent hallucinates an all-black image's
/// contents or loops on it, where a refusal forces a strategy change. It would never have thrown
/// RedactionUnmaskable at all, and the headless tests would still have passed — they inject a fake whose
/// chain ends in null, so they described a root boundary the real walk did not have. A green suite over a
/// dead guarantee.
///
/// The bound is the root's RuntimeId, read ONCE at construction. An ancestor whose RuntimeId matches it is
/// reported as "no rect at this level", so reaching the root is indistinguishable from running out of
/// ancestors — which is exactly what MaskEscalation.Resolve turns into a refusal.
///
/// ⚠ If the root's own RuntimeId cannot be read, the walk CANNOT be bounded safely, so every ancestor
/// request answers null and the element refuses. Fail closed: an unbounded climb would sail past the root
/// into the desktop, and masking the desktop is a worse answer than refusing one capture.
///
/// ⚠ MEMOIZATION: the honest justification is COHERENCE, not cost. If one container's bounds read is
/// broken, every one of its redact-worthy children escalates through the SAME chain; without a cache, two
/// siblings failing at different moments read a SCROLLING container's bounds at different times and produce
/// two MISALIGNED masks of one region. The cache locks one rect for the whole pass.
/// It does NOT save cross-process traffic, and the earlier draft's claim that it did was wrong: the key is
/// the ancestor's RuntimeId, which must be READ before the cache can be consulted, so N siblings still cost
/// N RuntimeId reads. The cache trades one COM read for another of the same order and buys coherence with
/// the difference. Stated plainly because "it's an optimisation" would be false.
///
/// ⚠ THE CACHE KEY IS NOT THE ELEMENT OBJECT. Keying a dictionary on AutomationElement invokes its
/// GetHashCode, which fetches RuntimeId cross-process anyway — but INVISIBLY, and once per lookup rather
/// than once per ancestor. Reading it explicitly keeps the cost where a reader can see it.
///
/// One instance per SEARCH ROOT (the window, or one popup), not one per capture: ancestor chains never
/// cross a root boundary, so a per-root cache is exactly as coherent and its bound is unambiguous. Never
/// reuse an instance across captures — a rect is frozen for one pass and must be re-read on the next.</summary>
public sealed class AncestorRectSource
{
    private readonly Dictionary<string, Rectangle?> _byRuntimeId = new(StringComparer.Ordinal);
    private readonly AutomationElement _root;
    private readonly string? _rootId;
    private readonly bool _rootIsWindow;

    /// <summary><paramref name="root"/> is the search root this source is bounded by — the window element,
    /// or the popup element, whichever subtree is being scanned. <paramref name="rootIsWindow"/> decides
    /// what happens WHEN the climb reaches it, and the two answers are genuinely different:
    ///
    /// • **The window root (true): REFUSE.** Masking it blacks out the entire capture, and a successful
    ///   all-black image is worse than an error — an agent hallucinates its contents or loops on it.
    /// • **A popup root (false): OFFER IT, then stop.** Masking a small popup blacks out the popup and
    ///   leaves the rest of the capture intact, which is ordinary over-masking and the direction this
    ///   feature deliberately degrades in. Refusing there would be strictly worse AND stricter than the
    ///   spec, whose rule names the WINDOW root specifically.
    ///   ⚠ OFFER, not decide. Whether that rect is ACCEPTED is MaskEscalation's call, and it rejects one
    ///   that covers the captured region — which is exactly the WPF light-dismiss popup, whose host is a
    ///   transparent FULL-SCREEN overlay. Its BoundingRectangle is the whole monitor while the visible
    ///   popup is small, so "it is only a popup, masking it is safe" is false for the most common popup
    ///   shape there is. This class must not make that judgement itself; it has no idea what is being
    ///   captured.
    ///
    /// ⚠ That distinction is not academic. PopupFinder returns Path-2 popups — WPF/.NET popup hosts that
    /// are direct CHILDREN of the window (PopupFinder.cs:63-78) — as their own search roots, while
    /// roots[0].FindAllDescendants() already covers those same subtrees. So every Path-2 popup's contents
    /// are scanned TWICE. With a refuse-at-every-root bound, pass 0 masks such an element successfully via
    /// the popup and pass 1 then REFUSES the identical element: the redundant scan would crash a capture
    /// the first pass had already handled correctly.</summary>
    public AncestorRectSource(AutomationElement root, bool rootIsWindow)
    { _root = root; _rootId = IdOf(root); _rootIsWindow = rootIsWindow; }

    /// <summary>The lazy accessor <see cref="MaskEscalation.Resolve"/> consumes, bound to ONE element.
    /// Level 1 is that element's parent. Returns null for "no rect at this level", covering the parent
    /// fetch throwing, the bounds read throwing, the bounds coming back empty, and there being no further
    /// ancestor.
    ///
    /// ⚠ Reaching the root is NOT one of those: the root is OFFERED (its rect returned) unless it is the
    /// window root, and the walk then stops. An earlier revision returned null there too and this sentence
    /// still said so one revision after it stopped being true — check the code, not this comment, if the
    /// two ever disagree again.
    ///
    /// The cursor only ever moves FORWARD. Resolve requests levels strictly in ascending order, so each
    /// ancestor of this element is fetched at most once, before the shared cache is consulted at all.</summary>
    public Func<int, Rectangle?> For(AutomationElement element)
    {
        // Unbounded is not an option — see the root-bound warning on this type. With no readable root id
        // there is nothing to stop the climb at, so every level answers "unusable" and the element refuses.
        if (_rootId is null) return _ => null;

        AutomationElement? cursor = element;
        string? lastId = null;
        int reached = 0;
        bool exhausted = false;
        return level =>
        {
            if (exhausted) return null;
            while (reached < level)
            {
                cursor = Parent(cursor);
                reached++;
                if (cursor is null) { exhausted = true; return null; }

                // ⚠ AN UNREADABLE ANCESTOR IDENTITY DOES NOT ABORT THE WALK, and an earlier draft's
                // decision that it should was over-refusal: IdOf answers null both when the read throws AND
                // for any element whose provider exposes no RuntimeId, and one anonymous intermediate
                // container — a bare WPF Border is the ordinary case — would have refused the whole capture
                // while perfectly good ancestors sat one level above it.
                //
                // Climbing on is safe ONLY because the identity stop is not the sole guard: MaskEscalation
                // rejects any escalated rect that covers the captured region, so a walk that escapes this
                // root through an unreadable ancestor still cannot return the desktop as a mask — that rect
                // blacks out the capture, is rejected, and the walk runs to its cap and REFUSES. Identity
                // catches the exact root whatever its geometry; geometry catches everything identity cannot
                // name. Neither is sufficient alone, which is why both are here.
                string? id = IdOf(cursor);
                lastId = id;

                // string.Equals(null, x, Ordinal) is FALSE, so no null guard is needed here — and an
                // unreadable id must NOT stop the walk anyway; see the paragraph above, which is the whole
                // reason this reads as a comparison rather than an abort.
                if (string.Equals(id, _rootId, StringComparison.Ordinal))
                {
                    exhausted = true; // never climb PAST the root
                    // The window root is never a usable mask — its rect IS the capture. A popup root may be,
                    // and MaskEscalation makes that call geometrically: a small popup masks, a full-screen
                    // light-dismiss overlay is rejected there for covering the capture.
                    // `cursor` IS the root here and its id is in hand — do not re-read either.
                    return _rootIsWindow ? null : RectOf(cursor, id);
                }
            }
            // The id was read on the way in; passing it on is not a micro-optimisation but the difference
            // between ONE cross-process RuntimeId read per level and THREE (the bound check, then the cache
            // key, then the popup branch). On the one path whose cost the memoization was supposed to
            // contain, reading the same property three times would have made the cache a net loss.
            return cursor is null ? null : RectOf(cursor, lastId);
        };
    }

    private static AutomationElement? Parent(AutomationElement? el)
    {
        if (el is null) return null;
        // The walker is fetched per step rather than cached in a field so this type names no FlaUI walker
        // type. It only runs on the escalation path, which is rare by construction: an element that reports
        // its own bounds never reaches here at all.
        try { return el.Automation.TreeWalkerFactory.GetRawViewWalker().GetParent(el); }
        catch { return null; }
    }

    private static string? IdOf(AutomationElement el)
    {
        try
        {
            var rid = el.Properties.RuntimeId.ValueOrDefault;
            return rid is not null && rid.Length > 0 ? string.Join(",", rid) : null;
        }
        catch { return null; }
    }

    /// <summary><paramref name="key"/> is the element's already-read RuntimeId — the caller has it, and
    /// re-reading it here would cost a second cross-process call per level.</summary>
    private Rectangle? RectOf(AutomationElement el, string? key)
    {
        if (key is not null && _byRuntimeId.TryGetValue(key, out var cached)) return cached;

        Rectangle? rect = null;
        try { rect = el.BoundingRectangle; } catch { }
        if (key is not null) _byRuntimeId[key] = rect;
        return rect;  // an ancestor whose RuntimeId is unreadable resolves UNCACHED rather than being
                      // skipped — losing the cache must never lose the mask
    }
}
```

⚠ **Sweep note, so it is not a surprise:** this type reads `RuntimeId` and `BoundingRectangle`, both of
which are in `StructuralProperties` (`RedactionSurfaceInventoryTests.cs:92-96`) and are never flagged by
RULE 1. It declares no `SensitivityClassifier` parameter, so RULE 4b does not apply. **No allowlist entry
is needed for this file.** If the sweep flags it anyway, STOP and report rather than adding an entry.

- [ ] **Step 4: Widen `CaptureGeometry`**

Replace `PerceptionManager.cs:942`:

```csharp
public sealed record CaptureGeometry(System.Drawing.Rectangle Bounds, IReadOnlyList<System.Drawing.Rectangle> MaskRects, bool Minimized, bool Denied, string? DeniedProcess,
    IReadOnlyList<MaskEscalationEntry> Escalations);
```

Update the two early-return construction sites, `:853` and `:858`, to pass an empty list:

```csharp
                return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), false, true, procName, System.Array.Empty<MaskEscalationEntry>());
```

```csharp
                    return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), true, false, null, System.Array.Empty<MaskEscalationEntry>());
```

A default parameter value was rejected: a new mask-reporting field that silently defaults to "nothing to
report" is exactly the shape that lets a future construction site forget it.

**The complete consumer set of `CaptureGeometry`, so the widening is not a surprise anywhere.** Three
construction sites, all in this one method (`:853`, `:858`, `:871`), and three readers:
`ScreenshotTools.cs:46` (updated in Task 5a), `PerceptionManager.ResolveTextCaptureGeometryAsync:881`, and
`RedactionOracleTests.cs:45`. **The last two need no edit and that is deliberate, not an oversight**:
`ResolveTextCaptureGeometryAsync` projects `geo` into a `TextCaptureGeometry` and drops `Escalated` on the
floor, which is correct — `desktop_find_text` returns OCR matches, not capture metadata, so it has nowhere
to report an escalation and no consumer expecting one. It still inherits the REFUSAL, which is the part
that matters, via Task 5b.

- [ ] **Step 4a: Rename `PasswordRects` → `MaskRects` and `AllPasswordRectsAsync` → `AllMaskRectsAsync`**

Same defect class as A2, and named by a review seat as the last thing standing between this plan and
"ready". The field has held rects from EVERY redaction source since SP3 — OS password fields, operator
rules, and unreadable-identity fail-closed — and after A1 it also holds ancestor rects from escalation, so
"password" is wrong three ways over.

⚠ **INTERNAL ONLY — this is not a wire change and must not become one.** `CaptureGeometry` and
`TextCaptureGeometry` are internal DTOs; no JSON field is named `passwordRects`. A2 is the wire half of
this same defect and is the only half with a back-compat cost.

Rename the member on both records and at every use site:

| File | What |
|---|---|
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:942` | `CaptureGeometry.PasswordRects` → `MaskRects` |
| `src/FlaUI.Mcp.Core/Perception/TextCaptureGeometry.cs:13` | `TextCaptureGeometry.PasswordRects` → `MaskRects` |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:884,888` | the two `geo.PasswordRects` reads in `ResolveTextCaptureGeometryAsync` |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:899` | `AllPasswordRectsAsync` → `AllMaskRectsAsync` |
| `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:41,49` | the call and the `geo.PasswordRects` read |
| `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:106` | `geo.PasswordRects` passed to `CaptureRectangle` |
| `test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs:49` | `Assert.NotEmpty(geo.PasswordRects)` |

⚠ The test edit is a RENAME ONLY — `Assert.NotEmpty` and its subject are unchanged, so standing rule 3 is
satisfied (a construction fix forced by a signature change is allowed; changing an assertion is not).

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`. Any file the compiler names that is NOT in the table above means the
enumeration was incomplete — STOP and report it rather than fixing it silently.

- [ ] **Step 4b: Add the `skipIfNoRenderableOverlap` parameter**

Replace the signature line at `PerceptionManager.cs:848`, which currently reads:

```csharp
    public Task<CaptureGeometry> ResolveWindowCaptureGeometryAsync(WindowHandle handle, string? @ref) =>
```

with:

```csharp
    /// <param name="skipIfNoRenderableOverlap">TRUE only for the full-desktop mask sweep, where a window with no
    /// renderable overlap contributes no pixels and may be skipped. FALSE for a caller that NAMED this
    /// window and will photograph its rect regardless — suppressing that window's masks would hand back an
    /// unmasked image of the named target, which is the one thing this feature must not do.</param>
    public Task<CaptureGeometry> ResolveWindowCaptureGeometryAsync(WindowHandle handle, string? @ref,
                                                                   bool skipIfNoRenderableOverlap = false) =>
```

The default is `false`, so every existing call site keeps the safe behaviour with no edit; only the
full-desktop sweep opts in, in Task 5b.

- [ ] **Step 5: Replace the mask collection**

⚠ **ANCHOR ON THE CONTENT, NOT THE LINE NUMBERS — Step 4b has already moved them.** Step 4b replaces the
one-line signature at `:848` with a six-line block (five `<param>` doc lines plus a wrapped signature), so
everything below it has shifted DOWN by five: the region originally at `:862-871` now sits at `:867-876`.
Both numbers are given because a plan that names only the stale one is how an executor edits the wrong
lines confidently.

Replace the region running from `var pw = new List<System.Drawing.Rectangle>();` through the
`return new CaptureGeometry(...);` — originally `PerceptionManager.cs:862-871`, `:867-876` after Step 4b —
with:

```csharp
            // ⚠ EVERY FAILURE FROM HERE TO THE RETURN MUST BECOME RedactionUnmaskable, NEVER A RAW
            // EXCEPTION. AllMaskRectsAsync (the FULL-DESKTOP path) wraps this call in `catch { }` to
            // skip a window it cannot bind, and rethrows ONLY RedactionUnmaskable. So a raw COMException
            // escaping here does not fail the capture — it silently drops this window's ENTIRE mask set and
            // photographs it in the clear. That is a leak, and it defeats two decisions at once: A1's
            // refusal, and the strict-on-roots[0] rule below, whose whole point is that a dead target must
            // not be quietly skipped.
            //
            // Note this hazard PREDATES SP4 — the old code read target.BoundingRectangle unguarded at its
            // return statement, with the same swallow downstream. It is fixed here because A1 is what makes
            // the difference between "no mask" and "refuse" load-bearing.
            //
            // This read keeps its OWN guard even though a blanket conversion follows, because it is the one
            // failure worth naming precisely in the message an operator reads.
            System.Drawing.Rectangle captureBounds;
            try { captureBounds = target.BoundingRectangle; }
            catch (System.Exception ex)
            {
                throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                    $"Could not read the capture bounds of window '{handle.Id}' (process '{procName}'), so its redacted regions cannot be located ({ex.GetType().Name})",
                    "retry once the UI has settled, or capture a different window");
            }

            // The yardstick is the capture clipped to the RENDERABLE desktop. A maximized window's rect
            // bleeds past the monitor by its invisible resize border, and comparing against that bleed is
            // what let a full-monitor mask pass the blacks-out check. Read once, so the rect the decision
            // judges against is the same rect returned as Bounds and ultimately captured.
            // ⚠ It does NOT eliminate movement-induced misalignment: if the window moves mid-walk, live mask
            // rects land against a stale capture rect. Reading it AFTER the walk just inverts which side is
            // stale. That race is inherent to capturing a moving window — do not read this as claiming
            // otherwise.
            var yardstick = System.Drawing.Rectangle.Intersect(captureBounds, ScreenCapture.VirtualScreenBounds());

            // ⚠ A window with NO renderable overlap contributes no pixels to any capture, so there is
            // nothing here to withhold and nothing to refuse over. Returning early is not a shortcut: it is
            // what stops one invisible off-screen window with a broken provider from refusing — and, on the
            // full-desktop path, FATALLY FAILING — a capture it could not have appeared in.
            //
            // ⚠ Tested on EXTENTS, not Rectangle.IsEmpty. IsEmpty requires all four fields to be zero, while
            // Rectangle.Intersect compares with `>=` and so yields a zero-width rect at NON-ZERO coordinates
            // — (100, 50, 0, 30) — for two rects that merely touch along an edge. IsEmpty is false there,
            // and the walk would then judge every mask against a degenerate yardstick that nothing
            // intersects: every mask dropped, capture returned unmasked. A guard producing a leak.
            if (yardstick.Width <= 0 || yardstick.Height <= 0)
            {
                // ⚠⚠ TWO CALLERS, TWO CORRECT ANSWERS, AND THEY ARE NOT THE SAME ANSWER. This is why the
                // parameter exists: the method cannot infer which caller it is serving, and guessing was
                // wrong in BOTH directions across two review rounds.
                //
                // FULL-DESKTOP (skipIfNoRenderableOverlap: true): a window with no renderable overlap contributes no
                // pixels to a virtual-screen capture, so there is nothing to withhold. Return an empty mask
                // set. This is what stops ONE invisible off-screen window with a broken provider from
                // refusing — and so fatally failing — a whole-desktop capture it could not have appeared in.
                //
                // WINDOW- OR ELEMENT-SCOPED (false, the default): the caller NAMED this window and will
                // photograph its rect whatever is or is not rendered there. Suppressing its masks would
                // hand back an unmasked image of the named target, which is the one thing this feature must
                // not do. Fall back to the unclipped rect and compute masks normally — the maximized-bleed
                // case the clipping exists for cannot arise when nothing is on screen to bleed over.
                if (skipIfNoRenderableOverlap)
                    return new CaptureGeometry(captureBounds, System.Array.Empty<System.Drawing.Rectangle>(),
                                               false, false, null, System.Array.Empty<MaskEscalationEntry>());
                // ⚠ The UNCLIPPED rect becomes the yardstick here, and MaskEscalation's parameter contract
                // says that is allowed: the requirement is NON-DEGENERATE, not "clipped". Clipping has
                // already produced a degenerate rect for this target, and judging every mask against THAT
                // would discard them all and return an unmasked image of a window the caller named.
                yardstick = captureBounds;
            }

            var pw = new List<System.Drawing.Rectangle>();
            var escalations = new List<MaskEscalationEntry>();

            // ⚠ ONE BLANKET CONVERSION, not a guard per read. Round 4 wrapped the capture-bounds read and
            // the roots[0] enumeration individually and STILL missed PopupFinder.SearchRoots, which is a
            // UIA walk of its own. Any raw exception escaping this region is swallowed by
            // AllMaskRectsAsync's `catch { }` on the full-desktop path and becomes a window photographed
            // with NO mask set — so the safe default has to be structural, not a list of remembered sites.
            //
            // A ToolException passes through UNCHANGED: those are deliberate, already-classified outcomes
            // (RefNotFound from the ref resolution above, and the RedactionUnmaskable refusals raised
            // inside the loop), and re-wrapping them would destroy the code an agent branches on.
            //
            // ⚠ THE EXCEPTION'S TYPE NAME GOES ON THE WIRE, NEVER ITS MESSAGE. An earlier revision
            // interpolated ex.Message, which is UNCLASSIFIED THIRD-PARTY TEXT of unknown provenance being
            // written into a payload an agent reads — a direct violation of this increment's own binding
            // constraint 1 ("no content may reach the wire unclassified"), inside the one feature whose
            // whole subject is that constraint. A type name is bounded, diagnostic enough to route an
            // investigation, and cannot carry an element's Name or value.
            //
            // Nothing on the query path is cancellable, so this broad catch reclassifies no cancellation:
            // VERIFIED at AutomationDispatcher.cs:61, RunQueryAsync is `_query.RunAsync(func)` with no
            // timeout and no CancellationToken (only the ACTION path has AwaitWithTimeout).
            try
            {
            var roots = PopupFinder.SearchRoots(win, desktop);
            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                // ⚠ ONE SOURCE PER ROOT, and it is CONSTRUCTED FROM THAT ROOT. The root is what bounds the
                // ancestor climb: without it the walk sails through the window into the desktop, masks
                // everything, and returns a successful all-black image instead of refusing. Ancestor chains
                // never cross a root boundary, so a per-root cache is exactly as coherent as a per-capture
                // one and its bound is unambiguous.
                var ancestors = new AncestorRectSource(roots[rootIndex], rootIsWindow: rootIndex == 0);
                AutomationElement[] descendants;
                if (rootIndex == 0)
                {
                    // roots[0] IS the window (PopupFinder.cs:16). A failure here is the TARGET dying, not a
                    // popup closing mid-scan, and swallowing it returns a capture whose mask set is empty
                    // because the tree could not be read — a leak dressed as a successful screenshot.
                    // FindAsync (:583-596) and EvaluateSelectorValueAsync (:745-760) already draw exactly
                    // this line for exactly this reason; this makes the third sibling agree with them.
                    //
                    // ⚠ CONVERTED, not propagated raw. Letting a COMException escape would be caught by
                    // AllMaskRectsAsync's `catch { }` on the full-desktop path and turn this strictness
                    // into a SILENT SKIP - the precise opposite of the decision this branch encodes.
                    try { descendants = roots[rootIndex].FindAllDescendants(); }
                    catch (System.Exception ex)
                    {
                        throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                            $"Could not enumerate window '{handle.Id}' (process '{procName}'), so its redacted regions cannot be located ({ex.GetType().Name})",
                            "retry once the UI has settled, or capture a different window");
                    }
                }
                else
                {
                    // PER-ROOT isolation, POPUPS only: a tooltip or menu closing mid-scan must not fail the
                    // window's own capture. A root that throws contributes nothing.
                    try { descendants = roots[rootIndex].FindAllDescendants(); }
                    catch { continue; }
                }

                foreach (var d in descendants)
                {
                    // DEF-1: this read used to be RAW (`d.Properties.IsPassword.ValueOrDefault`) inside a
                    // swallowing catch, so a provider that THREW yielded no rect — text redacted, PIXELS
                    // CAPTURED, while all eleven text sites failed closed. SensitivityOf routes through the
                    // same classifier, which fails closed on a throw AND honours configured rules.
                    // ⚠ THIS CATCH IS UNREACHABLE TODAY, and saying so is the point of writing it down.
                    // TRACED at ef17ed9: every path inside SensitivityOf swallows — RedactionPolicy
                    // .IsPasswordOrFailClosed (RedactionPolicy.cs:10), ElementContent.SafeIdentity
                    // (ElementContent.cs:137), RedactionRule.Safe and RedactionRule.IsMatch
                    // (RedactionRules.cs:66,73, the latter `catch { return true; }`). It is kept as
                    // defence-in-depth against a future change INSIDE the classifier, and it fails CLOSED,
                    // because the failure it guards against is a silent pixel leak. Do not read it as
                    // evidence that SensitivityOf throws, and do not write a test for it — nothing can
                    // reach it.
                    //
                    // The sharper reason to LABEL it rather than delete it: an unlabelled catch here reads
                    // as "the CALLER provides the fail-closed guarantee". A future developer who believed
                    // that could strip the fail-closed logic from inside ElementContent and leave the eleven
                    // TEXT egress sites - which have no such catch - silently leaking, while the pixel path
                    // kept working and hid the regression.
                    Sensitivity sens;
                    try { sens = ElementContent.SensitivityOf(d, _classifier, procName); }
                    catch { sens = Sensitivity.UnreadableIdentity; } // an element we cannot CLASSIFY is
                                                                     // masked, never skipped
                    if (!sens.Redact) continue;

                    // A1: the rect read used to sit in that same swallowing catch, so a redact-worthy
                    // element whose BoundingRectangle THREW contributed NO mask and its pixels were
                    // captured — the identical fail-OPEN shape as DEF-1, on the other half of one line.
                    System.Drawing.Rectangle? own = null;
                    try { own = d.BoundingRectangle; } catch { }

                    var resolution = MaskEscalation.Resolve(own, ancestors.For(d), yardstick);
                    string aid = SafeRead(() => d.AutomationId, "") ?? string.Empty;
                    string ct = SafeRead(() => d.ControlType, FlaUI.Core.Definitions.ControlType.Custom).ToString();
                    if (resolution.Refused)
                        // Escalating to the root and masking IT would return a SUCCESSFUL all-black image,
                        // which is worse than an error: an agent hallucinates its contents or loops on it.
                        //
                        // The message names automationId and controlType, NEVER the Name — that is the
                        // identity the mask exists to hide. It ALSO names the window handle and process,
                        // because this same method backs the FULL-DESKTOP capture: without them an operator
                        // whose whole-desktop screenshot just refused sees `controlType='Edit',
                        // automationId=''` for a desktop of a dozen windows and has nothing to act on. The
                        // window is not withheld content — desktop_list_windows publishes handle, process
                        // and title already.
                        throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                            $"A redact-worthy element in window '{handle.Id}' (process '{procName}') could not be masked (automationId='{aid}', controlType='{ct}'): neither it nor any ancestor reported usable bounds.",
                            "capture that window alone to confirm, or retry once the UI has settled");

                    pw.Add(resolution.Rect);
                    if (resolution.Escalated) escalations.Add(new MaskEscalationEntry(aid, ct));
                }
            }
            return new CaptureGeometry(captureBounds, pw, false, false, null, escalations);
            }
            catch (ToolException) { throw; }
            // ⚠ A CRITICAL failure is NOT a redaction outcome. Reclassifying OutOfMemoryException — or a
            // cancellation, if this path ever gains one — as RedactionUnmaskable would tell the agent to
            // "retry once the UI has settled" while the process is actually dying, and would hide the real
            // cause from every log above. (Measured: the query path has no cancellation today —
            // AutomationDispatcher.cs:61 is `_query.RunAsync(func)` with no timeout and no
            // CancellationToken; only the ACTION path has AwaitWithTimeout. The filter is there so that
            // stays true by construction if the query path ever gains one.)
            catch (System.Exception ex) when (ex is not System.OutOfMemoryException
                                              and not System.OperationCanceledException)
            {
                throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                    $"Could not determine the redacted regions of window '{handle.Id}' (process '{procName}'): {ex.GetType().Name}",
                    "retry once the UI has settled, or capture a different window");
            }
```

⚠ **Escalation depth is NOT uniform, and the implementer must not be surprised by it.** The walk enumerates
`FindAllDescendants()`, so an element's parent may BE the window root. On deep trees (WPF, Electron, modern
XAML) a single unreadable rect degrades gracefully — a local container is masked and a usable screenshot
still comes back. On FLAT trees (classic Win32 dialogs, where controls are direct children of the window)
the parent IS the root, so ONE unreadable rect refuses the whole capture as soon as its first escalation
step reaches that root. (Mechanically the loop still runs to its cap: once the source has stopped it answers
null immediately, so the remaining levels are near-free rather than skipped. The OUTCOME is decided at the
first step; do not read the prose as a claim about the loop count.) That
disparity is intended — refusing beats leaking — and Task 11 documents it, because "screenshots of this one
old app always fail" is otherwise an unexplainable report.

- [ ] **Step 6: Build and run the headless gate**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged from Task 3. In particular
`SourceTree_EnforcesRedactionSurfaceInvariants` must still be green.

- [ ] **Step 7: Commit**

```bash
# ⚠ Step 4a touches FIVE files, not one. Committing only PerceptionManager.cs would push a tree whose
# committed PerceptionManager references MaskRects/AllMaskRectsAsync while the four files that USE those
# names still say PasswordRects at that commit - a build-broken commit in history, and
# TextCaptureGeometry.cs would never be staged by ANY task in this plan.
git add src/FlaUI.Mcp.Core/Perception/AncestorRectSource.cs         src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs         src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs         src/FlaUI.Mcp.Core/Perception/TextCaptureGeometry.cs         src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs         src/FlaUI.Mcp.Server/Tools/FindTextTools.cs         test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs
git commit -m "fix(sp4): A1 - an unmaskable element escalates, then refuses

The pixel-mask rect collection failed OPEN: SP3 made SensitivityOf fail closed
but left the rect add inside the same swallowing catch, so a redact-worthy
element whose BoundingRectangle THREW yielded no mask and its pixels were
captured - the same defect class as DEF-1, on the other half of one line.

Now the rect resolves through MaskEscalation: precise when readable, an
ancestor's when not, and ToolException(RedactionUnmaskable) when no usable
ancestor remains. A new error code rather than CaptureUnavailable, so a script
never has to scrape message text to tell 'desktop unavailable' from 'cannot
mask safely'.

The ancestor source walks the raw view (matching the FindAllDescendants
enumeration it masks) and memoizes per capture, keyed on an explicitly-read
RuntimeId - never on the element object, whose GetHashCode fetches RuntimeId
cross-process and would make the cache generate the COM traffic it exists to
remove. Memoization's stronger justification is correctness: without it, two
siblings failing at different moments mask one scrolling container twice, out
of alignment.

Descendant enumeration is strict on roots[0] and lenient on popup roots,
matching FindAsync (:583-596) and EvaluateSelectorValueAsync (:745-760): a
failure at the window root is the target dying, a failure at a popup is a
tooltip closing mid-scan.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: A1 — the metadata and the refusal's reach (ONE task, two halves)

⚠ **RESUMING AFTER A HALT INSIDE THIS TASK.** Because 5a leaves the tree non-compiling, an executor that
stops between the halves comes back to a STATE-VERIFY in 5a that expects the ORIGINAL text and now finds
5a's own output. **That is NOT a `STATE_MISMATCH` — do not abort on it.** If `ScreenshotTools.cs` already
declares `IReadOnlyList<MaskEscalationEntry> escalations;` and reads `desk.Escalations`, 5a is already applied:
skip to 5b. Reporting a mismatch there would strand the repository in the one non-compiling gap this plan
contains, with no automated way out.

⚠ **5a and 5b are one compile unit, one gate and one commit.** An earlier draft split them into separate
numbered tasks and told the executor that the first would not compile — which contradicts standing rule 5
(run the headless gate after every task) and leaves no way to verify the first half's edits before moving
on. They are two decisions but one change: 5a consumes `DesktopMaskSet`, 5b defines it. Do not run a gate
between them; do not commit between them.

### Task 5a — the screenshot metadata

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:17,52`

Fail closed, but never silently (spec §10.2): where withholding degrades output, the caller must be able to
see that it happened AND identify what caused it, without the diagnostic itself leaking withheld content.

- [ ] **Step 1: STATE-VERIFY**

Confirm `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:51-52` is exactly:

```csharp
            var dpi = DpiHelper.ScaleForPoint(result.X, result.Y);
            return ToolResponse.Image(result.Png, new { bounds = new { x = result.X, y = result.Y, w = result.W, h = result.H }, dpiScale = dpi, scaleApplied = result.ScaleApplied, redactions = result.Redactions });
```

If it differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Carry the escalation list out of both capture branches**

Replace `ScreenshotTools.cs:30-52` (from `CaptureResult result;` through the `return ToolResponse.Image(...)`)
with:

```csharp
            CaptureResult result;
            IReadOnlyList<MaskEscalationEntry> escalations;
            if (string.IsNullOrEmpty(window))
            {
                var present = await _perception.DenylistedWindowsVisibleAsync();
                if (present)
                    throw new ToolException(ToolErrorCode.TargetDenied, "A credential/denylisted window is currently visible; full-desktop capture is refused.", "capture a specific non-sensitive window: desktop_screenshot window=<handle>");
                var vbounds = ScreenCapture.VirtualScreenBounds();
                // DEF-2: this passed Array.Empty<Rectangle>() — full-desktop capture masked NOTHING, even
                // though window- and element-scoped capture both masked correctly. The refusal above only
                // covers DENYLISTED windows; an ordinary window holding a password field was photographed
                // in the clear.
                var desk = await _perception.AllMaskRectsAsync();
                escalations = desk.Escalations;
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(vbounds, desk.Rects, maxWidth));
            }
            else
            {
                var geo = await _perception.ResolveWindowCaptureGeometryAsync(new WindowHandle(window!), @ref);
                if (geo.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"Capturing windows owned by '{geo.DeniedProcess}' is blocked.", "capture a non-sensitive window");
                if (geo.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");
                escalations = geo.Escalations;
                result = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.Bounds, geo.MaskRects, maxWidth));
            }
            var dpi = DpiHelper.ScaleForPoint(result.X, result.Y);
            // A1: maskEscalations counts ELEMENTS whose own rect was unusable and whose mask therefore came
            // from an ancestor — NOT levels climbed. One element climbing three levels counts as 1. Both
            // fields are ALWAYS present (0 / empty when nothing escalated), because a diagnostic that
            // appears only on failure teaches a consumer to ignore its absence.
            //
            // `escalated` carries automationId + controlType and NEVER the Name: a bare count is not a
            // diagnostic — an operator seeing a giant black box and maskEscalations:2 cannot tell which
            // control has the broken provider — while the Name is the identity the mask exists to hide.
            // Both fields are already published for redacted elements by desktop_find, so this surfaces
            // nothing the query path does not.
            return ToolResponse.Image(result.Png, new
            {
                bounds = new { x = result.X, y = result.Y, w = result.W, h = result.H },
                dpiScale = dpi,
                scaleApplied = result.ScaleApplied,
                redactions = result.Redactions,
                maskEscalations = escalations.Count,
                escalated = escalations.Select(e => new { automationId = e.AutomationId, controlType = e.ControlType })
            });
```

Add `using System.Linq;` and `using System.Collections.Generic;` to the top of the file if either is absent.

⚠ `AllMaskRectsAsync` does not yet return `.Rects` / `.Escalations` — 5b adds that. **Do not build or
gate here.** Continue straight into 5b; the tree is expected to be non-compiling in between, which is why
this is one task rather than two.

- [ ] **Step 3: Update the tool description**

Replace `ScreenshotTools.cs:17`, which currently reads:

```csharp
    [McpServerTool(ReadOnly = true), Description("Capture a window, an element (window+ref), or the full virtual desktop as a PNG. Returns a native image block + JSON metadata {bounds,dpiScale,scaleApplied,redactions}. Redacted elements (OS password fields, or an operator rule) are masked at capture time, full-desktop included (window/element scope covers popups; full-desktop is refused if a denylisted credential window is visible — capture a specific window instead). output must be 'inline' (file→NotImplemented). Focus the window first (no occlusion handling). Minimized→ElementNotActionable. Width is clamped to 1920.")]
```

with:

```csharp
    [McpServerTool(ReadOnly = true), Description("Capture a window, an element (window+ref), or the full virtual desktop as a PNG. Returns a native image block + JSON metadata {bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated}. Redacted elements (OS password fields, or an operator rule) are masked at capture time, full-desktop included (window/element scope covers popups; full-desktop is refused if a denylisted credential window is visible — capture a specific window instead). If a redacted element cannot report usable bounds its mask is taken from an ancestor: maskEscalations counts those ELEMENTS (not levels climbed) and escalated lists their {automationId,controlType} so you can tell which control has a broken provider. If no ancestor is usable either, the capture is REFUSED with RedactionUnmaskable rather than returning an all-black image - retry once the UI settles, or capture a different window. NOTE redactions counts rects PAINTED, so when several elements escalate to the SAME ancestor it exceeds the number of distinct black regions you can see; compare it against maskEscalations rather than reading it as a control count. output must be 'inline' (file→NotImplemented). Focus the window first (no occlusion handling). Minimized→ElementNotActionable. Width is clamped to 1920.")]
```

- [ ] **Step 4: Continue into 5b — no build, no gate, no commit here**

### Task 5b — propagate the refusal to full-desktop capture and OCR

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — the `AllMaskRectsAsync` member and its XML doc (pre-Task-4 `:891-914`; ~`:1089-1112` after Task 4's +198 — anchor on the member)
- Modify: `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:104`

Decisions D4 and D5. Both sites currently swallow a `ToolException` from geometry resolution, so without
this task A1's refusal is silently discarded on both — and full-desktop capture is the exact mode DEF-2 was
about.

- [ ] **Step 1: STATE-VERIFY**

⚠⚠ **THIS BLOCK QUOTES THE FILE AS TASK 4 LEAVES IT, NOT AS IT IS ON DISK TODAY — and it is the ONLY
STATE-VERIFY block in this plan that does.** Task 4 Step 4a renames `AllPasswordRectsAsync` and
`geo.PasswordRects`, and Task 4 runs first, so by the time an executor reaches here those two identifiers
have already changed. Quoting the pre-rename text would produce a `STATE_MISMATCH` caused by the plan's own
earlier step, halting the rollout halfway through — which is exactly what a review round caught. If you are
running the mechanical disk-diff over this plan's STATE-VERIFY blocks, EXCLUDE this one or apply Task 4a's
rename to the file first; a diff failure here is expected and is not a defect.

⚠⚠ **THE LINE NUMBERS BELOW ARE PRE-TASK-4 AND ARE NOW WRONG BY ~198 LINES. ANCHOR ON THE MEMBER NAME.**
Task 4 replaces a 10-line region with a 203-line one (Step 5) and a 1-line signature with a 6-line one
(Step 4b), so everything below `:848` in this file has moved DOWN by **198** by the time you arrive here:
`AllMaskRectsAsync` sits at roughly `:1097-1112` and its XML doc at roughly `:1089-1096`.

**Find the member by its declaration, not by counting.** An executor that trusts `:899-914` here overwrites
the dead centre of the 203-line block Task 4 just wrote — destroying the mask walk and leaving a file that
neither compiles nor resembles either version. Every "roughly" above is deliberate: the exact figure shifts
again with any edit to Task 4, so the SIGNATURE is the anchor and the numbers are orientation only.

Confirm the member `AllMaskRectsAsync` (pre-Task-4 `PerceptionManager.cs:899-914`, ~`:1097-1112` now) is
exactly (post-Task-4a):

```csharp
    public async Task<IReadOnlyList<System.Drawing.Rectangle>> AllMaskRectsAsync()
    {
        var rects = new List<System.Drawing.Rectangle>();
        var windows = await _windows.ListWindowsAsync(includeBounds: false, includeHandles: true);
        foreach (var w in windows)
        {
            if (w.Handle is null || PerceptionPolicy.IsDenied(w.ProcessName)) continue;
            try
            {
                var geo = await ResolveWindowCaptureGeometryAsync(new WindowHandle(w.Handle), null);
                if (!geo.Denied && !geo.Minimized) rects.AddRange(geo.MaskRects);
            }
            catch { } // a window that closed mid-enumeration, or one we cannot bind: skip it
        }
        return rects;
    }
```

and that `FindTextTools.cs:103-104` is exactly:

```csharp
                try { geo = await _perception.ResolveTextCaptureGeometryAsync(new WindowHandle(window), region); }
                catch (ToolException) { return false; } // window vanished/closed mid-wait -> not found; an UNEXPECTED exception propagates (surfaced by ToolResponse.Guard) so a real bug isn't hidden as a timeout
```

If either differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Return escalations from the full-desktop path, and stop swallowing the refusal**

Replace the whole `AllMaskRectsAsync` member you just verified — pre-Task-4 `:899-914`, ~`:1097-1112` after
Task 4's +198 — with:

```csharp
    public async Task<DesktopMaskSet> AllMaskRectsAsync()
    {
        var rects = new List<System.Drawing.Rectangle>();
        var escalations = new List<MaskEscalationEntry>();
        var windows = await _windows.ListWindowsAsync(includeBounds: false, includeHandles: true);
        foreach (var w in windows)
        {
            // ⚠ THE ORIGINAL REASON GIVEN FOR KEEPING THIS CHECK WAS FALSE, and the correction is recorded
            // because the false version was persuasive. It claimed that deleting it would ENUMERATE a
            // denylisted window's entire tree. MEASURED at PerceptionManager.cs:852 (line as of the branch point, before this task's own +198): the callee tests
            // PerceptionPolicy.IsDenied FIRST and returns immediately, well before PopupFinder.SearchRoots
            // at :861 — so no tree is ever walked for a denied window, with or without this line.
            //
            // What it actually saves is smaller and worth stating accurately: one
            // RunWithWindowAndDesktopAsync dispatch per denied window — binding the handle to a live
            // element on the query STA, plus SafeProcessName — for an answer already known here. Kept for
            // that, and because stating the denylist at the enumeration site keeps the policy visible where
            // windows are chosen rather than only where they are resolved. A reviewer who wants it gone has
            // a defensible case; the case must just not be the false one above.
            if (w.Handle is null || PerceptionPolicy.IsDenied(w.ProcessName)) continue;
            try
            {
                var geo = await ResolveWindowCaptureGeometryAsync(new WindowHandle(w.Handle), null,
                                                                  skipIfNoRenderableOverlap: true);
                // ⚠ KEPT DELIBERATELY. A review seat proposed deleting this condition because the callee
                // guarantees empty lists when Denied or Minimized, making the AddRange calls harmless
                // no-ops. True today — and it is precisely the "caller relies on the callee's internals"
                // coupling that produced this review's round-9 finding, one frame in the other direction.
                // If a future change ever returned a non-empty rect alongside Denied, deleting this would
                // turn that into a silent mask of a window we refused to inspect.
                if (!geo.Denied && !geo.Minimized)
                {
                    rects.AddRange(geo.MaskRects);
                    escalations.AddRange(geo.Escalations);
                }
            }
            // A1: a REFUSAL is not a window we failed to bind — it is a window we CAN see and CANNOT mask.
            // Swallowing it here would skip that window's mask set and photograph the whole desktop
            // anyway, making A1 inert on precisely the capture mode DEF-2 was about. Rethrown so the
            // full-desktop capture refuses as a window-scoped one does.
            catch (ToolException ex) when (ex.Code == ToolErrorCode.RedactionUnmaskable) { throw; }
            // ⚠ THE SKIP MUST NOT SWALLOW A CRITICAL FAILURE, and this is the edge round 6's own fix cut.
            // That round stopped the mask walk reclassifying OutOfMemoryException as RedactionUnmaskable —
            // correct — but the exception then propagates OUT of the walk and lands HERE, where a bare
            // `catch { }` swallowed it, skipped the window, and let the full-desktop capture proceed and
            // photograph it. Filtering the misclassification without filtering the swallow just moved the
            // leak one frame up the stack.
            catch (System.Exception ex) when (ex is not System.OutOfMemoryException
                                              and not System.OperationCanceledException)
            { } // a window that closed mid-enumeration, or one we cannot bind: skip it
        }
        return new DesktopMaskSet(rects, escalations);
    }
```

Also update the XML doc immediately ABOVE that member — pre-Task-4 `:891-898`, ~`:1089-1096` after Task 4's
+198 — by appending this paragraph before `</summary>`:

```
    /// ⚠ The skip is NOT unconditional: a RedactionUnmaskable refusal is rethrown. Skipping a window we
    /// cannot BIND is right; skipping one we can see and cannot MASK would photograph it in the clear.
```

- [ ] **Step 3: Add the return type**

Append to `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`, next to the other DTOs at the bottom of the
file (after `CaptureGeometry` at `:942`):

```csharp
/// <summary>The mask set for a FULL-DESKTOP capture: every visible non-denied window's rects, plus the
/// elements across all of them whose mask came from an ancestor. Two lists rather than a tuple so the
/// screenshot tool's metadata reads the same on both capture paths.</summary>
public sealed record DesktopMaskSet(IReadOnlyList<System.Drawing.Rectangle> Rects, IReadOnlyList<MaskEscalationEntry> Escalations);
```

- [ ] **Step 4: Stop the OCR path swallowing the refusal**

In `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs`, replace line 104:

```csharp
                // A1: a RedactionUnmaskable refusal must NOT degrade to "not found". This path captures the
                // same pixels the screenshot path refused to show and OCRs them, so swallowing the refusal
                // would read back in plaintext exactly what the mask was there to withhold. Every OTHER
                // ToolException still means the window vanished/closed mid-wait -> not found.
                catch (ToolException ex) when (ex.Code != ToolErrorCode.RedactionUnmaskable) { return false; }
```

Confirm `using FlaUI.Mcp.Core.Errors;` is present in that file (it references `ToolException` already, so
it must be); add it if not.

- [ ] **Step 5: Check the other OCR entry point**

Read `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:55-70` and `:88-95`. Confirm the call at `:59` and the
call at `:91` are NOT wrapped in a `catch (ToolException)`. If either is, apply the same
`when (ex.Code != ToolErrorCode.RedactionUnmaskable)` filter and say so in the commit message. If neither
is, they propagate through `ToolResponse.Guard` already and need no change.

- [ ] **Step 6: Build and run the headless gate**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`. This is the first build since Task 5, and it is the one that proves
Tasks 5 and 6 fit together.

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/FindTextTools.cs
git commit -m "feat(sp4): A1 - maskEscalations/escalated on the wire, and the refusal reaches every pixel path

desktop_screenshot metadata gains maskEscalations (always present, 0 when
none) and escalated (always present, empty when none). maskEscalations counts
ELEMENTS whose mask came from an ancestor, not levels climbed. escalated
carries automationId + controlType and never the Name - a bare count is not a
diagnostic, since an operator seeing a black box cannot tell which control has
the broken provider, while the Name is the identity the mask hides. Both are
already published for redacted elements by desktop_find, so this surfaces
nothing the query path does not.

The refusal now reaches both other pixel paths, which were swallowing it:

- full-desktop capture (AllMaskRectsAsync) skipped any window whose
  geometry threw. A window we cannot BIND is right to skip; one we can see and
  cannot MASK is not - swallowing there made A1 inert on exactly the capture
  mode DEF-2 was about.
- desktop_find_text degraded every geometry ToolException to satisfied:false.
  That path captures the same pixels and OCRs them, so suppressing the refusal
  would return in plaintext what the mask withheld.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: A6 — refactor the four `[Description]` attributes

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:19`
- Modify: `src/FlaUI.Mcp.Server/Tools/FindTools.cs:22`
- Modify: `src/FlaUI.Mcp.Server/Tools/WatchTools.cs:30`
- Modify: `src/FlaUI.Mcp.Server/Tools/ContentTools.cs:65`

⚠ **This task comes BEFORE the rule that needs it (Task 8).** Activating the rule first turns the build red
on these four attributes, and that red looks like a regression when it is only sequencing. A guard may only
be tightened after the thing it guards is already correct.

An attribute argument is EXECUTABLE assembly metadata, not prose. `ElementContent.RedactedToken` is a
`const string`, and `const + const` concatenation is a compile-time constant, which is exactly what an
attribute argument requires.

- [ ] **Step 1: STATE-VERIFY**

Confirm these four lines exist, verbatim:

- `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:19`:
  ```csharp
        "A redacted element shows \"[REDACTED]\" as its name; if an operator rule caused it the state also carries " +
  ```
- `src/FlaUI.Mcp.Server/Tools/FindTools.cs:22`:
  ```csharp
        "rule) return name=\"[REDACTED]\" and are NOT findable by name - not by their real name nor by the " +
  ```
- `src/FlaUI.Mcp.Server/Tools/WatchTools.cs:30`:
  ```csharp
        "(name is [REDACTED] for any redacted element - OS password field or operator rule; ref/name/bounds " +
  ```
- `src/FlaUI.Mcp.Server/Tools/ContentTools.cs:65` contains the substring:
  ```
  A redacted field returns text=\"[REDACTED]\" with redacted=true
  ```

If any differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Add the using to each file that lacks it**

Each of the four files needs `ElementContent` in scope. `SnapshotTools.cs`, `FindTools.cs` and
`ContentTools.cs` already have `using FlaUI.Mcp.Core.Perception;`. `WatchTools.cs` does NOT — add it after
`using FlaUI.Mcp.Core.Errors;` (line 3):

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Watch;
```

⚠ **Step 2 shifts Step 3's line number inside this same task** — adding the `using` to `WatchTools.cs`
pushes its line 30 to 31. The standing rule already covers it (every number is as of the branch point) and
Step 3 quotes the content, so anchor on the quoted line. Called out here because Task 6 is the only task
whose own earlier step moves its own later step, and because this defect class has bitten three times at
larger scale.

- [ ] **Step 3: Replace the four literals with const concatenations**

`src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:19` becomes:

```csharp
        "A redacted element shows \"" + ElementContent.RedactedToken + "\" as its name; if an operator rule caused it the state also carries " +
```

`src/FlaUI.Mcp.Server/Tools/FindTools.cs:22` becomes:

```csharp
        "rule) return name=\"" + ElementContent.RedactedToken + "\" and are NOT findable by name - not by their real name nor by the " +
```

`src/FlaUI.Mcp.Server/Tools/WatchTools.cs:30` becomes:

```csharp
        "(name is " + ElementContent.RedactedToken + " for any redacted element - OS password field or operator rule; ref/name/bounds " +
```

`src/FlaUI.Mcp.Server/Tools/ContentTools.cs:65` — this is one long single-line string. Split it at the token
only, leaving every other character identical:

```csharp
    [McpServerTool(ReadOnly = true), Description("Read an element's text via UIA TextPattern. selectionOnly=true reads the current selection (empty if none). maxLength caps output (default 10000, 1..200000); truncated=true if the text exceeded it, and truncatedFrom tells which end was dropped (\"tail\" for the default head-keeping read, \"head\" for a fromEnd read, null when not truncated). fromEnd=true returns the LAST maxLength chars (the latest output, e.g. a terminal's most-recent lines) instead of the first. NOTE: TextPattern returns roughly the visible viewport — text scrolled above it is not recoverable. A redacted field returns text=\"" + ElementContent.RedactedToken + "\" with redacted=true and redactedBy (\"os\", \"rule:<name>\", or \"unreadable\"). Read redacted, NOT isPassword: isPassword is the deprecated OS-only signal and stays false for rule-redacted fields. Off-screen targets ARE readable. PatternUnsupported if no TextPattern.")]
```

⚠ The description text an agent reads at runtime must be BYTE-IDENTICAL after this change. You are
replacing a literal with a constant of the same value, nothing else. If the emitted string would differ by
even one character, STOP and report it.

- [ ] **Step 4: Build**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`. A `CS0182` ("An attribute argument must be a constant expression")
means `RedactedToken` stopped being a `const` — STOP and report.

- [ ] **Step 5: Run the headless gate**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs src/FlaUI.Mcp.Server/Tools/FindTools.cs src/FlaUI.Mcp.Server/Tools/WatchTools.cs src/FlaUI.Mcp.Server/Tools/ContentTools.cs
git commit -m "refactor(sp4): A6 step 1 - the four [Description] attributes concatenate the token constant

An attribute argument is EXECUTABLE assembly metadata, not prose, so these
four are as much a copy of the token as the three egress sites are. Valid
because RedactedToken is a const string and const+const is a compile-time
constant.

Done BEFORE the rule that requires it: activating the rule first turns the
build red on these attributes, and that red reads as a regression when it is
only sequencing.

Emitted description strings are byte-identical - a constant replaced a literal
of the same value.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: A6 — the three executable literals

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:210`
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs:188`
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs:27`

Three independent literals that must stay byte-identical is exactly the coupling a constant exists to
remove.

- [ ] **Step 1: STATE-VERIFY**

Confirm these three lines, verbatim:

- `PerceptionManager.cs:210`:
  ```csharp
                string name = sens.Redact ? "[REDACTED]" : rawName;
  ```
- `SnapshotEngine.cs:188`:
  ```csharp
        string shownName = n.Sensitivity.Redact ? "[REDACTED]" : n.Name;
  ```
- `SnapshotDiff.cs:27`:
  ```csharp
    private static string ShownName(SnapshotNode n) => n.Sensitivity.Redact ? "[REDACTED]" : n.Name;
  ```

All three files are already in namespace `FlaUI.Mcp.Core.Perception`, so `ElementContent` needs no using.
If any line differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Replace all three**

```csharp
                string name = sens.Redact ? ElementContent.RedactedToken : rawName;
```

```csharp
        string shownName = n.Sensitivity.Redact ? ElementContent.RedactedToken : n.Name;
```

```csharp
    private static string ShownName(SnapshotNode n) => n.Sensitivity.Redact ? ElementContent.RedactedToken : n.Name;
```

Leave every `[REDACTED]` occurrence in a `//` or `///` comment ALONE. There are twelve, and they are
documentation: `PerceptionManager.cs:214,627,650,772`, `FindQuery.cs:21,63`, `WaitCoordinator.cs:78,91`,
`SnapshotEngine.cs:117,186`, `SnapshotDiff.cs:21`, `CheckRedactionRulesCommand.cs:213`. Roslyn treats them
as trivia, so the Task 8 rule never sees them.

- [ ] **Step 3: Run the headless gate**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged. The existing egress tests already compare against
`ElementContent.RedactedToken`, so a value change here would have been caught — which is precisely why this
is a coupling removal and not a leak fix.

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs
git commit -m "refactor(sp4): A6 step 2 - the three egress sites use ElementContent.RedactedToken

Three independent literals that must stay byte-identical is the coupling a
constant exists to remove. HONEST VALUE: the existing egress tests already
assert the constant, so a typo at any one site would already fail some test.
This removes a coupling; it does not close a leak.

Comment occurrences are left alone - they are documentation, and Roslyn treats
them as trivia, so the rule added next never sees them.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: A6 — the allowlist-independent sweep rule

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs`

⚠⚠ **The single most important property of this rule: it MUST NOT run through `AllowedMembers`.** The
existing walker suppresses findings on sanctioned members — `if (_allowed.ContainsKey(CurrentKey)) continue;`
at `:454` and `... return;` at `:567`. **Every site this rule targets is already an allowlisted member**:
`SnapshotEngine.FormatNode` (`:60`), `SnapshotDiff.ShownName` (`:64`),
`PerceptionManager.ResolveSelectorOnSta` (`:70`). A rule reusing that suppression would be silently DEAD ON
ARRIVAL on exactly the sites it exists to catch.

The two concerns are orthogonal and must stay separate: the allowlist exempts a member from the
CONTENT-READ rules because it is a sanctioned egress accessor; it says nothing about whether that member
may hard-code the token string. This rule therefore evaluates every `src/` node regardless of
`AllowedMembers`, with its own exemption set containing exactly one entry: `ElementContent`.

- [ ] **Step 1: STATE-VERIFY**

Confirm in `test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs`:

- `:151-153` is the `SweepResult` record with five rule lists.
- `:230-234` declares `Rule1` … `Rule4b` on `SweepVisitor`.
- `:236-241` is the `SweepVisitor` constructor.
- `:243-245` are `CurrentType`, `CurrentMemberName`, `CurrentKey`.
- `:454` is `if (_allowed.ContainsKey(CurrentKey)) continue;` and `:567` is
  `if (_allowed.ContainsKey(CurrentKey)) return;`.

If any differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Write the failing tests**

Add these three `[Fact]` methods INSIDE the `RedactionSurfaceInventoryTests` class, immediately after
`SourceTree_EnforcesRedactionSurfaceInvariants` (which ends at `:147`). They must live inside that class:
`SweepVisitor` and `AllowedMembers` are private members of it and are unreachable from any other type.

```csharp
    // ============================== RULE 5 pins (SP4/A6) ==============================

    /// Parse a snippet and run the REAL visitor with the REAL allowlist over it.
    ///
    /// ⚠ <paramref name="relPath"/> is NOT decoration. RULE 5's exemption is keyed on the type name AND its
    /// defining FILE, so a snippet declaring `ElementContent` at some other path is correctly FLAGGED. A
    /// caller checking the exemption must pass the real defining path; everything else uses the default.
    private static List<string> Rule5Over(string source, string relPath = "src/Fake.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: relPath);
        Assert.Empty(tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var visitor = new SweepVisitor(relPath, "Fake", AllowedMembers);
        visitor.Visit(tree.GetRoot());
        return visitor.Rule5;
    }

    /// <summary>⚠ THE ANTI-DEAD-ON-ARRIVAL PIN, and the reason this rule is written separately from every
    /// other rule in this file. SnapshotDiff.ShownName is IN AllowedMembers (:64) and it is one of the three
    /// sites A6 exists to catch. If RULE 5 were routed through the `_allowed.ContainsKey` suppression at
    /// :454 / :567 — the obvious way to write it, reusing the machinery already here — it would fire on
    /// nothing that matters and report GREEN forever.</summary>
    [Fact]
    public void Rule5_fires_inside_a_member_that_the_content_allowlist_exempts()
    {
        Assert.True(AllowedMembers.ContainsKey("SnapshotDiff.ShownName"),
            "this pin is only meaningful while SnapshotDiff.ShownName is allowlisted; it no longer is");

        var hits = Rule5Over(@"
namespace X;
public static class SnapshotDiff
{
    private static string ShownName(SnapshotNode n) => n.Sensitivity.Redact ? ""[REDACTED]"" : n.Name;
}");

        var hit = Assert.Single(hits);
        Assert.Contains("SnapshotDiff.ShownName", hit);
    }

    /// <summary>The rule is AST-aware, and that is what keeps it from breaking the build on documentation.
    /// A token inside a `//` or `///` comment is TRIVIA, never a LiteralExpressionSyntax, so it is never
    /// visited. Twelve such comments exist in src/ today.</summary>
    [Fact]
    public void Rule5_ignores_the_token_in_comments_and_doc_comments()
    {
        var hits = Rule5Over(@"
namespace X;
/// <summary>A password field's name is ""[REDACTED]"" on the wire.</summary>
public static class Documented
{
    // the snapshot renders [REDACTED] here
    private static string Safe() => ""nothing to see"";
}");

        Assert.Empty(hits);
    }

    /// <summary>The exemption set has exactly one entry: the type that DEFINES the token, IN ITS OWN FILE.
    /// Anywhere else in src/, a literal copy of it is the coupling this rule removes.
    ///
    /// ⚠ The third assertion is the anti-gaming half, and it is why the exemption is keyed on the file and
    /// not just the type name: `CurrentType` holds only the SHORT class name, so declaring a second
    /// `class ElementContent` anywhere in src/ and putting the literal inside it would otherwise satisfy
    /// the letter of the guard while leaving a hole in it.</summary>
    [Fact]
    public void Rule5_exempts_only_the_type_that_defines_the_token_in_its_own_file()
    {
        const string decl = @"
namespace X;
public static class ElementContent { public const string RedactedToken = ""[REDACTED]""; }";

        // The real definition, at its real path: exempt.
        Assert.Empty(Rule5Over(decl, "src/FlaUI.Mcp.Core/Perception/ElementContent.cs"));

        // Any other type: flagged.
        Assert.Single(Rule5Over(@"
namespace X;
public static class SomethingElse { public const string Copy = ""[REDACTED]""; }"));

        // ⚠ THE SAME TYPE NAME AT A DIFFERENT PATH: flagged. An impostor cannot borrow the exemption.
        Assert.Single(Rule5Over(decl, "src/FlaUI.Mcp.Server/Tools/Impostor.cs"));
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~RedactionSurfaceInventoryTests"`
Expected: FAIL to BUILD — `CS1061: 'RedactionSurfaceInventoryTests.SweepVisitor' does not contain a
definition for 'Rule5'`.

- [ ] **Step 4: Add RULE 5 to the sweep engine**

**4a.** Replace the `SweepResult` record at `:151-153`:

```csharp
    private sealed record SweepResult(
        string RepoRoot, int SrcFileCount, List<string> ParseDiagnostics,
        List<string> Rule1, List<string> Rule2, List<string> Rule3, List<string> Rule4, List<string> Rule4b,
        List<string> Rule5);
```

**4b.** In `RunSweep`, add the accumulator beside the others (after `var rule4b = new List<string>();` at
`:170`):

```csharp
        var rule5 = new List<string>();
```

collect it after `rule4b.AddRange(visitor.Rule4b);` at `:192`:

```csharp
            rule5.AddRange(visitor.Rule5);
```

and widen the return at `:195`:

```csharp
        return new SweepResult(repoRoot, files.Count, parseDiagnostics, rule1, rule2, rule3, rule4, rule4b, rule5);
```

**4c.** In `SourceTree_EnforcesRedactionSurfaceInvariants`, add one line after the RULE 4b line at `:142`:

```csharp
        Add("RULE 5 — bare redaction-token literal in src/ outside ElementContent", r.Rule5);
```

**4d.** Add the exemption constants to the `SweepVisitor` class, immediately after the `Rule4b` declaration
at `:234`:

```csharp
        public readonly List<string> Rule5 = new();

        // The token itself, taken from the ONE place that defines it rather than spelled again here — a
        // fourth independent copy inside the rule that forbids independent copies would be its own joke.
        // The sweep only scans src/, so this reference in test/ is not self-flagging.
        private static readonly string RedactionToken = FlaUI.Mcp.Core.Perception.ElementContent.RedactedToken;
        private const string TokenDefiningType = "ElementContent";
        private const string TokenDefiningFile = "src/FlaUI.Mcp.Core/Perception/ElementContent.cs";
```

**4e.** Add the visitor override. Put it immediately after `VisitMemberAccessExpression` (which ends at
`:323`):

```csharp
        // ---- Rule 5 (SP4/A6): the redaction token may not appear as a bare string literal anywhere in
        // src/ outside the type that DEFINES it.
        //
        // ⚠⚠ DELIBERATELY INDEPENDENT OF `_allowed`. The allowlist exempts a member from the CONTENT-READ
        // rules because it is a sanctioned egress accessor; that says nothing about whether the member may
        // hard-code the token string. The two concerns are orthogonal, and conflating them would be fatal
        // rather than untidy: SnapshotEngine.FormatNode (:60), SnapshotDiff.ShownName (:64) and
        // PerceptionManager.ResolveSelectorOnSta (:70) are ALL allowlisted, and they are ALL the sites this
        // rule exists to catch. Routing it through the `_allowed.ContainsKey` suppression at :454 / :567
        // would leave it firing on nothing and reporting GREEN forever.
        // Rule5_fires_inside_a_member_that_the_content_allowlist_exempts pins exactly that.
        //
        // AST-awareness is free here rather than new machinery: a token in a `//` or `///` comment is
        // TRIVIA and never reaches a LiteralExpressionSyntax, so documentation is untouched.
        //
        // HONEST LIMITS, so this is never mistaken for a guarantee:
        //  · BYPASSABLE BY CONSTRUCTION — writing the token as a concatenation, or as a second constant,
        //    defeats it. It catches ACCIDENT, not intent.
        //  · NOT A LEAK GUARD — the existing egress tests already compare against the constant, so a typo
        //    at any single site already fails a test. This removes a coupling.
        //  · An interpolated string's text is InterpolatedStringTextSyntax, not a literal, so a token
        //    embedded in a $"..." is not seen. No such site exists in src/ today.
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            // The exemption is keyed on the type name AND its file. CurrentType holds only the SHORT
            // class name, so a type-name-only exemption is defeated by declaring a second
            // `class ElementContent` anywhere in src/ and putting the literal inside it — which satisfies
            // the letter of the guard while leaving a hole in it. Requiring the defining file closes that
            // without a semantic model.
            //
            // ⚠ The PATH half compares case-INSENSITIVELY on purpose. `relPath` comes from
            // Path.GetRelativePath, which preserves the casing ON DISK, so an ordinal compare would fail —
            // and fail the BUILD, by flagging ElementContent's own constant — on a clone whose folder is
            // `Src`. Windows paths are case-insensitive, so nothing is lost: an impostor still needs a
            // different PATH, not merely different casing, and the TYPE-name half stays ordinal.
            bool inTokenDefiningType =
                string.Equals(CurrentType, TokenDefiningType, StringComparison.Ordinal)
                && _relPath.EndsWith(TokenDefiningFile, StringComparison.OrdinalIgnoreCase);

            if (node.IsKind(SyntaxKind.StringLiteralExpression)
                && !inTokenDefiningType
                && node.Token.ValueText.Contains(RedactionToken, StringComparison.Ordinal))
            {
                int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Rule5.Add($"{_relPath}:{line} member={CurrentKey} — string literal contains the redaction " +
                          "token; use ElementContent.RedactedToken (concatenate it if the literal is a " +
                          "longer sentence — const + const is a compile-time constant, so this works in an " +
                          "attribute argument too)");
            }
            base.VisitLiteralExpression(node);
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~RedactionSurfaceInventoryTests"`
Expected: `Passed!  - Failed: 0`. `SourceTree_EnforcesRedactionSurfaceInvariants` must be green — Tasks 7
and 8 removed all seven `src/` literals, which is why this rule can be switched on now and could not have
been switched on earlier.

If it is RED and names the four `[Description]` attributes, Task 6 was not applied. If it names the three
egress sites, Task 7 was not applied. Neither is a reason to weaken the rule.

- [ ] **Step 6: Prove the anti-DOA pin is non-vacuous (temporary LOGIC MUTANT)**

Route RULE 5 through the allowlist suppression — add this as the FIRST line of `VisitLiteralExpression`:

```csharp
            if (_allowed.ContainsKey(CurrentKey)) { base.VisitLiteralExpression(node); return; }
```

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~RedactionSurfaceInventoryTests"`
Expected: FAIL, and `Rule5_fires_inside_a_member_that_the_content_allowlist_exempts` must be the failing
test.

**REVERT THE MUTANT** and re-run to confirm green.

- [ ] **Step 7: Prove the rule fires on real source (temporary LOGIC MUTANT)**

Revert nothing from the rule; instead mutate the SOURCE. In `src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs:27`
put the bare literal back:

```csharp
    private static string ShownName(SnapshotNode n) => n.Sensitivity.Redact ? "[REDACTED]" : n.Name;
```

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~SourceTree_EnforcesRedactionSurfaceInvariants"`
Expected: FAIL, with the message naming `RULE 5` and `src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs:27`.

**REVERT THE MUTANT** and re-run to confirm green.

- [ ] **Step 8: Run the headless gate and commit**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total up by 3.

```bash
git add test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs
git commit -m "test(sp4): A6 step 3 - RULE 5, and it must not run through the allowlist

No bare redaction-token literal may appear in src/ outside ElementContent,
which defines it.

The rule is DELIBERATELY independent of AllowedMembers, and that is the whole
finding. The existing walker suppresses on allowlist membership (:454, :567),
and all three sites A6 targets - SnapshotEngine.FormatNode, SnapshotDiff.
ShownName, PerceptionManager.ResolveSelectorOnSta - are ALREADY allowlisted
members. A rule reusing that machinery would have been dead on arrival on
exactly the sites it exists to catch, and would have reported GREEN forever.
Rule5_fires_inside_a_member_that_the_content_allowlist_exempts pins that
specifically, and is proven by mutant: adding the ContainsKey suppression
turns it red.

AST-awareness comes free from Roslyn - a token in a comment is trivia and
never reaches a literal node, so the twelve documentation occurrences in src/
are untouched.

Stated in the rule's own comment so it is never oversold: bypassable by
concatenation, catches accident not intent, and is a coupling removal rather
than a leak guard.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: A5 — rewrite the sweep allowlist entry

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs:85`

⚠ **This entry is REWRITTEN, never DELETED.** An earlier draft of the spec said delete it, "because after
the fix the member performs no UIA content read". That became false the moment the window-root fallback was
added in Task 1: that fallback IS a UIA content read, on this member. Deleting the entry at any point in
this sequence turns the build red.

That sentence was correct when written and became wrong two folds later, in the same document, without
anyone editing it — which is why the standing rule is: **before tightening a guard, re-read what the guarded
code does AFTER all of this increment's other changes, not as it was when the guard change was drafted.**

- [ ] **Step 1: STATE-VERIFY the code the entry describes**

Open `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` and confirm that after Task 1,
`ResolveFocusedWindowAsync` reads `root.Properties.Name` (the window root) and does NOT read
`focused.Properties.Name` (the focused element). The new reason is true only because of that distinction.

If the member still reads `focused.Properties.Name`, Task 1 was not applied — STOP and report.

- [ ] **Step 2: Replace the entry**

Replace `test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs:85`, which currently reads:

```csharp
        ["WindowManager.ResolveFocusedWindowAsync"] = "window TITLE via Properties.Name, not element content - the census's canonical 'neither'. NOTE: filed as an open anomaly (.clavity/local-anomalies.md) because a title can carry a document/customer name and no rule can target it today",
```

with:

```csharp
        // ⚠⚠ THIS REASON HAS THE SAME SHAPE AS THE FALSE ONE IT REPLACES, AND THAT IS A HAZARD, NOT A
        // COINCIDENCE. The removed text read "window TITLE via Properties.Name, not element content" and was
        // FALSE, because the element being read was the FOCUSED ELEMENT. The text below is TRUE, because the
        // element being read is the WINDOW ROOT. The distinction IS the entire justification. A future
        // reviewer must check WHICH ELEMENT this member reads before honouring the exemption — not merely
        // that a plausible sentence is present. Two reasons in this file have already turned out false, and
        // one of them silenced the sweep on the exact member that was leaking.
        ["WindowManager.ResolveFocusedWindowAsync"] = "IDENTITY reader: reads the WINDOW ROOT's Name, which IS the window's title, and only as a fallback when the Win32 caption is empty (a framework drawing its own title bar). NOT the focused element's Name - that was the false version of this reason, and the leak it hid is fixed in SP4/A5",
```

- [ ] **Step 3: Run the headless gate**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged. This is a comment-and-string change; a RED here means the
entry key was altered, which un-exempts the member.

- [ ] **Step 4: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs
git commit -m "test(sp4): A5 - rewrite the ResolveFocusedWindowAsync allowlist reason

The old reason - 'window TITLE via Properties.Name, not element content' - was
FALSE: the element being read was the FOCUSED ELEMENT, so it was element
content, and the sweep was silenced on a real leak by a sentence that sounded
right. This is the second false reason found in this file; unlike the 26
pasted ones, this one was individually reasoned and still wrong.

REWRITTEN, not deleted. The A5 fix keeps a UIA read on this member - the
window ROOT's Name, as the fallback when the Win32 caption is empty - so
deleting the entry would turn the build red.

The new reason has the same SHAPE as the false one, and the entry now says so
out loud: the distinction is WHICH element is read, and a future reviewer must
check that rather than accept a plausible sentence.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Desktop pins for A1 and A5

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs`

⚠ **This task needs a physical console and an active input lease.** The Desktop suite does not run over RDP
(`SendInput` is silently no-opped) and takes ~12 minutes. Run it once, here.

- [ ] **Step 1: STATE-VERIFY the fixture facts these tests depend on**

Confirm all four:

- `test/FlaUI.Mcp.TestApp/MainWindow.xaml:4` is `Title="FlaUI.Mcp TestApp"`.
- `test/FlaUI.Mcp.TestApp/MainWindow.xaml:34-35` declares a Button with
  `Content="OK"` and `AutomationProperties.AutomationId="OkButton"`.
- `test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs:25-26` is
  `private static PerceptionManager Perception(WindowManager mgr) => new(mgr, new RefRegistry(), new SnapshotCache());`
- The file's closing brace is at `:74`.

If any differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 2: Write the three Desktop facts**

Insert all three methods into `RedactionOracleTests` immediately before the closing brace at `:74`. The
third is the `WindowTitle` assertion moved here from Task 1, because a headless test constructing a
`WindowManager` would be the first in this repo and the headless filter is what CI runs.

```csharp

    /// <summary>SP4/A5. `window.title` is now the OWNING WINDOW's caption via Win32 GetWindowText, with the
    /// window ROOT's UIA Name as a fallback. This pins the empty-caption path's FIRST step: WindowTitle
    /// must ANSWER rather than throw when the caption cannot be read, because the fallback below it runs
    /// only if this returns empty.
    ///
    /// ⚠ HONEST LIMIT, recorded rather than implied: this exercises neither the UIA fallback nor its catch.
    /// Both need a live window with an empty caption whose provider throws on demand, which no fixture can
    /// stage — the AB-1 limitation. Ledgered as AB-6 in docs/coverage-debt.md.</summary>
    [Fact]
    public async Task An_unreadable_window_caption_answers_null_rather_than_throwing()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        await mgr.OpenByPidAsync(_app.Process.Id); // bind the fixture, as the sibling facts do

        Assert.Null(mgr.WindowTitle(System.IntPtr.Zero));
    }

```csharp

    /// <summary>SP4/A5 — the load-bearing half of this fact is the NotEqual. `window.title` used to be the
    /// focused ELEMENT's Name, so under the pre-fix code focusing OkButton made title == "OK". It is now
    /// the owning window's caption.
    ///
    /// The literals mirror FlaUI.Mcp.TestApp.MainWindow: Title="FlaUI.Mcp TestApp" (MainWindow.xaml:4) and
    /// the OK button (MainWindow.xaml:34-35). TestApp is an exe launched by the fixture, not referenced as
    /// a library, so they are duplicated here intentionally - the same convention PasswordRedactionTests
    /// uses for the secret.
    ///
    /// The third assertion is the other half of the design: the element's name did not become
    /// unreachable, it moved to `descriptor`, which is rendered through SnapshotEngine.Render and is
    /// therefore CLASSIFIED. Removing the raw Name removed a leak, not a capability.</summary>
    [Fact]
    public async Task Focused_element_reports_the_WINDOW_title_not_the_element_name()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.AsWindow().Focus();
            win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!.Focus();
            return true;
        });

        var focused = await Perception(mgr).GetFocusedElementAsync();

        Assert.Equal("FlaUI.Mcp TestApp", focused.Title);
        Assert.NotEqual("OK", focused.Title);          // the pre-fix value
        Assert.Contains("OK", focused.DescriptorLine); // still reachable, through the classified path
    }

    /// <summary>SP4/A1 — the no-escalation case is the one that runs on every ordinary capture, so it is
    /// the one worth pinning on a real desktop: a healthy WPF window reports its bounds, nothing escalates,
    /// and both diagnostics are PRESENT and empty rather than absent. A field that appears only on failure
    /// teaches a consumer to ignore its absence.</summary>
    [Fact]
    public async Task An_ordinary_window_capture_reports_no_mask_escalations()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var result = await new ScreenshotTools(Perception(mgr)).DesktopScreenshot(window: handle.Id);

        Assert.False(result.IsError);
        var meta = result.Content.OfType<TextContentBlock>().Single().Text;
        using var doc = System.Text.Json.JsonDocument.Parse(meta);
        Assert.Equal(0, doc.RootElement.GetProperty("maskEscalations").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("escalated").GetArrayLength());
    }
```

Confirm `using FlaUI.Core.AutomationElements;` is present at the top of the file for `AsWindow()` /
`FindFirstDescendant`; add it if not.

- [ ] **Step 3: Run only the new facts first**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~RedactionOracleTests"`
Expected: `Passed!  - Failed: 0, Passed: 5`.

⚠ If `Focused_element_reports_the_WINDOW_title_not_the_element_name` fails with an empty or unexpected
title, check `qwinsta` for a physical console session before suspecting the code — `SetForegroundWindow` is
denied to a process with no recent user input, and the fixture window may not have come forward.

- [ ] **Step 4: Prove both pins are non-vacuous (temporary LOGIC MUTANTS)**

| Mutant | Edit | Test that must go red |
|---|---|---|
| Revert A5 | in `WindowManager.ResolveFocusedWindowAsync`, replace the `WindowTitle(hwnd) ?? ""` line with `string title = ""; try { title = focused.Properties.Name.ValueOrDefault ?? ""; } catch { }` and delete the fallback block | `Focused_element_reports_the_WINDOW_title_not_the_element_name` |
| Report a phantom escalation | in `ScreenshotTools`, change `maskEscalations = escalations.Count` to `maskEscalations = escalations.Count + 1` | `An_ordinary_window_capture_reports_no_mask_escalations` |

Run each as: `dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~RedactionOracleTests"`

⚠ **The second mutant is deliberately the boring one.** The obvious mutant — making
`MaskEscalation.Resolve` treat every own-rect as unusable — goes red for EITHER of two reasons depending on
whether the fixture's password field happens to be a direct child of the window root (escalate-and-succeed
vs escalate-to-root-and-refuse). A mutant that can go red two ways proves something broke, not that this
assertion is load-bearing. Incrementing the reported counter can only fail one way.

**REVERT BOTH MUTANTS** and re-run to confirm green.

- [ ] **Step 5: Run the full Desktop gate**

Both halves, on a physical console, with an active lease. `0 skipped` on both is part of the gate — a
skipped Desktop test is an unrun one.

```bash
dotnet test FlaUI.Mcp.slnx -c Release --filter "Category=Desktop&Category!=KnownDefect&Category!=Measurement&FullyQualifiedName!~PopupGrafting"
dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~PopupGrafting"
```

Expected: first `Passed!  - Failed: 0, Passed: 156, Skipped: 0` (153 at the branch point plus the 3 added
here); second `Passed!  - Failed: 0, Passed: 1, Skipped: 0`.

- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs
git commit -m "test(sp4): Desktop facts for A5's re-meaning and A1's default path

A5: focus OkButton, whose Name is 'OK', and assert window.title is the
WINDOW's caption and NOT 'OK'. The NotEqual is the load-bearing half - it is
exactly the pre-fix value. The third assertion pins the other half of the
design: the element's name is still reachable through descriptor, which is
rendered classified, so removing the raw Name removed a leak and not a
capability.

A1: an ordinary healthy window reports maskEscalations 0 and an EMPTY
escalated array - present, not absent, because a diagnostic that only appears
on failure teaches consumers to ignore its absence.

Also moved here from the source task: the WindowTitle assertion. It was
drafted as a HEADLESS fact, and measurement said no headless test in this repo
constructs a WindowManager - the headless filter is what CI runs, so that draft
risked a red CI on master to save a Desktop slot.

Both behaviour pins proven non-vacuous by mutant: reverting the title read, and
reporting a phantom escalation. The obvious escalation mutant was rejected as
non-deterministic - it can go red two ways depending on the fixture's tree
shape.

Desktop gate green on a physical console: 156/0/0 plus PopupGrafting 1/0/0,
0 skipped on both halves.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 11: dispositions, documentation, and the ledgers

**Files:**
- Modify: `docs/coverage-debt.md`
- Modify: `docs/agent-contract.md:58,59,61,77,78`
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md:328,332,333,334`
- Modify: `.clavity/local-anomalies.md`

A3 and A4 are disposition-only (no code). This task also discharges the spec's §3 obligation to re-validate
AB-3, documents A1's flat-tree refusal, and closes the triage loop on the six captured anomalies.

- [ ] **Step 1: Re-validate accepted boundary AB-3**

Spec §3 requires this, because A1 introduces the very seam pattern AB-3 uses as its excuse ("faking
`AutomationElement` is brittle by construction"), and `docs/coverage-debt.md`'s own rule says a changed
compensation reopens an entry.

Run: `grep -rn "A_pid_of_zero_is_unattributable" test/FlaUI.Mcp.Tests/ --include=*.cs`
Expected: exactly one hit, `test/FlaUI.Mcp.Tests/Perception/CapstoneFixTests.cs:263`.

- If the anchor is present, the compensation still exists: the entry stands. Append the re-validation stamp
  in Step 2.
- If it is absent, the compensation has vanished: promote AB-3 back to a live gap under **Tracked debt** and
  say so in the commit.

⚠ Do NOT extend A1's seam to `ProcessIdentity.OfElement` in this increment. That is the `ProcessIdentity`
strong-type follow-up, which the spec puts explicitly OUT of scope (§2) as additive and not deadline-bound.

- [ ] **Step 2: Update the coverage-debt ledger**

Replace the `_None._` line under `## Tracked debt (deferred gaps)` (`docs/coverage-debt.md:21`) with:

```markdown
### A3 — the DEF-3 SELECTOR path has no specific fact
**Gap:** DEF-3 (a redacted element is withheld from NAME search, so a selector cannot be used as a
password-field locator oracle) is pinned on the find and wait paths. The SELECTOR path rests on 21
regression tests that would each fail for other reasons first, so no single fact says "the selector honours
DEF-3".
**Why deferred:** the direct pin needs a visible fixture element literally named `[REDACTED]`, which risks
the layout-coupled fixture guards `MainWindow.xaml` warns about (D7 / offscreen-cull / column-height
invariants). Its sibling half — an element GENUINELY named the token must still be matchable — is already
ledgered as AB-2 for the same reason.
**Owner:** post-v1.0. Captured during SP3, dispositioned in SP4 (spec §7).
```

Append to the accepted-boundary ledger, after AB-5:

```markdown
### AB-6 — the window-title UIA fallback and its catch  *(SP4/A5)*
**Behaviour:** in `WindowManager.ResolveFocusedWindowAsync`, reading the window ROOT's `Name` when the
Win32 caption is empty, and degrading to an empty title when that read THROWS.
**Why not covered:** needs a live window whose caption is empty AND whose provider throws on demand — the
same limitation as AB-1, plus a selection effect that makes even the empty-caption half hard to sample:
`EnumTopLevel` skips captionless windows (`WindowManager.cs:394`), so `desktop_list_windows` can never show
one, and their absence there is not evidence they do not exist.
**Compensation + anchor:** the Win32 half is pinned headlessly —
`WindowTitleTests.An_unreadable_caption_answers_null_rather_than_throwing` asserts `WindowTitle` ANSWERS
rather than throwing, which is the precondition for the fallback ever running; and the primary path is
pinned on the desktop by
`RedactionOracleTests.Focused_element_reports_the_WINDOW_title_not_the_element_name`.
⚠ **Honest limit:** if the fallback block or its `catch` were deleted, no test goes red on a machine whose
windows all have captions.

### AB-9 — a window UIA cannot BIND is skipped, not refused  *(SP4/A1, pre-existing)*
**Behaviour:** on the full-desktop path, `AllMaskRectsAsync` catches a bind failure and skips that
window; the capture then proceeds and photographs it.
**Why it matters:** an ELEVATED window (an admin terminal, Task Manager) cannot be bound by a
non-elevated UIA client at all, so `ResolveWindowCaptureGeometryAsync` throws BEFORE reaching the mask
walk — outside the blanket conversion, which lives inside the STA lambda. Failing to prove a window is safe
becomes, silently, a guarantee that its pixels are captured.
**Why not fixed here:** the fix is a policy decision with real cost, not a code detail. Refusing every
full-desktop capture while any unbindable window is visible would break the common case (an admin terminal
open on a developer's desktop) to protect a window SP3 never protected either — UIA cannot read its
contents, so no redaction has ever applied to it. **Surfaced to the operator in the OPEN section rather
than decided here.**
**Compensation + anchor:** partial and honest — `ScreenshotTools.cs:33-35` already refuses full-desktop
capture outright when a DENYLISTED credential window is visible, which covers the highest-value case
(`PerceptionPolicy.IsDenied`). Everything else is skipped.
⚠ **Honest limit:** this predates SP4 and A1 does not close it. It is ledgered so it stops being invisible.

### AB-8 — escalation assumes an ancestor encloses its descendants  *(SP4/A1)*
**Behaviour:** when an element's own bounds are unreadable, the mask taken from its ancestor is assumed to
cover the pixels the element was painting.
**Why not covered, and why not FIXED:** a child can paint outside its parent's bounds via a negative
margin, absolute/canvas positioning, or a render transform. The check cannot be written, in tests or in
production, because escalation runs precisely when the element's own extent is the unavailable quantity.
**Compensation + anchor:** none, and that is the entry's point. The limit is stated in
`MaskEscalation`'s own type doc so it is read by anyone touching the mechanism, and the diagnostic path
(`maskEscalations` + `escalated`) tells an operator which control escalated, so a leak of this shape is at
least attributable after the fact.
⚠ **Honest limit:** an escalated mask is best-effort. A1 closes the fail-OPEN hole where an unreadable rect
contributed NO mask at all; it does not promise the substituted mask is pixel-complete.

### AB-7 — `AncestorRectSource`'s root bound  *(SP4/A1)*
**Behaviour:** the ancestor climb stops AT its search root and never climbs past it. Reaching the WINDOW
root yields no rect, which becomes a refusal; reaching a POPUP root offers that popup's rect, which
`MaskEscalation` then accepts or rejects geometrically. (This entry described a blanket refusal at every
root until the popup fork and the geometric rule replaced it — the behaviour changed three times during
review, so read the code, not this sentence, if they ever disagree.)
**Why not covered:** needs a live element that fails to report bounds while its ancestors succeed. No
fixture can stage one — the AB-1 limitation.
**Compensation + anchor:** the decision's half of the contract is pinned headlessly by
`MaskEscalationTests.A_source_that_reports_the_root_as_usable_would_mask_instead_of_refusing`, which
asserts that a source still answering at the root turns a refusal into a mask — i.e. it states exactly what
the source owes the decision.
**Second uncovered half:** whether the root is treated as REFUSE (window) or as a usable mask (popup) is
an adapter decision too. The decision function only ever sees "a rect" or "no rect", so both branches look
identical from the headless side.
⚠ **Honest limit, and it is the sharp one:** if the root check were deleted from `AncestorRectSource`, no
test goes red. **This is not hypothetical — it is what happened.** The plan's first draft had no root bound
at all, every headless fact passed, and the guarantee was dead: `GetParent` succeeds at the window root and
the root has valid bounds, so escalation would have returned a successful all-black screenshot. It was
caught by an adversarial panel, not by the suite. Treat this entry as a standing warning about what a
faked ancestor source can hide.
```

Add the AB-3 re-validation stamp by appending one line to that entry:

```markdown
**Re-validated 2026-08-19 (SP4):** anchor confirmed present at `CapstoneFixTests.cs:263`. Entry stands.
A1's injectable-seam pattern was NOT extended to `ProcessIdentity.OfElement` — that is the out-of-scope
`ProcessIdentity` strong-type follow-up (SP4 spec §2), not this increment.
```

- [ ] **Step 3: Update `docs/agent-contract.md`**

⚠ **STATE-VERIFY EVERY ROW BY ITS FULL OLD TEXT, NOT BY ITS LINE NUMBER OR ITS FIRST CELL.** This file has
**two** rows whose first cell is `` `redacted` ``: line 74 documents the per-element field on snapshot
nodes / `desktop_get_text` / `desktop_get_grid_cell`, and line 78 documents the `desktop_snapshot_stats`
counter. Only line 78 changes. Rewriting line 74 would break the documentation of a field this increment
does not touch — and the two rows read almost identically at a glance.

Replace line 58, which currently reads:

```markdown
| `desktop_snapshot_stats` | ReadOnly | Control counts. **Params:** `snapshotId` (offline stats) OR `window`. |
```

with:

```markdown
| `desktop_snapshot_stats` | ReadOnly | Control counts. **Params:** `snapshotId` (offline stats) OR `window`. |
```

with:

```markdown
| `desktop_snapshot_stats` | ReadOnly | Control counts. **Params:** `snapshotId` (offline stats) OR `window`. ⚠ The `redacted` field is now `osPasswordCount` (v1.0.0 BREAKING). |
```

Replace line 59, which currently reads:

```markdown
| `desktop_get_focused_element` | ReadOnly | Return UIA-focused element's ref and descriptor. |
```

with:

```markdown
| `desktop_get_focused_element` | ReadOnly | Return UIA-focused element's ref and descriptor. ⚠ `window.title` is the OWNING WINDOW's title. Before v1.0.0 it carried the focused ELEMENT's name — a silent BREAKING change, see the CHANGELOG. The element's name is in `descriptor`. |
```

Replace line 61, which currently reads:

```markdown
| `desktop_screenshot` | ReadOnly | PNG capture. Masks redacted elements (see **Redaction on the wire**). **Params:** `window`, `ref`, `maxWidth` (default 1600). |
```

with:

```markdown
| `desktop_screenshot` | ReadOnly | PNG capture. Masks redacted elements (see **Redaction on the wire**). **Params:** `window`, `ref`, `maxWidth` (default 1600). Metadata carries `maskEscalations` + `escalated`; can fail `RedactionUnmaskable`. |
```

Replace line 78. Before editing, confirm the line you are about to replace ENDS with
"Not the same number as `redactedCount`." - that clause is what distinguishes it from the OTHER `redacted`
row at line 74. It currently reads:

```markdown
| `redacted` | `desktop_snapshot_stats` | ⚠ Counts **OS password nodes only**, unchanged for back-compat. Not the same number as `redactedCount`. |
```

with:

```markdown
| `osPasswordCount` | `desktop_snapshot_stats` | Count of OS password nodes **only**. A different number from `redactedCount`. Called `redacted` before v1.0.0. |
```

Append after line 80 (the "Withheld content reads `[REDACTED]`" paragraph):

```markdown
**When a screenshot cannot mask.** If a redacted element cannot report usable bounds, its mask is taken
from an ancestor: `maskEscalations` counts those **elements** (not levels climbed) and `escalated` lists
their `{automationId, controlType}` so you can tell which control has a broken provider — never their name.
If no ancestor is usable either, the capture is **refused** with `RedactionUnmaskable` rather than returning
an all-black image you would be tempted to interpret.

⚠ **This lands differently on deep and flat trees, and the difference is expected.** On WPF / Electron /
modern XAML a redacted control usually sits many levels down, so one unreadable rect degrades gracefully —
a local container is masked and you still get a usable screenshot. On classic Win32 dialogs, where controls
are direct children of the window, the parent IS the root, so one unreadable rect refuses the whole capture
on the first escalation step. If screenshots of one particular older app always fail this way, that is the
mechanism, not a bug — capture a different window, or use `desktop_snapshot` for that one.
```

- [ ] **Step 4: Add the CHANGELOG entry**

Insert immediately after line 4 (before `## [0.20.0] - 2026-07-29`):

```markdown
## [Unreleased]

### BREAKING

- **`desktop_snapshot_stats.redacted` is now `osPasswordCount`.** It always counted OS-flagged password
  nodes only, never the rule-redacted ones — `redactedCount` is the total and is unchanged. Two same-rooted
  names for two different quantities mislead by default, and this was the last moment to fix it for free.
  This break is LOUD: a consumer reading `redacted` gets a missing field immediately. No alias is kept,
  because an alias would preserve exactly the ambiguity being removed.

- **`desktop_get_focused_element`'s `window.title` changed MEANING, and this break is SILENT.** It used to
  carry the focused ELEMENT's name; it now carries the owning WINDOW's title, which is what the field name
  always claimed. Unlike the rename above, this sails through deserialization and simply returns different
  data — an agent waiting on `window.title == "Submit"` does not fail, it waits forever. The field name was
  kept deliberately: `title` is the correct name for a window title, and renaming it would surrender the
  right name permanently just to manufacture a compile-time break. **If you have a script keyed on the old
  value, change it now.** The element's name is still available, through `descriptor` (rendered classified)
  and through `ref` + `desktop_get_text`.

### Security

- **The screenshot mask no longer fails open.** A redacted element whose `BoundingRectangle` could not be
  read contributed no mask rect and its pixels were captured — the same fail-open shape as DEF-1, on the
  other half of the same line, and it survived the SP3 fix. The mask now escalates to an ancestor, and
  REFUSES the capture (`RedactionUnmaskable`) when no usable ancestor remains. Refusing beats returning a
  successful all-black image, which an agent would hallucinate contents for or loop on.
  The refusal reaches all three pixel paths, including full-desktop capture and the `desktop_find_text` OCR
  path, both of which previously swallowed it.

- **`desktop_get_focused_element` no longer publishes element content as a window title.** See BREAKING
  above; the leak and the contract fix are the same change.

- **⚠ RETROSPECTIVE DISCLOSURE — DEF-3 was reachable in the DEFAULT configuration of every prior release.**
  Before SP3, a `desktop_find` name query containing the redaction token matched redacted elements, so one
  `contains` query returned the full set of password-field refs and bounds. This needed **no** operator
  redaction rules: the OS `IsPassword` flag alone was enough, which is the shipping default. Every install
  before SP3 was affected, not only adopters of the SP3 rule feature. Fixed in SP3; disclosed here because
  the original note understated the population as rule-adopters only.
```

- [ ] **Step 5: Update the ROADMAP**

Replace line 333:

```markdown
| 9 | per-field redaction | SP3 | ✅ shipped — merged `ef17ed9` (`--no-ff`) |
```

⚠ **Do NOT touch lines 328, 332 or 334.** They are items 4, 8 and 10 - snapshot/diff value-change
detection, `PrintWindow`, and `WM_RENDERFORMAT` - none of which this increment touches. An earlier draft of
this step read "replace lines 328, 332 and 334", which was simply wrong and would have rewritten three
unrelated roadmap items.

APPEND one new row after line 334:

```markdown
| 11 | redaction completeness (A1 pixel-mask fail-open · A5 focused-window title · A2 stats rename · A6 token constant) | SP4 | 🔄 in progress — spec `docs/superpowers/specs/2026-08-18-sp4-redaction-completeness-design.md`, plan `docs/superpowers/plans/2026-08-19-sp4-redaction-completeness.md` |
```

⚠ Do NOT renumber items 1-10. The numbering is cited by name across several specs, and the file's own
header records that reconstructing it once already cost an audit.

⚠ The table is introduced by a sentence reading "**the 2 filed defects + ALL 8 opportunistic items = 10**".
An 11th row contradicts it, so amend that sentence in the SAME edit - replace `= 10` with
`= 10, plus item 11 added by SP4` - rather than leaving the prose and the table disagreeing.

Append to the H1 entry (after line 509, the "resolve `processName` per HWND boundary" line):

```markdown
  **Unchanged by SP4 (spec §2, explicitly out of scope).** A5 makes the window-title surface consistent with
  `desktop_list_windows`, which is what allows "should window titles be redactable" (`titlePattern`) to be
  asked once, coherently, later — but neither touches the homogeneity assumption.
```

- [ ] **Step 6: Close the triage loop on the captured anomalies**

All six entries in `.clavity/local-anomalies.md` are now PROMOTED — A1, A2, A5, A6 into this plan's tasks,
A3 into `docs/coverage-debt.md` (Step 2), A4 into the CHANGELOG (Step 4). The `open-issues` skill allows
exactly two outcomes, PROMOTE-then-delete or DELETE-with-a-reason, and there is no parked state, so the
lines are removed now that their tracking homes exist.

⚠ **Do NOT truncate the file with `>`.** The `open-issues` skill that owns this file states, with a
measurement behind it, that its header is written with `>>` and NEVER `>`, precisely because two sessions
open on the same repository will destroy each other's entries. Delete the six entry LINES and leave
everything else alone:

⚠ **COUNT BEFORE YOU DELETE.** An earlier draft ran `grep -v '^- \['` and then told the executor to leave
any surviving entry alone — which is incoherent, because that command strips EVERY entry line, so nothing
can survive to be noticed. It would have silently destroyed anything a concurrent session captured while
reporting success.

```bash
R=$(git rev-parse --show-toplevel)
F="$R/.clavity/local-anomalies.md"
grep -c '^- \[' "$F"
```

Expected: exactly `6`. **If it is not 6, STOP and report the count** — another session captured an anomaly
after this plan was written, it is NOT one of the six dispositioned here, and deleting an untriaged
anomaly because it was in the way is the exact failure the no-parked-state rule exists to prevent.

Only once the count is 6:

```bash
grep -v '^- \[' "$F" > "$F.tmp" && mv "$F.tmp" "$F"
```

Then confirm with `cat "$F"`: the header remains, and no `- [` entry line does. The file is gitignored, so
this does not appear in the commit.

- [ ] **Step 7: Run the headless gate**

Run: `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Passed!  - Failed: 0`, total unchanged from Task 10's headless count. This step changes no code;
it runs to prove that.

- [ ] **Step 8: Commit**

```bash
git add docs/coverage-debt.md docs/agent-contract.md CHANGELOG.md ROADMAP.md
git commit -m "docs(sp4): A3/A4 dispositions, the two breaking changes, and the ledgers

A3 -> tracked debt: the DEF-3 SELECTOR path has no specific fact and rests on
21 regression tests. Deferred for the same fixture reason as AB-2.

A4 -> CHANGELOG: DEF-3 was reachable in the DEFAULT shipping configuration -
the OS password flag alone, no rules - so every prior install leaked the
password-field handle set, not only SP3 rule adopters. The original note
understated the affected population; this corrects it in the place consumers
actually read.

Both v1.0.0 breaks are announced where consumers learn: the CHANGELOG, the
agent contract, and each tool's own [Description], which is what an agent reads
at runtime. A5's break is called out as SILENT - it returns different data
rather than failing, so a script keyed on the old value waits forever instead
of erroring.

A1's flat-tree refusal is documented for operators: on classic Win32 dialogs
the parent IS the window root, so one unreadable rect refuses the whole
capture, while deep trees degrade gracefully. 'Screenshots of this one old app
always fail' is otherwise unexplainable.

AB-3 re-validated per the spec's section 3 obligation: anchor still present at
CapstoneFixTests.cs:263, entry stands. AB-6 added for A5's UIA fallback, with
its honest limit stated.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Final gates (spec §9)

Not a task — the branch-completion sequence, run once at the end, in this order.

- [ ] **G1 — build clean.** `dotnet build FlaUI.Mcp.slnx -c Release` → `0 Warning(s)`, `0 Error(s)`.
- [ ] **G2 — headless green.**
      `dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
      → `Failed: 0, Skipped: 0`, total = 870 + 19 new headless facts (16 in Task 3, 3 in Task 8) = **889**.
- [ ] **G3 — Desktop green, both halves, `0 skipped`, one SHA, physical console + lease.**
      `--filter "Category=Desktop&Category!=KnownDefect&Category!=Measurement&FullyQualifiedName!~PopupGrafting"` → **156** (153 + the 3 facts in Task 10);
      `--filter "FullyQualifiedName~PopupGrafting"` → **1**.
- [ ] **G4 — AGY-CAPSTONE** over the whole SP4 range, rounds until one is GREEN. Verify every finding by
      measurement before folding, and verify the peer's SUGGESTED FIX the same way — a correct finding
      routinely arrives with a wrong or incomplete fix. Re-run a fresh round after each fold.
- [ ] **G5 — AGY-TEST-AUDIT** on the committed test suites, after G4 is GREEN.
- [ ] **G6 — merge `sp4-redaction-completeness` to `master` with `--no-ff`**, matching SP0-SP3. Do not push: nothing has been pushed this
      release, by standing decision. Do not delete the branch: this project keeps merged branches.

---

## Exhaustiveness self-audit

Run by the author against the spec, after the plan was written.

**1. Spec coverage — every requirement maps to a task.**

| Spec section | Requirement | Task |
|---|---|---|
| §3 rule + case table (9 rows) | escalate, refuse at root, parent-fetch failure, zero-size, depth cap | 3 (decision) + 4 (wiring) |
| §3 contracts | `maskEscalations`, `escalated`, `RedactionUnmaskable`, refusal message names id+type never Name | 4 (code) + 5 (metadata) |
| §3 testability seam (lazy · failure-as-absence · injectable) | all three | 3 |
| §3 cost + termination (memoize, non-element key, cap) | all three | 3 (cap) + 4 (memoize) |
| §3 ledger consequence (re-validate AB-3) | done, with a recorded outcome | 12 |
| §4 A5 fix steps 1, 2, 4 | GetWindowText · no new field · guarded root fallback | 1 |
| §4 A5 fix step 3 | allowlist REWRITE, not delete | 10 |
| §4 out-of-band announcement (CHANGELOG · agent-contract · `[Description]`) | all three | 5 (description) + 12 |
| §5 A2 rename + no alias + pin preserved | all three | 2 |
| §6 A6 (3 executable · 4 attributes · allowlist-independent rule · one exemption) | all four | 7, 8, 9 |
| §7 A3, A4 dispositions | both | 12 |
| §8 test table (10 rows) | every row has a task and a named mutant | 2, 3, 9, 11 |
| §8a order (source → attributes → rules → allowlist) | task order is 1-2, 3-6, 7-8, 9, 10 | — |
| §9 gates | G1-G6 | — |
| §10 binding constraints (4) | see below | — |

**2. Binding constraints, each traced.** (1) No content unclassified: A1 routes the pixel decision through
`SensitivityOf` and refuses when it cannot act on it; A5 removes the value entirely. (2) Fail closed but
never silently: `maskEscalations` + `escalated` are always present, and name no withheld content. (3) §4.4
zero-cost default path: an install with no rules is unchanged except the three declared wire changes. The
added cost on that path is one `AncestorRectSource` per SEARCH ROOT (the window plus any popups — it read
"per capture" until the bound had to be rooted somewhere) and one closure per *redact-worthy* element
(typically 0-1 per window), not per node — and the escalation walk itself runs only for an element that
failed to report its own bounds. (4) No pre-existing test weakened: the only test edit outside new facts is
`RedactionStatsTests.cs:32`, identifier-only, with its discriminating `1`/`2` untouched and proven by mutant.

**3. Placeholders:** none. Every step carries the actual code or the actual command and its expected output.

**4. Type consistency, checked across tasks:** `MaskEscalation.Resolve` / `MaskResolution` /
`MaskEscalationEntry` / `AncestorRectSource.For` / `CaptureGeometry.Escalated` / `DesktopMaskSet.Rects` /
`DesktopMaskSet.Escalated` / `SnapshotStats.OsPasswordCount` / `ToolErrorCode.RedactionUnmaskable` — each is
spelled once and used identically everywhere after.

**5. Gaps I am flagging rather than closing, with where each resolves.**

- **Task 5 has a non-compiling interior**, between 5a and 5b. That is why they are ONE task with one gate
  and one commit rather than two: an earlier draft split them into separate numbered tasks and told the
  executor the first would not build, which contradicts the standing rule that every task ends at a green
  gate and leaves no way to verify 5a's edits before moving on.
- **The A5 root-`Name` fallback and its `catch` are not covered by any test**, and cannot be without a
  window that has no caption and a provider that throws. Recorded as accepted boundary AB-6 in Task 11,
  with its compensation and its honest limit, rather than left implied.
- **A1's `Refused` path has no Desktop fact** — staging an element that cannot report bounds needs a
  provider that lies, which no fixture can do. It is fully pinned headlessly in Task 3 (three of the seven
  facts), and Task 10 pins the default path on real UIA. This is the AB-1 limitation and is not new.
- **The RULE 5 interpolated-string gap** (`$"...{...}[REDACTED]..."` is not a `LiteralExpressionSyntax`) is
  stated in the rule's own comment. No such site exists in `src/` today; if one is ever added, the rule is
  silent on it. Not closed because closing it means walking `InterpolatedStringTextSyntax` too, which buys
  nothing against a guard the spec already declares bypassable by construction.
- **`CaptureGeometry.MaskRects` and `TextCaptureGeometry.MaskRects` are MISNAMED, and this plan
  does not rename them.** They have held rects from every redaction source since SP3 — OS password fields,
  operator rules, and unreadable-identity fail-closed — and after A1 they also hold ancestor rects from
  escalation, so "password" is wrong three ways over. It is the same defect class as A2, which this
  increment DOES fix. The difference, and the reason for the split: A2's `redacted` is a WIRE field that
  misleads every consumer, and renaming it is free only before v1.0.0. `MaskRects` is an internal DTO
  member with three call sites and no wire presence, so it can be renamed at any time at no cost — while
  renaming it here would add a step touching two records, three consumers and a test, to a plan that has
  already been rewritten across ten review rounds, and each such edit has twice produced a STATE-VERIFY
  mismatch. **Deferred deliberately, not overlooked. Raised at panel round 10.**
- **The exact Desktop pass count in G3 (156) is an arithmetic projection** from 153 at the branch point plus
  the 3 facts added in Task 10. If the actual number differs, do not adjust the expectation to match the
  result — find out which test appeared or vanished first.


---

## Adversarial panel - round 1 ledger (folded; do NOT re-raise)

Solo panel (Axiom Breaker, Cascade Analyst, Literal Implementer, Protocol Pedant, Mechanism Gamer,
Blindspot Auditor, State Corruptor, Resource Vampire, Boundary Smuggler, Dependency Cynic, Activation
Auditor) plus an agy escalation round. Every use-when trigger in the palette fired on an artifact this
wide; none was dropped.

**Folded - from the solo round:**
- A new headless test constructed a `WindowManager`; measured, no headless test in this repo does, and the
  headless filter is what CI runs. Moved to the Desktop suite (Task 10).
- The ROADMAP step said "replace lines 328, 332 and 334" - three unrelated roadmap items. Now an append,
  with the table's "= 10" prose amended in the same edit.
- The `docs/agent-contract.md` edits had no old-text quotes, and that file has TWO rows whose first cell is
  `redacted`. All four edits now quote the line they replace.
- Gate G2's expected total was arithmetically wrong (883 -> 881); G3's moved to 156.
- `redactions` over-counts once several elements escalate to one ancestor rect. Documented in the tool
  description rather than silently changed.
- The refusal message named an element but not a window, which is useless on a full-desktop capture. It now
  names the window handle and process too.
- Task 10's escalation mutant could go red two ways depending on fixture tree shape. Replaced with a
  deterministic counter mutant.
- Task 11 truncated the anomalies file with `>`, the exact pattern its owning skill forbids. Now a
  line-targeted deletion that preserves anything a concurrent session captured.
- `MaxAncestorLevels = 32` versus a snapshot `maxDepth` of 40 was examined and CLEARED - cap-exhaustion and
  root-reaching produce the identical REFUSE - and the reasoning written into the constant's doc so the
  next reader does not re-derive the worry.

**Folded - from the agy escalation, and one of these was fatal:**
- WARNING, CRITICAL: **the ancestor walk had NO ROOT BOUND.** `GetParent` succeeds at the window root and
  the root has valid bounds, so escalation would have returned the root's rect, masked the whole window,
  and handed back a *successful all-black screenshot* - the precise outcome A1 exists to refuse. Every
  headless fact would still have passed, because the injected fake's chain ends in null and so described a
  boundary the real walk did not have: a green suite over a dead guarantee. `AncestorRectSource` is now
  constructed FROM its search root, bounded by that root's RuntimeId, and fails closed when the root's id
  is unreadable. Ledgered as AB-7 with its honest limit.
- **Task 5 and Task 6 were split into an unbuildable sequence**, contradicting the standing rule that every
  task ends at a green gate. Merged into one task with two halves, one gate, one commit.
- **Memoization saves no cross-process traffic.** The cache key is the ancestor's RuntimeId, which must be
  READ before the cache can be consulted - so N siblings still cost N RuntimeId reads. The earlier draft
  claimed a cost win. The doc comment now states the honest justification: it buys COHERENCE (one rect per
  ancestor for the whole pass), trading one COM read for another of the same order.
- `ResolveTextCaptureGeometryAsync` consumes the widened `CaptureGeometry` and was missing from the
  enumeration. Added, with why it needs no edit.

**Verified and NOT folded, recorded so it is not re-raised:**
- The peer's RULE 5 finding described the rule as checking `val == RedactionToken` and therefore bypassable
  by concatenation. It checks `.Contains(...)`, not equality. The interpolated-string gap it also named is
  real and was ALREADY disclosed in the rule's own comment and in the audit before the round ran.
- The peer's `AncestorRectSource.cs:812` / `:821` and `MaskEscalationTests.cs:439` citations are
  FABRICATED - those files do not exist on disk; they are code blocks inside this plan. The substance of
  the root-bound finding was verified against the plan's own text and stands on its own.
- The peer's answer to the dispatcher question was CORRECT and its citation real: `StaThreadContext.cs:29`
  uses `tcs.SetException(ex)`, so a `ToolException` thrown on the STA reaches `ToolResponse.GuardImage`
  unwrapped with its `Code` intact.

**PANEL VERDICT - round 1: REJECT-then-fold. One CRITICAL (no root bound) would have shipped a dead
guarantee under a green suite; 13 findings folded in total.**

### OPEN - for the operator, not resolved here

**A benign element that dies mid-scan now refuses the whole capture.** Traced: a dead element makes
`SensitivityOf` fail closed to `Redact = true` (it already did that before SP4), its `BoundingRectangle`
throws, `GetParent` on a dead element throws, no ancestor resolves, and A1 refuses. Before SP4 the same
element was silently skipped and the capture succeeded.

This IS what the spec's case table specifies - "obtaining the PARENT itself throws -> REFUSE ... a dying
subtree must not silently drop out of the mask set" - so the plan implements it faithfully. But the spec's
own cost narrative described the accepted cost as *occasional black boxes over benign content*, which is
over-masking, not refusal. The refusal case is harsher than what was signed off, and popups and tooltips
die mid-scan during ordinary UI transitions.

Unverified, and worth stating plainly: whether `GetParent` on a stale UIA element actually throws, or still
answers from a cached parent, was ASSERTED by the peer and NOT measured. If it answers, this concern
largely evaporates. Measuring it costs one Desktop experiment and would settle the question before Task 4
is written.

### OPEN #2 — a window UIA cannot BIND is skipped, and the desktop is photographed anyway

Raised at panel round 9 and ledgered as AB-9. On the full-desktop path, a window that cannot be bound —
most commonly an ELEVATED one, since a non-elevated UIA client cannot bind it at all — throws before the
mask walk is even entered, is swallowed by the per-window `catch { }`, and contributes no masks while the
capture proceeds and photographs it.

**Why this is not decided in the plan:** the only fix is a policy choice with real cost. Refusing every
full-desktop capture while any unbindable window is visible would break an ordinary developer desktop with
an admin terminal open, in order to protect a window SP3 never protected either — UIA cannot read its
contents, so no redaction has ever applied to it, and A1 changes nothing about that.

**What is already true, and is the reason this is a boundary rather than a hole:**
`ScreenshotTools.cs:33-35` refuses full-desktop capture outright when a DENYLISTED credential window is
visible, so the highest-value case is covered by a different mechanism.

**The choice, for the operator:** (a) leave it as a ledgered boundary, which is the current state;
(b) refuse full-desktop capture when any visible window cannot be bound, trading availability for
strictness; or (c) narrow (b) to windows whose process is elevated, which needs a new elevation probe and
is its own increment.


### Round 2 (rotation seat: Fix-Edge Hunter) - folded; do NOT re-raise

Round 2 existed because a fix spawns its own edges, and it did: the first two findings are consequences of
round 1's own root-bound fix.

- **CRITICAL: the root bound made a POPUP container unmaskable.** Bounding every search root identically
  meant an element inside a popup that escalated to the popup itself hit the bound and REFUSED - when
  masking the popup is perfectly safe, blacks out only the floating popup, and leaves the rest of the
  capture intact. Worse, it is stricter than the spec, whose rule names the WINDOW root specifically. The
  bound now forks: window root -> REFUSE, popup root -> mask it and stop.
- **CRITICAL, and it defeated the bound entirely: `IdOf` failed OPEN during the climb.** The check compared
  each ancestor's RuntimeId against the root's, and `IdOf` swallows a throw and answers null - which does
  not equal a non-null root id. One transient COM failure reading the ROOT's id mid-climb and the walk
  sails past it into the DESKTOP, reads the desktop's valid bounds, and returns a successful all-black
  screenshot of the whole monitor. An unreadable ancestor id now STOPS the walk: an ancestor that cannot be
  proven inside the root is not climbed past.
- **Path-2 popups are scanned TWICE.** VERIFIED at `PopupFinder.cs:63-78`: popups that are direct CHILDREN
  of the window are returned as their own search roots, while `roots[0].FindAllDescendants()` already
  covers those same subtrees. Under a refuse-at-every-root bound, pass 0 would mask an element via the
  popup and pass 1 would then REFUSE the identical element. The window/popup fork above removes the
  contradiction; the double scan itself predates SP4 and is left alone.
- **Halting between 5a and 5b was an inescapable trap.** On resume, 5a's STATE-VERIFY expects the original
  text, finds 5a's own output, and reports STATE_MISMATCH - stranding the repo in the one non-compiling gap
  the plan contains. Task 5 now carries an explicit resume rule.
- **The anomalies step contradicted itself.** It told the executor to leave any surviving entry alone while
  handing them `grep -v '^- \['`, which strips every entry line so none can survive. Now it COUNTS first
  and stops if the count is not exactly 6.
- **RULE 5's exemption matched the SHORT class name**, so declaring a second `class ElementContent`
  anywhere in `src/` and putting the literal inside it bypasses the guard while satisfying its letter. The
  exemption is now keyed on the type name AND its defining file path.
- **Standing rule 5 said "gate after every task" while Task 5 says not to.** The rule now defines "task" as
  a numbered `## Task N` and states that Task 5's halves are interior.

**PANEL VERDICT - round 2: REJECT-then-fold. Two CRITICALs, both introduced BY round 1's fix - one made
popups unmaskable, the other let a single transient COM failure defeat the bound and mask the whole
desktop. 7 findings folded.**


### Round 3 (rotation seat: Termination Auditor) - folded; do NOT re-raise

Round 3's job was the edges round 2's fixes cut. Two of its findings invalidated round 2's own fix, which
is the second consecutive round where that happened - worth recording as evidence that re-running after a
fold is not ceremony.

- **CRITICAL: the window-vs-popup fork masked whole monitors.** A WPF light-dismiss popup's host is a
  transparent FULL-SCREEN overlay, so its `BoundingRectangle` is the entire screen while the visible popup
  is small. Round 2's "a popup root is always safe to mask" therefore returned a successful all-black
  screenshot for the most common popup shape there is - reintroducing the exact hallucination trap A1
  exists to refuse. **Replaced with a geometric rule that subsumes the fork**: an ESCALATED rect that
  CONTAINS the captured region is not a mask, it is an all-black image wearing one, and it is rejected.
  That single rule catches the window root, the full-screen overlay, and the desktop at once, without the
  decision needing to know which element produced the rect. The identity stop is KEPT alongside it -
  identity catches the exact root whatever its geometry, geometry catches everything identity cannot name.
- **HIGH: latching on an unreadable ancestor id was over-refusal, and it contradicted the plan's own text.**
  `IdOf` answers null both when the read throws AND for any element exposing no RuntimeId, so a single
  anonymous container - a bare WPF `Border` is the ordinary case - refused the whole capture. It also
  directly contradicted `RectOf`'s own comment promising an unreadable id "resolves UNCACHED rather than
  being skipped", which the abort made unreachable. The walk now CLIMBS ON, which is safe only because the
  geometric rule above is the real net.
- **MEDIUM: the flat-Win32 prose overclaimed.** It said one unreadable rect "refuses on the FIRST
  escalation step"; mechanically the loop still runs to its cap, answering null immediately once the source
  has stopped. The OUTCOME is decided at the first step. Prose corrected to say so rather than implying a
  short-circuit that is not there.
- **HIGH, and it is now a stated limit rather than a fix: escalation assumes an ancestor ENCLOSES its
  descendants' painted pixels.** A child can paint outside its parent through a negative margin, absolute
  positioning, or a render transform, in which case the ancestor mask covers less than the element did and
  the uncovered part is photographed. This cannot be checked - escalation runs precisely when the element's
  own extent is the unavailable quantity. Ledgered as AB-8 with no compensation, because a guarantee that
  is 99% true and documented as absolute is worse than one that is 99% true and says so.
- **`SensitivityOf` cannot throw - CONFIRMED by tracing, not by assertion.** Every path swallows:
  `RedactionPolicy.IsPasswordOrFailClosed` (`RedactionPolicy.cs:10`), `ElementContent.SafeIdentity`
  (`ElementContent.cs:137`), `RedactionRule.Safe` and `RedactionRule.IsMatch` (`RedactionRules.cs:66,73`,
  the latter `catch { return true; }`). The Task 4 catch is dead code TODAY. Kept as defence-in-depth and
  now labelled as unreachable, with the sharper reason the peer supplied: an unlabelled catch reads as "the
  CALLER provides the fail-closed guarantee", and a developer who believed that could strip the internal
  logic and leave the eleven TEXT sites - which have no such catch - silently leaking while the pixel path
  kept working and hid it.

Also folded from this round's solo seats: the identity was being read THREE times per level (bound check,
cache key, popup branch) on the one path memoization was meant to keep cheap - it is now read once and
threaded.

**PANEL VERDICT - round 3: REJECT-then-fold. One CRITICAL (full-screen popup overlay masking the monitor)
and one HIGH over-refusal, both introduced by round 2's fix; plus one real limit promoted from silent
assumption to ledgered AB-8. 6 findings folded.**


### Round 4 (rotation seat: Geometry Adversary) - folded; do NOT re-raise

Third consecutive round in which the previous round's fix cut the new edges. Recorded as a pattern, not an
anecdote: on this mechanism, folding without re-running would have shipped a defect every single time.

- **CRITICAL: the geometric guard did not fire on a MAXIMIZED window.** A maximized window's UIA
  BoundingRectangle BLEEDS PAST the monitor - its invisible resize border gives it a negative origin and a
  width and height larger than the screen. A WPF light-dismiss overlay is exactly the monitor. So
  `overlay.Contains(window)` is FALSE (because -8 < 0), the full-monitor mask passed the guard, and round
  3's fix returned the all-black image it was written to reject. The yardstick is now the capture
  INTERSECTED WITH THE RENDERABLE DESKTOP, which removes the bleed and makes the comparison the one that
  was meant. This also closes the Boundary Smuggler's route: a walk that escapes its root through an
  unreadable identity reaches the desktop, whose rect now correctly contains the visible capture and is
  rejected.
- **CRITICAL, and it is a LEAK that predates SP4: a raw exception from the mask walk becomes a SILENT SKIP
  on the full-desktop path.** `AllMaskRectsAsync` wraps each window in `catch { }` to skip one it cannot
  bind, and rethrows only `RedactionUnmaskable`. So a COMException from the capture-bounds read - or from
  `roots[0].FindAllDescendants()` - drops that window's ENTIRE mask set while the full-desktop capture
  proceeds and photographs it in the clear. It defeated TWO decisions at once: A1's refusal, and the
  strict-on-roots[0] rule whose whole point is that a dead target must not be quietly skipped. Both reads
  are now CONVERTED to `RedactionUnmaskable` rather than propagated raw. (The old code read
  `target.BoundingRectangle` unguarded at its return statement with the same swallow downstream, so this
  hole is older than this increment - it is fixed here because A1 is what makes the difference between "no
  mask" and "refuse" load-bearing.)
- **HIGH: an element-scoped capture would have refused constantly.** With `@ref` supplied, `captureBounds`
  is one small element while mask rects are still collected window-wide, so ANY unrelated element
  escalating to the window root covers the capture and refuses. An ancestor that does not OVERLAP the
  captured region now contributes nothing and does not refuse: the secret is inside that ancestor and that
  ancestor cannot appear in the photograph, so refusing has no security value. It still counts as an
  escalation in the diagnostic, because the provider really did fail. (This rests on the same
  ancestor-encloses-descendant assumption as escalation itself - AB-8 - and introduces no new one.)
- **MEDIUM: prose in `AncestorRectSource.For` still described round 3's behaviour.** It claimed returning
  null covers "the walk reached the root", which stopped being true when the root began being OFFERED. The
  comment now says which is authoritative if the two ever disagree again. This is the SECOND round to find
  stale prose in this file; both times the sentence was correct when written.

**Partially folded, with the peer's framing rejected on reasoning:** the Mechanism Gamer argued that
reading `captureBounds` once "actively causes" mask misalignment when a window moves mid-walk. It does not:
reading it AFTER the walk instead just inverts which side is stale, and the race is inherent to capturing a
moving window. What WAS wrong is my comment claiming the single read prevents that misalignment - an
overclaim, now corrected to state what the single read actually buys (one rect serving as yardstick,
returned Bounds, and captured region) and what it explicitly does not.

**PANEL VERDICT - round 4: REJECT-then-fold. Two CRITICALs - one defeating round 3's own guard on
maximized windows, one a pre-existing leak where a raw exception becomes a silent skip on full-desktop
capture - plus one HIGH over-refusal and one stale-prose defect. 5 findings folded, 1 partially.**


### Round 5 (rotation seat: Contract Archaeologist) - folded; do NOT re-raise

- **CRITICAL, and it is the THIRD instance of one class: `PopupFinder.SearchRoots` was still unguarded.**
  Round 4 wrapped the capture-bounds read and the roots[0] enumeration individually and missed this one,
  which is a UIA walk of its own. Any raw exception escaping the mask walk is swallowed by
  AllMaskRectsAsync's `catch { }` and becomes a window photographed with NO mask set. Fixed
  STRUCTURALLY rather than by adding a third guard: the whole region is now wrapped in ONE conversion to
  RedactionUnmaskable, with ToolException passing through unchanged so RefNotFound and the refusals keep
  their codes. Guarding reads one at a time had already failed twice; the lesson is that the safe default
  has to be structural, not a list of remembered sites.
- **CRITICAL LEAK, found INDEPENDENTLY by the peer and by the driver in the same round: an EMPTY yardstick
  disabled masking entirely.** A window wholly off the renderable desktop intersects it in
  Rectangle.Empty, and NOTHING IntersectsWith an empty rect - so every escalated mask would be classified
  "outside the capture", dropped, and the capture would come back unmasked. A guard producing a leak is
  the worst kind. Fixed in two places: the call site degrades an empty intersection back to the unclipped
  capture rect (the pre-clipping behaviour, and the maximized-bleed case the clipping exists for cannot
  arise when nothing is on screen), and the DECISION refuses outright on an empty yardstick so a future
  caller that breaks the guarantee fails closed instead of silently.
- **The self-audit said "one AncestorRectSource per capture".** It has been per SEARCH ROOT since round 2,
  when the bound had to be rooted somewhere. Corrected.
- **AB-7 still described round 1's behaviour** - "reaching the root becomes a REFUSAL" - which stopped
  being true at round 2 (popup fork) and again at round 3 (geometric rule). Rewritten, with a note that
  this entry's behaviour changed three times during review.
- **Stale `<paramref name="captureBounds"/>`** in MaskEscalation's doc after the parameter was renamed to
  `yardstick` (driver's own find). Harmless to the build - neither project sets
  GenerateDocumentationFile, VERIFIED in both csproj files - but wrong.

**Confirmed clean, and worth recording as a positive result:** the peer enumerated every caller of
`ResolveWindowCaptureGeometryAsync` and found none outside the screenshot path, the OCR path and the test
suite - so the two (now one) new throw sites reach no unintended consumer. It also traced `Irrelevant`
(a flag REMOVED in round 6, when the drop it represented turned out to codify a leak - see below)
through the screenshot path and confirmed it cannot be confused with `Refused` or with a zero-area mask.
Axiom Breaker and Mechanism Gamer reported no new findings.

**PANEL VERDICT - round 5: REJECT-then-fold. Two CRITICAL leaks - a third unguarded UIA call becoming a
silent skip, and an empty yardstick dropping every mask - plus two stale-prose defects in sections nobody
had reread since round 1. 5 findings folded.**


### Round 6 (rotation seat: Exception Taxonomist) - folded; do NOT re-raise

- **CRITICAL, and it made the plan UNEXECUTABLE: Task 4's STATE-VERIFY block had grown ~50 lines of code
  that does not exist on disk.** Five rounds of review edits anchored new code onto a line that appears
  ONLY inside that verify block, so the "confirm the current code is exactly this" quote silently absorbed
  the captureBounds guard and the yardstick computation. An executor would have reported STATE_MISMATCH at
  the first task touching the mask walk, and the new code was never inserted anywhere - so had it somehow
  proceeded, Task 5 would have referenced `yardstick` and `captureBounds` as undefined symbols. The
  verify block is restored to the file's real contents, the new code moved into the REPLACEMENT block where
  it belongs, and a standing rule added at the top of the plan so the class cannot recur.
- **CRITICAL: the outside-the-capture DROP codified a leak, and its test asserted the bug.** UIA logical
  parents do not always enclose their visual children: a WPF tooltip, popup or drag adorner is routinely ON
  SCREEN while its logical parent - a scrolled-away button - is entirely off it. If the tooltip's own bounds
  throw, its off-screen parent "cannot appear in the capture", the mask was dropped, and the tooltip was
  photographed in the clear. A non-overlapping candidate is now UNUSABLE: keep climbing toward one that
  does overlap, refuse if none does. `MaskResolution.Irrelevant` is deleted, and the fact that asserted the
  drop is replaced by two that forbid it.
  ⚠ This also retires round 4's justification for the drop. The element-scoped over-refusal it was meant to
  soften is the fail-closed direction and is now accepted explicitly: an element whose position cannot be
  determined might be inside the captured region, and guessing otherwise is what leaked.
- **HIGH: an off-screen window with a broken provider FATALLY failed the whole full-desktop capture.** Its
  degraded yardstick equalled its own bounds, so any escalation to the window root "blacked out the
  capture" and refused - and AllMaskRectsAsync rethrows that. A window with no renderable overlap now
  returns early with no masks and no refusal: it contributes no pixels to any capture, so there is nothing
  to withhold and nothing to refuse over.
- **MEDIUM: the blanket `catch (System.Exception)` reclassified CRITICAL failures.** An OutOfMemoryException
  would have been reported to the agent as RedactionUnmaskable with the advice "retry once the UI has
  settled". Now filtered out, along with cancellation - which the query path does not have today
  (VERIFIED: AutomationDispatcher.cs:61 is `_query.RunAsync(func)`, no timeout, no CancellationToken; only
  the ACTION path has AwaitWithTimeout) - so the filter keeps that true by construction.
- **`ex.Message` on the wire: found INDEPENDENTLY by the peer and the driver in the same round, already
  folded before the peer's answer arrived.** Interpolating an arbitrary underlying exception's message into
  a payload an agent reads is unclassified third-party text on the wire - a direct violation of this
  increment's own binding constraint 1, inside the feature whose subject IS that constraint. The peer
  supplied the concrete case: a provider that fails reading a sensitive property often echoes the value it
  choked on. All three sites now emit `ex.GetType().Name`.
- **Driver's own find this round:** the empty-yardstick test used `Rectangle.IsEmpty`, which requires all
  four fields to be zero, while `Rectangle.Intersect` compares with `>=` and yields a degenerate rect at
  NON-ZERO coordinates for rects that merely touch along an edge. The leak would have returned for a window
  sitting exactly against the desktop edge. Both sites now test extents, pinned by a Theory with the
  touching-edge rows.

**PANEL VERDICT - round 6: REJECT-then-fold. Two CRITICALs - one that made the plan unexecutable, one that
codified a leak in a test - plus a fatal over-refusal, a critical-exception misclassification, and two
finds that the peer and the driver made independently. 6 findings folded.**


### Round 7 (rotation seat: Executability Auditor) - folded; do NOT re-raise

- **CRITICAL, an executability HALT: `Rule5_exempts_only_the_type_that_defines_the_token` could not pass.**
  Round 2 hardened RULE 5's exemption to require the defining FILE as well as the type name; the test
  written in round 1 still ran its `ElementContent` snippet through the helper's hardcoded `src/Fake.cs`,
  so the snippet was correctly FLAGGED and `Assert.Empty` failed. Five rounds passed without anyone
  noticing that a round-2 fix had broken a round-1 test. The helper now takes a path, the exemption case
  passes the real defining path, and a THIRD assertion was added for the impostor case - the same type name
  at a different path must still be flagged, which is the property the file-keying exists for and which no
  test previously covered.
- **HIGH: the off-screen early return was right for one caller and wrong for the other.** Round 6 added it
  to stop an invisible off-screen window fatally failing a full-desktop capture. But a caller that NAMES a
  window will photograph its rect regardless of what is rendered there, so suppressing that window's masks
  hands back an unmasked image of the named target. The two callers now get two answers via an explicit
  `skipIfNoRenderableOverlap` parameter, defaulting to the safe one: the full-desktop sweep skips, a named target
  falls back to the unclipped yardstick and computes masks normally. The parameter exists because the
  method cannot infer its caller, and guessing was wrong in BOTH directions across two rounds.
- **MEDIUM: the degenerate-yardstick fact had become a false-GREEN.** Round 6's early return intercepted
  degenerate yardsticks before `Resolve` could ever see one, so the test asserted a guard nothing could
  reach. The `skipIfNoRenderableOverlap` fork restores reachability (a zero-size NAMED window falls through to it),
  and the fact now documents exactly when it is reached plus the instruction to DELETE it rather than keep
  it if a future change makes it unreachable again.
- **Driver's own find this round, by running the check the rotation seat was asked to run:** Task 1's
  STATE-VERIFY cited `WindowManager.cs:611-628` while the method ends at 627 - line 628 is blank - so a
  literal comparison would have reported a false STATE_MISMATCH on the very FIRST task. Corrected, and all
  six STATE-VERIFY quotes are now verified against disk BY MEASUREMENT (extract from the plan, diff against
  the file): WindowManager 611-627, PerceptionManager 861-871 and 899-914, SnapshotStats 6-16,
  ScreenshotTools 51-52, FindTextTools 103-104, plus the CaptureGeometry line. That is the measured version
  of a claim round 6 could only assert.
- **Confirmed clean by the peer, recorded as a positive result:** Task 4's STATE-VERIFY matches the file
  exactly (it re-read `PerceptionManager.cs` to check), and `MaskEscalationEntry` is defined before use.
  Axiom Breaker, Cascade Analyst and Boundary Smuggler all reported no new findings.

**PANEL VERDICT - round 7: REJECT-then-fold. One executability HALT that five rounds had missed, one
caller-dependent correctness fork, one false-GREEN, and one false STATE_MISMATCH on the first task.
4 findings folded.**


### Round 8 (rotation seat: Regression Historian) - folded; do NOT re-raise

The rotation seat's brief was "which earlier fix did a later fix silently break?", and it found the worst
case of that so far: **a fix this plan's own ledger CLAIMED was applied, and which was not.**

- **CRITICAL: `skipIfNoRenderableOverlap` was a DEAD LETTER, and round 7's ledger asserted otherwise.** The parameter
  was added to the signature (Step 4b) and passed by the full-desktop sweep, but the method body still
  returned unconditionally - so the early return fired for EVERY caller. A window- or element-scoped
  capture of an off-screen target therefore still received zero masks: exactly the leak round 7 believed it
  had closed. Root cause is worth recording because it is a process defect, not a reasoning one: the edit
  that would have written the body fork was in a script that aborted on a later assertion and never wrote,
  while the follow-up script only re-applied the signature and the call site. **The ledger entry was
  written from intent rather than from the file.** The body fork is now actually present.
- **CONSEQUENCE, also confirmed: two things round 7 recorded as fixed were still broken** because they
  depended on that fork - the named-target leak, and the false-GREEN `A_degenerate_yardstick_refuses...`
  fact, which production still could not reach. Both are genuinely closed now.
- **MEDIUM: RULE 5's file-keyed exemption was case-brittle.** `relPath` comes from
  `Path.GetRelativePath`, which preserves the ON-DISK casing, so an ordinal `EndsWith` against
  `"src/FlaUI.Mcp.Core/Perception/ElementContent.cs"` would FAIL - and fail the build, by flagging
  `ElementContent`'s own constant - on a clone whose folder is `Src`. The path half now compares
  case-insensitively (Windows paths are), while the TYPE-name half stays ordinal; an impostor still needs a
  different path, not merely different casing.
- **Driver's own find this round, by counting rather than trusting: 7 test methods (16 xunit cases) that
  belong to `MaskEscalationTests.cs` were physically sitting in TASK 4.** Five rounds of appending facts
  had anchored them wherever the nearest matching summary happened to be. Two consequences, both real:
  Task 3's gate expected `Passed: 16` from a file that then held 7, and Task 4's `git add` names only three
  source files, so those facts would have been left UNCOMMITTED. All of them are pure `MaskEscalation.Resolve`
  decision facts and now sit in Task 3 where that type is built. MEASURED after the move: Task 3 = 16
  cases, Task 4 = 0, Task 8 = 3, so G2's 870 + 19 = 889 reconciles for the first time since round 3.

**PANEL VERDICT - round 8: REJECT-then-fold. One CRITICAL that was a ledger entry written from intent
rather than from the file - the most dangerous failure mode a review of this shape has - plus its two
dependent regressions, a case-brittle guard, and 16 test cases sitting in the wrong task. 4 findings
folded.**

⚠ **PROCESS LESSON, recorded because it nearly shipped a leak twice:** an entry in a folded ledger is a
CLAIM about the artifact, not evidence. From here on, every ledger entry in this file should be
re-verified against the file before it is trusted - which is exactly what the Regression Historian seat
did, and why it caught this.


### Round 9 (rotation seat: Fresh-Eyes Implementer) - folded; do NOT re-raise

The seat's lens was deliberately different: not "is this wrong" but "is this still followable" after eight
rounds of patching. It found a real contract defect that a bug-hunting lens had missed nine times.

- **HIGH: the caller VIOLATED the callee's most heavily-documented invariant.** `MaskEscalation.Resolve`'s
  docstring asserted, loudly and in capitals, that its yardstick "IS THE VISIBLE CAPTURE, NOT THE RAW
  CAPTURE RECT" - while the caller, since round 7, reassigns it to the UNCLIPPED rect for a named target
  with no renderable overlap. A fresh engineer modifying `Resolve` would trust the docstring and could
  confidently break the geometric guards. The parameter is renamed `captureYardstick`, its real contract
  (non-degenerate; usually but NOT always clipped) now sits on the parameter where a modifier will read it,
  and the reassignment site says why it is allowed. **The comment was correct when written and the caller
  changed underneath it** - the third time in this review that a true sentence became false without being
  edited.
- **HIGH, pre-existing, and SURFACED rather than decided: a window UIA cannot BIND is skipped and
  photographed anyway.** An ELEVATED window cannot be bound by a non-elevated UIA client at all, so the
  resolve throws BEFORE the mask walk - outside the blanket conversion, which lives inside the STA lambda -
  and the per-window `catch { }` turns "could not prove this is safe" into "capture it". Ledgered as AB-9
  and raised as OPEN #2 for the operator, because the only fix is a policy choice with real cost: refusing
  full-desktop capture whenever an admin terminal is visible, to protect a window SP3 never protected
  either. Partial compensation already exists and is why this is a boundary rather than a hole -
  `ScreenshotTools.cs:33-35` refuses outright when a DENYLISTED credential window is visible.

**Rejected on reasoning, with the reasoning recorded in the code:** the Mechanism Gamer called the
degenerate-yardstick refusal redundant, since the overlap rule discards every candidate against a zero-area
yardstick and the loop refuses at the cap anyway. It is right about the OUTCOME and wrong about the COST:
"at the cap" means MaxAncestorLevels ancestor fetches first, each a cross-process COM round trip on the
single query STA, for a question already answered. Kept as a short-circuit, and now labelled as one so the
next reader does not delete it as dead code.

**Confirmed clean, recorded as positive results:** the Literal Implementer verified the task ORDER holds
against the new signature - `skipIfNoRenderableOverlap` is optional, so Task 4's build gate passes with existing
callers untouched, and Task 5b then consumes it. Axiom Breaker and Boundary Smuggler reported no new
findings. Separately, the driver re-verified all 22 ledger claims against the file mechanically after round
8's lesson; 22 of 22 hold (one initial FAIL was a line-wrap artefact in the checker, not in the artifact).

**Driver's own find this round:** recorded WHY the root-identity stop is not redundant against the
geometric rule, since the next reader will ask. `captureBounds` is read ONCE at the start of the walk while
an ancestor's rect is read DURING it, so on a window that MOVES in between, the root's live rect no longer
contains the stale yardstick, the geometric check PASSES, and the root is accepted as a mask - an image
blacked out in the wrong place with the secret possibly still visible. The identity stop does not care
where the window is.

**PANEL VERDICT - round 9: REJECT-then-fold. One contract violation that nine rounds of bug-hunting had
missed because it needed a comprehension lens, one pre-existing boundary surfaced to the operator, and one
finding rejected with recorded reasoning. 2 folded, 1 rejected, 1 documented.**


### Round 10 (rotation seat: Naming & Contract Auditor) - folded; do NOT re-raise

⚠ **The peer's reply arrived TRUNCATED** - its final message was a meta-summary rather than the panel text,
naming its findings (`visibleCapture`, `MaskRects`, `Resolve`'s docstring, and a Cascade Analyst
finding on swallowed critical exceptions) without their full argument. Recorded plainly rather than
papered over: three of the four had already been found independently by the driver in the same round, and
the fourth was reconstructible and verified. Round 11 re-covers the same ground with a fresh seat, so a
detail lost to the truncation gets a second chance rather than being assumed absent.

- **CRITICAL, and it is the edge round 6's own fix cut: filtering the misclassification moved the leak one
  frame up.** Round 6 correctly stopped the mask walk reclassifying `OutOfMemoryException` as
  `RedactionUnmaskable` - but the exception then propagates OUT of the walk into
  `AllMaskRectsAsync`, where a bare `catch { }` swallowed it, skipped the window, and let the
  full-desktop capture photograph it. The skip now carries the same filter as the conversion.
- **Driver's own finds this round, converging with the peer on the first:**
  · `visibleCapture` was no longer the visible capture - after round 7 the caller reassigns it to the
    UNCLIPPED rect - so the local is renamed `yardstick`, matching the parameter renamed in round 9. The
    same lie, one frame up, found one round later.
  · `skipIfOffscreen` COLLIDED with an established domain term: UIA's `IsOffscreen` means "scrolled or
    virtualized out of view", which this repo uses throughout (`includeOffscreen`, `isOffscreen`), and is
    NOT what the parameter means. Renamed `skipIfNoRenderableOverlap`.
  · `IsUsable` promised more than it delivered - a rect can be "usable" and still be rejected for
    overlapping nothing or blacking out the capture. Renamed `HasArea`, which is exactly what it tests.
  · **`Escalated` meant a BOOL on one type and a LIST on two others** (`MaskResolution.Escalated` vs
    `CaptureGeometry.Escalated` / `DesktopMaskSet.Escalated`). The list-valued members are now
    `Escalations`; the WIRE field stays `escalated`, because that is the published contract.
- **Flagged, deliberately NOT fixed, with the reason recorded:** `MaskRects` has been misnamed since
  SP3 - it holds rects from every redaction source, and after A1 also ancestor rects - which is the same
  defect class as A2. A2 is fixed here because it is a WIRE field, misleading every consumer, and free to
  rename only before v1.0.0. `MaskRects` is internal with three call sites and can be renamed at no
  cost at any time, while renaming it at round 10 would add a step touching two records, three consumers
  and a test to a plan whose every such edit has twice produced a STATE-VERIFY mismatch. Deferred, not
  overlooked.

**PANEL VERDICT - round 10: REJECT-then-fold. One CRITICAL leak created by round 6's own fix, four naming
contracts that lied at their use sites, and one misnaming flagged with an explicit reason for deferring.
5 folded, 1 flagged, 1 peer reply truncated and re-covered next round.**


### Round 11 (rotation seat: Diff Minimalist) - folded; do NOT re-raise

The first round in which most seats reported NO NEW FINDINGS - Axiom Breaker, Cascade Analyst, Boundary
Smuggler, Literal Implementer and Mechanism Gamer all came back empty, and the peer answered the
"is it ready" question directly rather than hunting for a finding to justify the round.

- **The `PasswordRects` deferral is REVERSED, and the reversal is worth recording.** Round 10 flagged the
  misnaming and deferred it, arguing that each such edit had twice produced a STATE-VERIFY mismatch. That
  argument was weaker than it looked: the mismatch risk is now MEASURABLE - a script extracts every
  STATE-VERIFY block and diffs it against disk - so the cost that justified deferring had already been
  removed by an earlier round. The peer independently named this as the last thing between the plan and
  "ready". Renamed: `PasswordRects` -> `MaskRects` on both records, `AllPasswordRectsAsync` ->
  `AllMaskRectsAsync`, as new Step 4a with a complete site table. Internal only; no wire field is affected,
  because A2 is the wire half of this same defect.
- **HIGH: `Resolve`'s docstring said the window/popup fork was an earlier draft that was "wrong in both
  directions", while `AncestorRectSource` still implements it and its own doc defends it.** Both sentences
  were written truthfully and neither was edited when the design changed underneath them: the fork WAS
  wrong as the SOLE mechanism, and is correct as an identity stop beside the geometric rule. The doc now
  says which of those it is talking about. This is the FOURTH stale-comment defect in this review, all four
  the same shape - a sentence that was true when written.
- **ACCEPTED removal: the `id is not null &&` guard was dead code.** `string.Equals(null, x, Ordinal)` is
  already false, so the operator did nothing. Removed, with the comment kept, because the comment is what
  carries round 2's finding that an unreadable id must not abort the walk.

**REJECTED, with the reasoning written into the code where the next reader will try again:**
- *"`PerceptionPolicy.IsDenied` in the caller duplicates the callee's check."* True about the ANSWER, wrong
  about the cost: deleting it reaches the same result only after ENUMERATING A DENYLISTED WINDOW'S ENTIRE
  TREE to build a mask set that is then discarded. On a credential window that is the one tree this product
  most wants never to walk.
- *"`if (!geo.Denied && !geo.Minimized)` duplicates a callee guarantee."* True today, and it is exactly the
  "caller relies on the callee's internals" coupling that produced round 9's finding, one frame in the
  other direction. If a future change returned a non-empty rect alongside Denied, deleting this would turn
  that into a silent mask of a window we refused to inspect.

**Driver's own find this round, applying the same minimalist lens:** the degenerate-yardstick check was
INSIDE the loop, so it could not run until an ancestor with area had already been fetched - meaning the
short-circuit that round 9 justified it by did not actually short-circuit anything. The yardstick does not
change between iterations; hoisted above the loop, which is the only position at which the check does what
it is justified by.

**PANEL VERDICT - round 11: REJECT-then-fold, but the shape of the round changed. Five of six seats found
nothing; one CRITICAL-class stale doc, one reversed deferral, one accepted removal, two removals rejected
with recorded reasoning, one driver find. The peer's answer to "is it ready" was YES conditional on the
two items now folded.**


### Round 12 (rotation seat: Adversary of Record) - folded; do NOT re-raise

Five of six seats reported no new findings again. The rotation seat's brief was to name the ONE thing that
would most embarrass everyone if it shipped - and it did, plus it refuted one of the driver's own
rejections from round 11.

- **CRITICAL, and self-inflicted by round 11: the plan was UNEXECUTABLE again.** Task 4 Step 4a renames
  `AllPasswordRectsAsync` and `geo.PasswordRects`; Task 5b Step 1 then STATE-VERIFIES
  `PerceptionManager.cs:899-914` against the PRE-rename text. An executor following the plan literally
  halts at `STATE_MISMATCH` caused by the plan's own earlier step, aborting the rollout halfway. Fixed by
  quoting the POST-Task-4a text and labelling that block as the single one in this plan that quotes the
  file as a PRIOR TASK leaves it rather than as it is on disk - with an explicit instruction that the
  mechanical disk-diff must exclude it, so a future run does not "fix" it back into a halt. The standing
  rule at the top of the plan is reworded to cover this case, since it previously said STATE-VERIFY quotes
  disk, full stop, and that is now true of every block but one.
  ⚠ This is the SECOND executability halt caused by an edit interacting with a STATE-VERIFY block, and the
  FIRST caused by a rename. Both were caught by a panel, neither by the mechanical check - the check
  compares against DISK, and neither defect was a disk mismatch.
- **THE DRIVER'S OWN REJECTION FROM ROUND 11 WAS FACTUALLY WRONG, and the peer refuted it with a
  citation.** I rejected removing the caller's `IsDenied` pre-filter on the grounds that deleting it would
  ENUMERATE a denylisted window's entire tree. MEASURED at `PerceptionManager.cs:852`: the callee tests
  `PerceptionPolicy.IsDenied` FIRST and returns immediately, well before `PopupFinder.SearchRoots` at
  `:861`. No tree is ever walked for a denied window, with or without that line. The check is KEPT, but on
  the accurate and much smaller ground that it saves one `RunWithWindowAndDesktopAsync` dispatch per denied
  window and keeps the denylist visible where windows are chosen. The false reasoning is recorded at the
  site rather than quietly replaced, because it was persuasive and someone will re-derive it.
  ⚠ Worth stating plainly: a rejection written into the code is exactly as capable of being wrong as a
  finding, and this review had been treating its own rejections as settled. The peer accepted the OTHER
  rejection (the Denied/Minimized guard) on its merits.

**PANEL VERDICT - round 12: REJECT-then-fold. One CRITICAL executability halt introduced by round 11's own
rename, and one of the driver's rejections refuted by measurement. The seat's answer to "is it ready" was:
ready once the STATE-VERIFY contradiction is patched. It now is.**


### Round 13 (rotation seat: Sequence Simulator) - folded; do NOT re-raise

The seat was chosen because of what round 12 exposed: two executability halts in this review were caused by
one task's edit invalidating a LATER task's verify block, and NEITHER was catchable by a disk diff, because
neither was a disk mismatch. This seat walks the tasks as a SEQUENCE, holding a model of the repository
after each step.

- **HIGH, and it would have committed a BROKEN BUILD into history: Task 4's `git add` was never updated
  when Step 4a was added.** Step 4a renames across FIVE files; the commit staged three. Two distinct
  consequences, both real: `TextCaptureGeometry.cs` would never be staged by ANY task in the plan, and the
  commit at the end of Task 4 would contain a `PerceptionManager.cs` referencing `MaskRects` /
  `AllMaskRectsAsync` while the four files that use those names still said `PasswordRects` at that commit.
  Found independently by the peer and the driver in the same round. All seven paths now staged.
- **Driver's own find, which the peer did NOT catch: Step 4b silently moved the line numbers Step 5 names.**
  Step 4b replaces the one-line signature at `:848` with a six-line block, shifting everything below it down
  by five - so Step 5's `:862-871` is `:867-876` by the time an executor reaches it. Step 5 was already
  content-anchored in its prose, so a careful executor was safe; one following the numbers was not. Both
  numbers are now given, with the content anchor stated first, because a plan naming only the stale number
  is how someone edits the wrong lines confidently.
- **Driver's own sweep, recorded as a positive result:** every task's `git add` was checked mechanically
  against the files its body names. After the Task 4 fix, four apparent omissions remain and ALL FOUR are
  false positives, verified one by one - `AutomationDispatcherTests.cs` is cited as measurement evidence,
  `VerifyReader.cs` appears in Task 2's explicit "do NOT rename these" list, `SnapshotDiff.cs` is a Task 8
  mutant that is reverted before committing, `WindowManager.cs` is only STATE-VERIFIED by Task 9, and
  `CapstoneFixTests.cs` is only grepped for the AB-3 anchor. `src/Fake.cs` is a path inside a test snippet
  and not a file at all.

**Confirmed clean by the peer, recorded as positive results:** it verified that every STATE-VERIFY block
matches the SIMULATED state - explicitly including Task 5b Step 1's post-Task-4a block, which is the one
round 12 fixed - and that both gate numbers are right at the point in the sequence where they are checked
(870 + 19 = 889 headless, 153 + 3 = 156 Desktop). Axiom Breaker, Cascade Analyst, Boundary Smuggler,
Literal Implementer and Mechanism Gamer all reported no new findings.

**PANEL VERDICT - round 13: REJECT-then-fold, and narrowing. One HIGH that would have put a broken build in
history, one line-number drift the peer missed, and everything else verified clean. 2 findings folded, 0
disputed.**


### Round 14 (rotation seat: Closing Argument) - folded; do NOT re-raise

Four of six seats reported no new findings. The Literal Implementer found a much larger instance of the
drift round 13 had caught, and the rotation seat - asked to argue that the review should STOP, then attack
its own case - predicted precisely that class before it was found.

- **CRITICAL, and it is round 13's finding at forty times the magnitude: Task 5b's line numbers were wrong
  by 198 lines, and following them would have destroyed the code Task 4 had just written.** Round 13 fixed
  the +5 from Step 4b's expanded signature. It missed that Step 5 replaces a 10-line region with a
  **203-line** one. MEASURED by counting both fenced blocks: total shift below `PerceptionManager.cs:848`
  is **+198**, so `AllMaskRectsAsync` sits at ~`:1097-1112` and its XML doc at ~`:1089-1096`. An executor
  replacing `:899-914` would overwrite the dead centre of the 203-line mask walk it had written one task
  earlier, leaving a file that neither compiles nor resembles either version.
  ⚠ The peer computed +193 and the driver had earlier computed +5. **Each had exactly half the answer**:
  the peer missed Step 4b's signature expansion, the driver missed Step 5's block. The true figure is the
  sum, and neither party would have got there alone.
  Fixed by ANCHORING Task 5b on the member declaration rather than on numbers, giving both the pre-Task-4
  and post-Task-4 figures explicitly as orientation, and adding a standing rule at the top of the plan:
  every line number in this document is stated as of the BRANCH POINT, so any task after Task 4 that names
  a line in that file is naming a number that no longer exists.
- **Driver's own sweep after the fold:** every `PerceptionManager.cs` line reference in a task AFTER Task 4
  was enumerated and dispositioned. Task 7's `:210` and its comment lines are all ABOVE `:848` and so
  unaffected - verified, not assumed. The two survivors are now explicitly qualified: Task 5b's Files entry
  names the member, and the `:852` citation is marked as a branch-point line, since it supports a
  MEASUREMENT claim about the code as it exists today rather than an edit target.

**The Closing Argument's verdict, recorded because it is the answer to the question this review has been
circling:** the plan's CODE defects are exhausted; what remains fragile is its meta-level scaffolding -
docstrings, ledger claims, and sequential coupling between tasks. It made the case to stop (every finding
since round 11 has been a document-maintenance artifact created by the review process itself) and then
attacked it: a plan that enforces byte-matching STATE-VERIFY blocks and exact line numbers precisely to
remove executor judgement cannot dismiss line-number drift as clerical, because a brittle instruction set
is a failing one. It named "sequential execution traps" as what it would still expect to find - and then
found one in the same round.

**PANEL VERDICT - round 14: REJECT-then-fold. One CRITICAL sequential-execution trap that would have
destroyed the previous task's work, found by combining two half-answers. 1 finding folded, 1 sweep clean.**


### Round 15 (rotation seat: Coupling Cartographer) - **CLEAN**

**All six seats reported no new findings, and the rotation seat's map came back ALL-SAFE.**

The seat was created for the one class that had produced every finding since round 11 and that BOTH
mechanical checks are structurally blind to: one task's edit invalidating a later task's assumption. It
mapped, per task, what each changes about the repository - line offsets, symbol names, signatures, file
existence, test counts, staged-file state - and which later task depends on that thing being as it was.
Three such couplings had been BROKEN across this review (rounds 6, 12, 14). It marked every one of them
SAFE, naming the specific mechanism that saves each: Task 9 anchors on `ResolveFocusedWindowAsync` rather
than a line; Task 5a's targets in `ScreenshotTools.cs` are immune to Task 4's +198; Task 5b anchors on the
`AllMaskRectsAsync` declaration; Task 7's `:210` sits above the drift; Task 9's `:85` sits above Task 8's
insertions; and 5a's deliberate build-break is caught by 5b before any gate.

**The driver's independent coupling map, built in parallel without seeing the peer's, reached the same
conclusion** and turned up exactly one item: a prose note in Task 5a still said `AllMaskRectsAsync` returns
`.Rects` / `.Escalated`, which round 10's rename had made `.Escalations`. Fixed.

**One clarification added AFTER the clean verdict, and it does not reopen it:** Task 6 is the only task
whose own Step 2 shifts its own Step 3's line number (adding a `using` moves `WatchTools.cs:30` to `:31`).
The standing rule already covers this and Step 3 quotes the content, so the peer correctly marked it SAFE;
the note simply says so at the site, because this class has bitten three times at larger scale. A
documentation clarification of a coupling already marked safe is not a mechanism change.

**PANEL VERDICT - round 15: GREEN. No live challenge from any seat. The review's stop condition is met.**

⚠ **REVIEW-ONLY BREACH, recorded rather than glossed:** the peer wrote `check.csx` to the REPOSITORY ROOT,
outside the `.clavity/scratch/` directory every payload names. Diffed after the consult, as the safety
envelope requires: **no tracked file was touched**, and the file was a single line -
`new Rectangle(100, 50, 0, 30)` printing `IsEmpty` and `Width`. So the peer was EMPIRICALLY VERIFYING the
touching-edge degenerate claim this plan folded in round 6, which is the behaviour one wants; only its
location was wrong. Deleted. It has no bearing on what was folded, because round 15 folded nothing - every
seat reported no new findings.

---

## Final disposition

**GREEN after 15 rounds.** Fourteen rounds of REJECT-then-fold, ~85 findings, then a clean round.

**What the review actually bought, stated plainly.** Six of the findings were leaks that would have shipped
under a green suite - the missing root bound (an all-black screenshot returned successfully), the
maximized-window bleed defeating the geometric guard, the empty yardstick dropping every mask, a raw
exception becoming a silent skip on full-desktop capture, that same leak moved one frame up by its own fix,
and a test that CODIFIED a leak as correct behaviour. Three were executability halts that would have
stopped the rollout partway. One was a fix this plan's own ledger claimed had been applied and which had
never been written to the file.

**The pattern worth keeping.** In six rounds the CRITICAL was an edge cut by the PREVIOUS round's fix.
Folding without re-running would have shipped a defect nearly every time. And the two mechanical checks
this review built - diffing STATE-VERIFY blocks against disk, and verifying every ledger claim against the
artifact - caught real defects but were structurally blind to the coupling class, which needed a panel
every time.

**What remains OPEN, and is the operator's to decide, not the panel's:**
1. A benign element dying mid-scan now refuses the whole capture. The operator chose MEASURE FIRST: whether
   `GetParent` on a stale UIA element throws was asserted by the peer and never measured. One Desktop
   experiment settles it.
2. A window UIA cannot BIND - an elevated one - is skipped and photographed anyway on full-desktop capture
   (AB-9). The fix is a policy choice with real cost; options (a), (b) and (c) are stated in OPEN #2.

Neither blocks execution of the plan as written; both are recorded so they cannot be mistaken for
oversights.
