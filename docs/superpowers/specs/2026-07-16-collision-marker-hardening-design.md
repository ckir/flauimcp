# Collision-Marker Durability Hardening — Design

**Status:** design (pre-plan), revised after panel rounds 1–2 (solo + agy escalation, all findings
measured). Defects came from the 2026-07-16 agy-vs-subagent A/B code review of `ClaudeCollisionRemedy.cs`
+ `CollisionMarker.cs`, each verified against code that exists now.

**HELD-RELEASE INVARIANT:** folds into branch `fix/distribution-live-defects`; commit-only, per item.
`master` stays `2ffd6a1` — **no push/tag/merge**. v0.15.0 stays unreleased but hardened.

---

## Problem

`CollisionMarker.Read(stateDir)` **fails open**: on any I/O error, parse failure, malformed content, or a
future schema version it returns an **empty list** — indistinguishable from "no marker exists". Two
callers are harmed, and a third defect makes the ambiguity easy to trigger:

1. **`Record()` destroys data.** `merged = Read().ToList()`, merge, `File.WriteAllText` (overwrite),
   return `null` (= success). If the old marker was *unreadable* (not absent), prior entries are silently
   lost and success is reported → a plugin we disabled is stranded forever.
2. **`Restore()` strands silently.** `if (Read().Count == 0) return null;` — a corrupt marker reads as
   empty → uninstall returns success, never warns, never offers recourse, never deletes the corrupt file.
3. **`File.WriteAllText` (`CollisionMarker.cs:73`) is non-atomic** — an interrupted installer write is the
   realistic trigger for the corrupt file behind (1)/(2).

Plus two smaller independent defects: **(4)** `Restore()` leaks the internal `-1`/`-2` sentinels in its
`enable`-failure message; **(5)** `Apply()`'s R5 "assuming you did" check tests `recorded` but not
`justDisabled`, so a duplicate CLI row misattributes our own disable to the user.

### Warning visibility (measured)

`Record`/`Apply` warnings are written to the durable `install.log` (`InstallStatus.LogName`) and surfaced
by `flaui-mcp status` (`InstallStatus.DescribeCollisions`); uninstall warnings go to
`uninstall-warnings.log` + the Inno uninstall MsgBox. A persisted warning is **not** invisible — so a
fail-safe that surfaces a warning is sound. **But** `status` currently calls `Read()`, which returns
empty on a corrupt marker, so today it would *mask* corruption (round-2 SC) — fixed in §7.

### Why this design (process note)

AGY-FIRST killed my first two instincts (agy's minimal-exceptions overturned by measurement — `Read` has
4 callers incl. `status` + a pinned never-throws test). Round 1 found the repo's atomic
backup-then-write helper `JsoncFile.Save`; round 2 showed reusing it wholesale **backs up on every write**
(state-dir `.bak` litter) and is `void` (can't control the backup) — so this spec uses a **tailored
atomic write** that follows `JsoncFile`'s tmp→`File.Move` *pattern* but backs up **only** a `Corrupt`
file.

---

## Goals (checkable success criteria)

1. Pre-existing marker holds entry A; the file is then corrupt; `Record(B)` → A's bytes are preserved
   (renamed to a `.bak`), B is recorded, and — because the record **succeeded** — the caller still
   promises re-enable (no false suppression).
2. Corrupt marker at uninstall → `Restore()` surfaces a warning + manual recourse and does **not** return
   silent success.
3. An interrupted write never leaves a file that **reads as a valid marker** (a torn write fails to parse
   → `Corrupt`; the atomic `File.Move` makes the replace all-or-nothing).
4. A marker whose `version` is **greater** than this build understands is left untouched — never
   downgraded or clobbered.
5. No internal sentinel (`-1`/`-2`) is shown to the user.
6. R5 never fires for an entry we disabled earlier in the *same* `Apply()` run.
7. `flaui-mcp status` reports a corrupt/future marker rather than masking it as "nothing disabled".

Non-goals (settled): mutation-as-detector, disable-not-uninstall, the marker's on-disk location, and the
sibling-probe read design. **No `fsync`, no unique-`.tmp` name, no AV-retry loop** — this matches the
repo's existing `JsoncFile.Save` durability posture rather than inventing stricter I/O in one spot
(rounds 1–2 A7/S1/DC). **No JSON-passthrough** of unknown fields — we own the v1 schema and add none;
a future field must bump `version` (→ FutureVersion protects it).

---

## Design

### 1. `MarkerState` + `ReadState` — a four-way partition

```
enum MarkerState { Absent, Corrupt, FutureVersion, Present }

(MarkerState State, IReadOnlyList<DisabledEntry> Entries) ReadState(string stateDir)
```

Classification (ReadState never throws — every failure collapses to `Corrupt`):
- File does not exist → `(Absent, [])`.
- **File-level** structural failure — read throws, JSON parse throws, root not a `JsonObject`, `version`
  missing / not a number / `< 1`, or `disabled` missing or not a `JsonArray` → `(Corrupt, [])`. `version`
  is read as a **`double`** (via `GetValueKind()==Number` then `GetValue<double>()`), not `int`/`long`, so
  neither a large integer nor a fractional future version (`2.1`) overflows or throws into `Corrupt`
  (round-2 PP, round-3 PP).
- Root is an object with a numeric `version > 1` → `(FutureVersion, [])` (goal 4). This is honored on the
  version alone; requiring a `disabled` array would clobber a v2 that *renames* it (round-2 MG remedy
  rejected). A stray `{version:999}` is thus a *recoverable* lock-out — surfaced by `status` (§7) and
  removable by the user, per the warning in §3.
- `version == 1` and `disabled` is an array → `(Present, entries)`. Entries are projected **per-entry,
  each read inside its own `try/catch`** — a bare `(string?)e["id"]` cast **throws** on a numeric/boolean
  value (it does NOT return null), so the guard MUST either catch per-entry or gate every field on
  `e["id"]?.GetValueKind() == JsonValueKind.String`. Without that explicit guard a single wrong-typed
  field escapes to `ReadState`'s outer catch and collapses the whole file to `Corrupt`, defeating the
  targeted-drop design (round-3 LI). Any malformed field (non-object node; null/blank/non-string/wrong-
  typed `id` or `scope`; wrong-typed `projectPath`) drops **that one entry** only (round-2 R2-3, round-1
  A2/A4). `projectPath` may legitimately be null (user scope) — null `projectPath` is kept, not dropped.
  A `disabled` array of all-dropped entries yields `(Present, [])`.

### 2. `Read()` — safe projection wrapper (contract preserved)

```
IReadOnlyList<DisabledEntry> Read(string stateDir)
{
    var (state, entries) = ReadState(stateDir);
    return state == MarkerState.Present ? entries : Array.Empty<DisabledEntry>();
}
```

The explicit `Present`-gate keeps `Read` never-throws / empty-on-anything-non-Present, so the pinned
`A_corrupt_marker_reads_as_empty_and_never_throws` theory (all five inputs classify as `Corrupt`) and the
~20 other `Assert.Empty(Read(...))` assertions stay green.

### 3. `Record()` — tailored atomic write; back up only a `Corrupt` file

```
Record(stateDir, entries) -> string?      // null = recorded (promise holds); non-null = NOT recorded
    if entries.Count == 0: return null                       // never write an empty marker

    (state, existing) = ReadState(stateDir)
    if state == FutureVersion:                                // goal 4 — leave it, suppress the promise
        return "the restore record at {path} was written by a newer flaui-mcp and was left unchanged;
                this install's disable was NOT recorded. If you did not expect this, remove {path}."

    try:
        if state == Corrupt:
            // preserve the unreadable bytes for forensics, unless absurdly large (sanity cap), then
            // start from an empty baseline. This is SILENT — recovery succeeds, so the user only needs
            // the normal "will be re-enabled" promise, not a scary corruption notice (round-2 AB).
            if fileLength <= BackupSizeCap (1 MB): move old aside to a COLLISION-FREE ".bak-{utc}-{n}"
                (a same-second second corruption must not throw on a duplicate name — use a uniquifying
                suffix or overwrite; and do it best-effort so a backup hiccup does not fail a recoverable
                record — round-3 R3-1/R3-2)
            else: (leave nothing; the oversized garbage is discarded by the overwrite)
            baseline = []
        else:
            baseline = state == Present ? DedupBySameEntry(existing) : []   // NOT record .Distinct()!

        merged = baseline + entries, deduped by SameEntry
        WriteMarkerAtomically(stateDir, BuildJson(version:1, merged))       // CreateDir; write .tmp; File.Move
        return null                                                         // success (incl Corrupt) -> promise holds
    catch (Exception e):
        return "disabled a conflicting plugin but could NOT record it at {path} ({e.Message}) —
                uninstalling flaui-mcp will not re-enable it automatically."
```

`WriteMarkerAtomically`: `Directory.CreateDirectory(stateDir)`; `File.WriteAllText(path + ".tmp", json)`;
`File.Move(tmp, path, overwrite: true)`. No happy-path backup (atomicity alone protects a `Present`
overwrite — the merged marker is a superset of the old), so no litter on the common path.

`DedupBySameEntry` uses `SameEntry` (case-insensitive on id/scope/path), **not** `record` `.Distinct()`
(which is ordinal and would miss `C:\proj` vs `C:\Proj`, resurrecting the double-enable bug — round-2 LI).

Key contract (round-2 AB, Critical): `Record` returns **null on every success, including `Corrupt`
recovery** — because it *did* record, so `Apply`'s promise-suppression (`recordWarning is not null`,
`ClaudeCollisionRemedy.cs:133-143`) must NOT fire. Only a genuine write failure or a `FutureVersion`
refusal returns non-null.

### 4. `Restore()` — state-aware guard, and sweep the `.bak` on consume

Replace the opening `recorded = Read(); if (recorded.Count == 0) return null;` with a `ReadState` switch:
- `Absent`, or `Present` with zero entries → `return null`.
- `FutureVersion` → warn "the restore record at {path} was written by a newer flaui-mcp; left in place,
  not acted on" and **keep** the marker.
- `Corrupt` → warn + recourse and **keep** the marker: "the restore record at {path} is unreadable, so any
  conflicting plugin(s) we disabled may still be disabled and could not be re-enabled automatically. To
  check: `claude plugin list`, and re-enable `flaui-mcp@flaui-mcp` wherever it is disabled." (Mirrors the
  existing `TryReadInventory`-failure guard the old early-return bypassed.)
- `Present` → the existing per-entry restore loop, then **consume**: delete `PathIn(stateDir)`.

**`.bak` sweep runs UNCONDITIONALLY** on every `Restore` (all four branches), decoupled from the active
marker's state — best-effort glob-delete `disabled-plugins.json.bak-*` in `stateDir` (per-file `try/catch`,
swallow, so an AV-locked `.bak` never fails the uninstall — round-3 R3-2). **Flow-control mandate
(round-4 Regression):** the four state bullets above each `return`/short-circuit, so the sweep must NOT be
a bottom-of-method statement after them — an `if (Absent) return null;` would make it unreachable and
strand `.bak`s on every early-return path. Structure it so the sweep always runs: run the sweep FIRST
(it is independent of the restore logic), or compute the warning in the switch without returning and sweep
before the single `return`, or wrap the switch in `try { … } finally { Sweep(); }`. If the sweep were
skipped on `Absent`/`Corrupt`/`FutureVersion`, old backups would orphan in `%LOCALAPPDATA%` forever
(round-3 RV). The sweep is scoped to the marker's own backups by exact prefix, nothing else. (On
`Corrupt`/`FutureVersion` the active marker itself is kept; only stale `.bak`s are swept.)

### 5. Sentinel translation in `Restore()`

Reuse `TryReadInventory`'s sentinel→text mapping (`NotFound`→"could not be run", `TimedOut`→"timed out",
else the raw code) in the `enable`-failure message, so `Restore` never prints `claude exited -2`.

### 6. R5 fix

`Apply()`'s "assuming you did" guard consults `recorded.Concat(justDisabled)` instead of `recorded`.

### 7. `status` reads state, not just entries

`InstallStatus.DescribeCollisions` switches on `ReadState` instead of `Read()`: `Present` → list entries
as today; `Corrupt` → "a restore record exists but is unreadable — re-enable `flaui-mcp@flaui-mcp`
manually if a driving-skill copy is still disabled"; `FutureVersion` → "a restore record written by a
newer flaui-mcp is present"; `Absent` → null. Without this, `status` would report "nothing disabled" over
a corrupt marker (goal 7).

---

## Error handling summary

- `CollisionMarker` keeps "never throws"; `ReadState` collapses all failures to `Corrupt`.
- Read path fail-safe (empty on non-`Present`); write path non-destructive (backup only a `Corrupt` file,
  atomic overwrite otherwise, refuse a `FutureVersion`); consume path refuses to silently delete a
  `Corrupt`/`FutureVersion` marker and sweeps its own `.bak-*`.
- A transient unreadable (AV lock) → `ReadState` = `Corrupt`; the rename/write then throws on the lock →
  `catch` returns the record-failure warning; nothing is destroyed; next run retries. No permanent wedge,
  no retry loop (repo convention).

## Testing

- `ReadState`: `Absent`; `Present` (valid v1); `Corrupt` for "", non-JSON, "[]", "{}", `{version:1}`,
  `{version:1,disabled:"x"}`; `FutureVersion` for `{version:2,disabled:[…]}` **and** `{version:999}`
  (no disabled — still FutureVersion, recoverable); `Present` where a null/blank/wrong-typed `id`/`scope`
  **and** a wrong-typed `projectPath` (a numeric `{id:123}`, which *throws* on a bare cast) are each
  dropped while valid siblings survive; a **fractional** future version `{version:2.1}` classifies
  `FutureVersion`, not `Corrupt` (guards the `double` parse).
- `Read` wrapper stays empty for every non-`Present` state (extend the pinned theory with `version:2`);
  the ~20 existing `Assert.Empty(Read(...))` assertions stay green.
- `Record`: `Present` → merges + preserves prior entries (existing "C:\a preserved, C:\b merged" test
  stays green), no `.bak` produced on the happy path, and returns null; `Corrupt` → old bytes renamed to
  a `.bak-*`, fresh marker written with the new entry, and **returns null** (so `Apply` still promises
  re-enable); an oversized corrupt file is discarded (no `.bak`) and still recorded; `FutureVersion` →
  file byte-for-byte unchanged, returns a non-null warning; a hand-edited case-variant duplicate baseline
  is deduped (no double entry).
- `Apply` integration: a `Corrupt`-recovery `Record` still emits the "(they will be re-enabled…)" promise
  (guards round-2 AB regression).
- `Restore`: `Corrupt` → warns + recourse, no `enable`, marker kept; `FutureVersion` → warns, no `enable`,
  kept; `Absent` → null; `Present` → existing behavior, marker consumed.
- `.bak` sweep pinned on **all four** branches (round-4 Test-Oracle): seed a `disabled-plugins.json.bak-*`,
  drive `Restore` in each of `Absent`/`Corrupt`/`FutureVersion`/`Present`, and assert the `.bak` is gone
  every time — otherwise a sweep that regressed onto an early-return path would stay green.
- `status`: `Corrupt`/`FutureVersion` marker → `DescribeCollisions` returns a non-null description (goal 7);
  a `Present` marker → `DescribeCollisions` output is **byte-identical to today's** (round-4 Regression —
  do not perturb any existing status test).
- A stale leftover `<path>.tmp` is ignored by `ReadState` and does not corrupt the next `Record`.
- Sentinel: an `enable` returning **both** `TimedOut` (→ "timed out", not "-2") **and** `NotFound` (→
  "could not be run", not "-1") is translated (goal 5) — pin both sentinels, not just one (round-4).
- R5: two rows resolving to the same entry (fake runner) → the second does not warn (goal 6).

Goal 3 has no crash test (you cannot unit-test a power loss); it is guaranteed by `File.Move` atomicity +
`ReadState` rejecting a torn file as `Corrupt`, and partially exercised by the stale-`.tmp` test.

## Files touched

- `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs` — `MarkerState`, `ReadState`, safe `Read` wrapper,
  `Record` (tailored atomic write, Corrupt-only backup w/ size cap, FutureVersion refuse, SameEntry
  dedup, null-on-success), `.bak` sweep helper used by consume.
- `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` — `Restore` state-aware guard + `.bak` sweep on
  consume, sentinel translation, R5 `Concat`.
- `src/FlaUI.Mcp.Server/Install/InstallStatus.cs` — `DescribeCollisions` via `ReadState` (goal 7).
- Tests: `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs`,
  `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs`,
  `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs`, and a `status`/`InstallStatus` test.

## Sequencing (independent commits)

1. `MarkerState` + `ReadState` (four-way, long version, per-entry drops) + safe `Read` wrapper — existing
   tests stay green. 2. `Record` tailored write (Corrupt backup + size cap, FutureVersion refuse, SameEntry
   dedup, null-on-success). 3. `Restore` state-aware guard + `.bak` sweep. 4. `status` via `ReadState`.
   5. Sentinel translation. 6. R5 `Concat`. Items 5–6 are one-liners and may share a commit.

## Panel ledger (folded — do NOT re-raise)

**Round 1:** A1 wedge → backup-then-write (no wedge); "reaches nobody" corrected. A2/A4 entry-level drops.
A3 dedup baseline. A5 no persistent corrupt marker. A6 CreateDirectory. A7 fsync rejected (repo
convention). S1 fixed `.tmp` (repo convention). S2 goal-3 argued + stale-`.tmp` test. S3 FutureVersion
warning non-null. S5 Present-empty → null. S6 `File.Move` atomicity noted.
**Round 2:** AB(crit) Corrupt-recovery returns **null** so the re-enable promise is not suppressed. SC
`status` via `ReadState` (goal 7). Blindspot `.bak` swept on consume. LI `SameEntry` dedup (not
`.Distinct()`). PP `version` parsed as `long`. R2-1/R2-4 tailored writer (Corrupt-only backup, silent, no
litter). R2-3 per-entry projection catches any field error. RV(minor) backup size cap. MG remedy rejected
(require-disabled breaks goal 4; recoverable warning instead). DC AV-retry rejected (repo convention). CA
reserialization-lossy noted (we own the schema).
**Round 3:** PP `version` parsed as **`double`** (not `long`) — a fractional future version must not throw
into `Corrupt`. LI per-entry projection MUST use explicit `try/catch`/`GetValueKind` (a bare cast throws,
not returns null). RV `.bak` sweep runs **unconditionally** on uninstall (not just `Present`). R3-1 Corrupt
`.bak` name collision-free (same-second). R3-2 backup move + sweep best-effort. **Boundary Smuggler
cwd-hijack** (bare `claude` + marker-controlled cwd → `CreateProcess` searches cwd before PATH): mechanism
real, but **measured non-exploitable** at `installer/flaui-mcp.iss:13 PrivilegesRequired=lowest` (uninstaller
runs as the user; no elevation, no escalation) — **user-accepted risk, out of scope**, do not re-raise.
**Round 4 (design DRY per agy):** Regression — the `.bak` sweep must run BEFORE `Restore`'s early
returns (or in `try/finally`), else it is unreachable on `Absent`/`Corrupt`/`FutureVersion` (flow-control
mandate added to §4). Test-Oracle — pin the `.bak` sweep on all four branches, pin BOTH `-1`/`-2` sentinel
translations, and pin the `Present`-case `status` output as byte-identical. No new design defects; agy
verdict "core design fully sound."
