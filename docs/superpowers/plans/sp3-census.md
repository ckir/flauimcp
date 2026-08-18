# SP3 Task 1 — The census

Date: 2026-07-31. Every read of a content-bearing UIA property (`.Name`, `GetText`/`DocumentRange`/
`GetSelection`, `Patterns.Value`/`LegacyIAccessible`, `GetCurrentPropertyValue`) in `src/`, at HEAD on
`sp3-per-field-redaction`, classified `egress` / `identity` / `neither` per spec
`docs/superpowers/specs/2026-07-30-sp3-per-field-redaction-design.md` §3.1 / §7.2.1.

**This IS the initial content of the two closed lists** the Task-12 Roslyn sweep will allowlist against:
the egress-accessor list (Task 6 targets) and the identity-reader list (reads via `RawForIdentity`).
Six spec §3.1 sites (A1, A2, A3/D4, C1, D2, D3) are cited by their **redaction-decision line**, which is
not itself a literal regex hit of the four sweep patterns — those lines are added as explicit rows so
every one of the twelve sites is traceable in this table (see §3 of the accompanying report for the
full reconciliation). Two genuinely new findings not on the spec's twelve-site list are marked
**NEW** below.

## Table

`file:line | member | property | class | disposition`

### `.Name` sweep (31 literal matches)

| file:line | member | property | class | disposition |
|---|---|---|---|---|
| WindowManager.cs:610 | ResolveFocusedWindowAsync | `focused.Properties.Name` | neither | window title, not element content (canonical "neither" example) — flows to `FocusedElementInfo.Title` same as any window-identifying label |
| Watch/WatchPump.cs:247 | LiveEventSourceReader.Name (getter) | `_el.Name` | egress | **NEW** — raw supplier for WatchPayloadBuilder's A4 redaction decision; the interface property itself performs no redaction and has no classifier touchpoint, only `IsPassword` (WatchPump.cs:244-245) gates it downstream |
| Watch/WatchPump.cs:267 | LiveEventSourceReader.MintRef | `_el.Name` | identity | feeds `ElementDescriptor` for event-ref re-resolution (BC-1); comment confirms "RAW -> descriptor (never redacted)" |
| Watch/WatchPayloadBuilder.cs:34 | WatchPayloadBuilder.Build | `reader.Name` | egress | **spec A4** — `name = reader.IsPassword ? "[REDACTED]" : reader.Name` |
| Perception/WaitCoordinator.cs:88 | WaitCoordinator.Matches | `n.Name` | egress | **spec D1** — `by:"name"` compares `n.IsPassword ? "[REDACTED]" : n.Name` |
| Perception/WaitCoordinator.cs:109 | WaitCoordinator.Signature | `n.Name` | identity | builds an internal per-poll stability fingerprint string; the string itself is never returned to the caller, only its equality across polls drives `stableCount` — closest fit is identity (never leaves process) though it is not a ref-resolution/descriptor-key case; see report surprises |
| Perception/RefRegistry.cs:184 | ResolveDescriptor | `d.Name` | identity | pre-verified (spec §7.2.1 starting list) — Lenient re-walk fallback key |
| Perception/RefRegistry.cs:185 | ResolveDescriptor | `d.Name` | identity | pre-verified — `cf.ByName(d.Name)` re-walk |
| Perception/RefRegistry.cs:304 | ResolveStrict | `d.Name` | identity | pre-verified — Strict re-walk fallback key |
| Perception/RefRegistry.cs:305 | ResolveStrict | `d.Name` | identity | pre-verified — `cf.ByName(d.Name)` re-walk |
| Perception/RefRegistry.cs:337 | FastPathMatches | `cached.Name`, `d.Name` | identity | pre-verified — cached fast-path compare (two occurrences, one line) |
| Perception/SnapshotEngine.cs:73 | Build (local `Visit`) | `el.Name` | identity | pre-verified — "the node builder's raw read"; feeds both the descriptor (BC-1) and `SnapshotNode.Name` (stored raw; redaction deferred to each wire site per the :87-92 comment) |
| Perception/SnapshotEngine.cs:139 | FormatNode | `n.Name` | egress | **spec B1** — `shownName = n.IsPassword ? "[REDACTED]" : n.Name` |
| Perception/SnapshotEngine.cs:156 | IsInteractiveNode | `n.Name` | neither | boolean emptiness test only (`!string.IsNullOrWhiteSpace(n.Name)`) for post-hoc node-classification stats; exposes no content, not even via IsPassword-conditioned branching |
| Perception/SnapshotEngine.cs:186 | SupportedPatterns | `c.Name` | neither | `c` is a `(string Name, Func<bool> Supported)` tuple of hardcoded pattern labels ("Invoke","Value",…) — not a UIA element property at all |
| Perception/PerceptionManager.cs:186 | ResolveSelectorOnSta | `el.Name` | identity | pre-verified ("descriptor keys") — feeds both the redacted match name (line 187, see below) and `h.RawName` in the minted descriptor |
| Perception/PerceptionManager.cs:322 | ReadGridCell | `cell.Name` | egress | **spec A1 family** — Value-pattern-empty fallback read, same member as the redaction guard at :315/317 |
| Perception/PerceptionManager.cs:515 | FindAsync (local `Build`) | `query.Name` | neither | `query.Name` is the caller-supplied search term (`FindQuery.Name`), not a UIA element read |
| Perception/PerceptionManager.cs:516 | FindAsync (local `Build`) | `query.Name` | neither | same — feeds the native `ByName` condition builder from caller input |
| Perception/PerceptionManager.cs:524 | FindAsync (`hasNative`) | `query.Name` | neither | same — caller search criteria |
| Perception/PerceptionManager.cs:595 | FindAsync | (comment) | neither | comment text, not executable code |
| Perception/PerceptionManager.cs:598 | FindAsync | `el.Name` | identity | pre-verified — "raw -> descriptor (re-resolution key)"; feeds `rawName` used at :621 descriptor construction and at :599 (see explicit row below) |
| Perception/PerceptionManager.cs:750 | EvaluateSelectorValueAsync | `el.Name` | egress | **spec D3 family** — fallback read guarded by the IsPassword short-circuit at :746 |
| Perception/SnapshotDiff.cs:19 | Identity | `n.Name` | identity | composite identity key for baseline/current diff correlation; comment at :23 confirms "keeps the RAW name (internal, never serialized)" |
| Perception/SnapshotDiff.cs:25 | ShownName | `n.Name` | egress | **spec B2** — `n.IsPassword ? "[REDACTED]" : n.Name` |
| Perception/SnapshotDiff.cs:37 | Subtree | `scope.Name` | identity | same composite-identity purpose as :19, used only to locate the scope's position in the slice |
| Perception/FindQuery.cs:70 | MatchesPostFilter | `_q.Name` | neither | caller-supplied search criteria (`FindQuery.Name`); the `name` parameter compared against it is already redacted by the caller |
| Server/Tools/SnapshotTools.cs:52 | DesktopSnapshotDiff | `a.Name` | neither | reads `DiffDescriptor.Name`, already redacted at construction (SnapshotDiff.cs ShownName); not a live UIA read |
| Server/Tools/SnapshotTools.cs:53 | DesktopSnapshotDiff | `a.Name` | neither | same — `Removed` side of the same already-redacted DTO |
| Server/Tools/FindTools.cs:41 | DesktopFind | `m.Name` | neither | reads `FindMatch.Name`, already redacted at construction (PerceptionManager.cs:599/624-625); not a live UIA read |
| Perception/TerminalTabReader.cs:32 | NameOf | `e.Name ?? ""` | egress | **NEW, not in spec §3.1** — flows unredacted to `TabListing.Title` (`desktop_list_terminal_tabs`) and `Result.TabTitle` (`desktop_read_terminal_tab`); zero touchpoint with `RedactionPolicy`/`SensitivityClassifier` anywhere in this file |

### `GetText`/`DocumentRange`/`GetSelection`/`GetVisibleRanges`/`RangeFromPoint` sweep (22 literal matches)

| file:line | member | property | class | disposition |
|---|---|---|---|---|
| Server/Tools/ContentTools.cs:66 | DesktopGetText | (method decl.) | neither | method name only (`DesktopGetText`), not a property read |
| Server/Tools/ContentTools.cs:80 | DesktopGetText | `GetTextBySelectorAsync(...)` | neither | wrapper call to PerceptionManager; the actual read lives in `ReadText` (below) |
| Server/Tools/ContentTools.cs:83 | DesktopGetText | `GetTextAsync(...)` | neither | wrapper call, same as above |
| Perception/TerminalTabReader.cs:169 | Run | (comment) | neither | comment text |
| Perception/PerceptionManager.cs:100 | RunOnRefReadAsync | (comment) | neither | comment text |
| Perception/PerceptionManager.cs:345 | ReadText | (comment) | neither | comment text |
| Perception/PerceptionManager.cs:365 | ReadText | `tp.GetSelection()` | egress | **spec A2 family** — raw selection read; member guarded by the isPwd short-circuit at :353-354 |
| Perception/PerceptionManager.cs:367 | ReadText | `sel[0].GetText(...)` | egress | **spec A2 family** — raw text-of-selection read |
| Perception/PerceptionManager.cs:369 | ReadText | (comment) | neither | comment text |
| Perception/PerceptionManager.cs:371 | ReadText | (comment) | neither | comment text |
| Perception/PerceptionManager.cs:373 | ReadText | `tp.DocumentRange.GetText(...)` | egress | **spec A2 family** — raw head-read path |
| Perception/PerceptionManager.cs:388 | GetTextAsync | (method decl.) | neither | method name only |
| Perception/PerceptionManager.cs:391 | GetTextBySelectorAsync | (comment) | neither | comment text |
| Perception/PerceptionManager.cs:392 | GetTextBySelectorAsync | (method decl.) | neither | method name only |
| Server/Tools/ClipboardTools.cs:15 | DesktopClipboardGet | `ClipboardAccess.GetTextAsync()` | neither | reads the OS **clipboard**, not a UIA element; explicitly documented as unredactable at this layer ("no redaction possible") — out of the UIA-content scope entirely |
| Interaction/ClipboardAccess.cs:39 | GetTextAsync | (method decl.) | neither | clipboard access, same reason as above; also a method-name match only |
| Interaction/TextRangeInteractor.cs:14 | SetCaret | `DocumentRange.Clone()` | neither | obtains a `TextPatternRange` object for caret positioning; never extracts text content |
| Interaction/TextRangeInteractor.cs:23 | SelectRange | `DocumentRange.Clone()` | neither | same — range object for selection, not content |
| Interaction/TextRangeInteractor.cs:26 | SelectRange | (comment) | neither | comment text |
| Interaction/VerifyReader.cs:14 | (class doc) | (comment) | neither | comment text |
| Interaction/VerifyReader.cs:20 | (const doc) | (comment) | neither | comment text |
| Interaction/VerifyReader.cs:31 | FromElement | `tp.DocumentRange.GetText(...)` | egress | **spec A5** — post-type verification read-back; guarded by the isPwd short-circuit at :24-25 in the same member |

### `Patterns.Value` / `LegacyIAccessible` / `Properties.Value` sweep (11 literal matches)

| file:line | member | property | class | disposition |
|---|---|---|---|---|
| Server/Tools/SnapshotTools.cs:58 | DesktopWaitFor (Description attr.) | (doc string) | neither | tool description text mentioning "ValuePattern→Name→LegacyIAccessible"; not executable code |
| Perception/PerceptionManager.cs:321 | ReadGridCell | `cell.Patterns.Value.PatternOrDefault?.Value` | egress | **spec A1 family** — primary value read, guarded by the isPwd check at :315/317 |
| Perception/PerceptionManager.cs:742 | EvaluateSelectorValueAsync | (comment) | neither | comment text |
| Perception/PerceptionManager.cs:749 | EvaluateSelectorValueAsync | `el.Patterns.Value.PatternOrDefault?.Value` | egress | **spec D3 family** — guarded by the isPwd short-circuit at :746 |
| Perception/PerceptionManager.cs:751 | EvaluateSelectorValueAsync | `el.Patterns.LegacyIAccessible.PatternOrDefault?.Value` | egress | **spec D3 family** — same guard, final fallback |
| Interaction/ValueCapability.cs:17 | CanSetValue | `Patterns.Value.IsSupported`, `.Pattern.IsReadOnly` | neither | capability/read-only flags, not content — no `.Value.Value`/`.ValueOrDefault` content read |
| Interaction/Interactor.cs:26 | SetValue | `Patterns.Value.IsSupported` | neither | capability check only |
| Interaction/Interactor.cs:27 | SetValue | `Patterns.Value.Pattern.IsReadOnly` | neither | capability check (read-only flag), not content |
| Interaction/Interactor.cs:31 | SetValue | `Patterns.Value.Pattern.SetValue(value)` | neither | this is a **write**, not a read of content |
| Interaction/InputGuard.cs:193 | (doc comment) | (comment) | neither | comment text listing the audit allow-list's exclusions |
| Interaction/InputTargeting.cs:43 | ElementIdentityOf (doc comment) | (comment) | neither | comment text |

### `GetCurrentPropertyValue`/`TryGetCurrentPropertyValue` sweep

**0 occurrences in `src/`.**

### Explicit rows for spec §3.1 decision lines not caught by a literal regex match

These six sites are cited by spec §3.1 at the line where the redaction **decision** is made (a ternary or
an early-return), which does not itself contain a literal `.Name`/`GetText`/`Patterns.Value` token — the
decision consumes a local variable, not the UIA property directly. Added so all twelve sites are
traceable in one table, per Step 3 of the task brief.

| file:line | member | property | class | disposition |
|---|---|---|---|---|
| Perception/PerceptionManager.cs:317 | ReadGridCell | (redaction decision) | egress | **spec A1** — `if (isPwd) value = "[REDACTED]"`; sits alongside the raw reads at :321-322 |
| Perception/PerceptionManager.cs:354 | ReadText | (redaction decision) | egress | **spec A2** — `if (isPwd) return new TextReadResult("[REDACTED]", ...)`; sits alongside the raw reads at :365,367,373 |
| Perception/PerceptionManager.cs:599 | FindAsync | (redaction decision) | egress | **spec A3 + D4 (same site)** — `name = isPwd ? "[REDACTED]" : rawName`, consumed at :601 (match) and :624-625 (`FindMatch.Name` output) |
| Perception/PerceptionManager.cs:818 | ResolveWindowCaptureGeometryAsync | `d.Properties.IsPassword`, `d.BoundingRectangle` | egress | **spec C1**, with a caveat — see report §"conflict with the structural-property exclusion": `BoundingRectangle` is generically listed as an out-of-scope structural property, but here it is not read for positional/navigation purposes; it is read conditionally on `IsPassword` specifically to build `CaptureGeometry.PasswordRects`, the pixel-redaction mask consumed by `ScreenshotTools.cs:44`/`FindTextTools.cs:62,106`. **DEF-1** (spec §3.3) is still live at this exact line at HEAD: the `IsPassword` read is raw inside `try/catch {}`, not `IsPasswordOrFailClosed`, so it fails OPEN |
| Perception/PerceptionManager.cs:187 | ResolveSelectorOnSta | (redaction decision) | egress | **spec D2** — `name = isPwd ? "[REDACTED]" : rawName`, feeds `MatchesPostFilter` at :189 |
| Perception/PerceptionManager.cs:746 | EvaluateSelectorValueAsync | (redaction decision) | egress | **spec D3** — `if (IsPasswordOrFailClosed(...)) return (true, null)`; guards the raw reads at :749-751 |

## Summary counts

| Class | Count |
|---|---|
| egress | 21 |
| identity | 12 |
| neither | 37 |
| **Total rows** | **70** |

(31 `.Name` sweep rows + 22 `GetText`-family rows + 11 `Value`/`LegacyIAccessible`-family rows + 0
`GetCurrentPropertyValue`-family rows + 6 explicit spec-decision rows = 70.)
