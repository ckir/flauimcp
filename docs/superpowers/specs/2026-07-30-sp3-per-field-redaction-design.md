# SP3 — Per-field redaction (ROADMAP item 9) — design

**Status:** USER-APPROVED design, 2026-07-30. Awaiting adversarial panel.
**Base:** `master` @ `57bc7ef` (SP2 merged). Every file:line below was grep/read-verified at that SHA.

## 1. Goal

Guarantee that a sensitive field's text never reaches the wire, across **every** egress path, **without
relying on a developer remembering to add a redaction check at a new call site** — and extend the
definition of "sensitive" beyond the OS `IsPassword` flag via operator-configured rules.

**Checkable success criterion.** A developer adds a new tool that reads an element's `Name` or `Value`
and returns it, forgets redaction entirely, and the system does not silently leak: a test fails. Today
it leaks silently and nothing notices.

## 2. Scope decision and its history

The user chose **full item 9** — the egress architecture (P2) **and** a new sensitivity policy (P1) —
over three narrower alternatives, after being shown a substantive challenge to the premise.

The agy peer argued SP3 should not be built at all: that heuristic sensitivity detection is security
theater, that false positives are disqualifying, and that the app/OS own the semantics of a secret. Two
of its three arguments were accepted and shape this design (§4 excludes value-shape detection; §5.2
scopes rules to a process; §6 makes redactions debuggable). Its third — "you mask the DOM, not the
glass" — was **half-refuted by measurement** and is recorded in §3.3.

**The user's decision is final and is not to be relitigated.** It is recorded here with its dissent so a
later reader does not mistake the dissent for an open question.

## 3. Ground truth — the verified surface inventory

Redaction today is the single predicate `RedactionPolicy.IsPasswordOrFailClosed(Func<bool>)`
(`src/FlaUI.Mcp.Core/Perception/RedactionPolicy.cs:8`), which returns `true` when the read throws, and
is applied **independently, by hand, at twelve sites**. The sites fall into four families by *medium*,
and this classification is the core structural finding of the spec: **no single chokepoint spans them.**

### 3.1 The four families

**(A) JSON payload egress** — the value is serialized into a tool's JSON response.

| # | Site | What it redacts |
|---|---|---|
| A1 | `Perception/PerceptionManager.cs:317` | grid cell value → `"[REDACTED]"` |
| A2 | `Perception/PerceptionManager.cs:354` | `ReadText` → `TextReadResult("[REDACTED]", …)` |
| A3 | `Perception/PerceptionManager.cs:599` | `find` hit name (also a matcher — see D) |
| A4 | `Watch/WatchPayloadBuilder.cs:34` (+ `Watch/WatchPump.cs:244-245`) | watch event name |
| A5 | `Interaction/VerifyReader.cs:24-25` | post-type verification read-back |

**(B) Rendered-text egress** — the value is baked into a text blob that is then carried inside JSON as
one opaque string. A JSON-layer chokepoint **cannot see inside it**.

| # | Site | What it redacts |
|---|---|---|
| B1 | `Perception/SnapshotEngine.cs:139` (`FormatNode`) | the snapshot render's `shownName` |
| B2 | `Perception/SnapshotDiff.cs:25` (`ShownName`) | diff added/removed/changed names |

**(C) Pixel egress** — bounding rectangles are blacked out of a capture before it leaves.

| # | Site | What it redacts |
|---|---|---|
| C1 | `Perception/PerceptionManager.cs:818` builds `CaptureGeometry.PasswordRects` (`:866`, `Perception/TextCaptureGeometry.cs:13`) | consumed by `Tools/ScreenshotTools.cs:44` and `Tools/FindTextTools.cs:62,106` |

**(D) Oracle suppression — NOT egress.** These leak by *answering a question*, not by emitting a value.
No serializer or renderer chokepoint can catch them; they must compare on the redacted form.

| # | Site | Why |
|---|---|---|
| D1 | `Perception/WaitCoordinator.cs:88` | `wait_for by=name` compares `n.IsPassword ? "[REDACTED]" : n.Name` |
| D2 | `Perception/PerceptionManager.cs:187` | the selector walk redacts **before** `MatchesPostFilter(name, enabled)` |
| D3 | `Perception/PerceptionManager.cs:746` | `EvaluateSelectorValueAsync` returns `null` so `until:valueEquals` cannot confirm a guess |
| D4 | `Perception/PerceptionManager.cs:599` | `find` matches on the redacted name (same site as A3) |

Twelve distinct sites; A3/D4 is one site serving two roles.

### 3.2 The whole-window floor (already shipped, unchanged by SP3)

`Perception/PerceptionPolicy.cs:31` `IsDenied(processName)` against a hardcoded credential-store process
set (`:15-28`), enforced at `PerceptionManager.cs:290,436,489,805`, `Watch/WatchService.cs:65`,
`Watch/WatchPump.cs:208`, `Tools/WakeTools.cs:45`, `Tools/ScreenshotTools.cs:35,42`,
`Tools/FindTextTools.cs:60,92`, `Interaction/ActionPolicy.cs:36`. SP3 does not modify the denylist.

### 3.3 Two defects found while verifying, both in scope

**DEF-1 — the pixel path fails OPEN while every text path fails CLOSED.**
`PerceptionManager.cs:818` reads `d.Properties.IsPassword.ValueOrDefault` **raw**, inside `try/catch {}`.
All eleven other sites use `IsPasswordOrFailClosed`, which returns `true` on a throw. A provider that
throws on the `IsPassword` read therefore gets its **text redacted and its pixels captured**.

**DEF-2 — full-desktop capture masks nothing.**
`Tools/ScreenshotTools.cs:37` passes `System.Array.Empty<System.Drawing.Rectangle>()`. Full-desktop
capture refuses only when a *denylisted* window is visible (`:33-35`); a password box in an allowed
window is captured unmasked. Impact is low today (password boxes render as dots) but becomes material
the moment rule-based redaction covers plaintext fields — which is exactly what SP3 adds.

DEF-2 is also the half of agy's "you mask the DOM, not the glass" argument that **holds**. The half that
does not: for *window* capture, `ScreenshotTools.cs:44` and `FindTextTools.cs:62,106` consume the same
`PasswordRects` list, so generalizing the policy generalizes the pixel mask through one site (C1).

### 3.4 Comment drift — evidence for the thesis

The comment at `SnapshotEngine.cs:87-92` documents the redaction invariant and carries **five stale
cross-references**, all verified wrong at `57bc7ef`:

| Comment says | Verified actual |
|---|---|
| render `:133` | `SnapshotEngine.cs:139` |
| `RefRegistry.cs:181-182` (Name fallback) | `RefRegistry.cs:184-185` |
| `RefRegistry.cs:334` (cached fast-path Name compare) | `RefRegistry.cs:337` |
| `RefRegistry.cs:206-209` (`Key()` never echoes Name) | `RefRegistry.cs:211-212` |
| `PerceptionManager.cs:281-292` (FindAsync redaction, cited from `:183`) | `PerceptionManager.cs:599` |

Hand-maintained redaction spread across twelve sites drifts. This is the problem SP3 exists to fix, and
these references are corrected as part of it (§8, T7).

## 4. P1 — the sensitivity policy

### 4.1 Signals

| Signal | In? | Precedence |
|---|---|---|
| OS `IsPassword` (fail-closed) | **yes — always on, cannot be disabled** | 1 (wins) |
| Exact `AutomationId` match, operator-configured | yes — opt-in | 2 |
| Regex over `AutomationId` | yes — opt-in | 2 |
| Regex over raw `Name` | yes — opt-in | 2 |
| **Value-shape detection** (Luhn/PAN, JWT, entropy) | **NO — excluded** | — |

**Why value-shape is excluded** (two independent reasons, either sufficient):
1. It requires reading each node's `Value` on the snapshot walk. `ROADMAP.md` defers snapshot/diff
   value-change detection (item 4, scheduled SP4) *precisely because* per-node value reads are too
   expensive on the STA walk. SP3 would spend a budget item 4 owns, before item 4 is scheduled.
2. It is where the false-positive risk concentrates: UUIDs, build hashes and element ids are
   high-entropy strings an agent legitimately needs.

**Why the included signals are free:** `SnapshotEngine.cs` already reads `aid` (used at `:93`, `:95`) and
`name` per node, and the selector walk already reads both (`PerceptionManager.cs:176`, `:186`). The
included signals add **zero** additional COM reads on the hot path.

### 4.2 Rule scoping

A rule is **process-scoped by default**. `processName` is required unless the rule sets `"global": true`
explicitly, which forces the operator to opt into a machine-wide blast radius rather than backing into
one. Rationale: `AutomationId="token"` and `Name="Key"` are common developer nouns; a global rule on
either would blind the agent to unrelated UI across the whole desktop.

### 4.3 Default posture

**Off by default.** With no rules configured the classifier is behaviourally identical to today
(`IsPassword` only). No existing deployment changes behaviour until an operator writes a rule file.

## 5. Contracts (concrete — no part of this is left to the plan)

### 5.1 Rule file

Enabled by a new server option `--redaction-rules <path>`; absent ⇒ feature off. JSON:

```json
{
  "version": 1,
  "rules": [
    { "name": "vault-token",   "processName": "devenv", "automationId": "tokenBox" },
    { "name": "card-fields",   "processName": "chrome", "automationIdPattern": "^(cc|card)_" },
    { "name": "ssn-anywhere",  "global": true,          "namePattern": "(?i)social security" }
  ]
}
```

- `version` — required, must equal `1`. An unknown version is a config error.
- `name` — required, non-empty, unique across rules. Appears on the wire as `rule:<name>`.
- `processName` — matched case-insensitively against `Process.ProcessName` (no `.exe`), mirroring
  `PerceptionPolicy.cs:13-14`'s documented convention. Required unless `global: true`.
- `global` — optional bool, default `false`. `true` with a `processName` present is a config error.
- Element predicates: `automationId` (exact, Ordinal), `automationIdPattern`, `namePattern` (both .NET
  regex, compiled at load with a 100 ms `RegexOptions.NonBacktracking`-equivalent timeout).
- **At least one element predicate is required**; a rule with none would redact an entire process.
- A rule matches iff **all** its specified predicates match (AND).

### 5.2 The classifier

```csharp
public enum RedactionSource { None, Os, Rule }

public readonly record struct Sensitivity(bool Redact, RedactionSource Source, string? RuleName)
{
    public static readonly Sensitivity Visible = new(false, RedactionSource.None, null);
}

public sealed class SensitivityClassifier
{
    /// IsPassword-only. The behaviour of every deployment that configures no rules.
    public static SensitivityClassifier OsOnly { get; }

    public static SensitivityClassifier Load(string path);   // throws RedactionConfigException

    /// readIsPassword is a thunk so the OS check stays fail-closed and is evaluated FIRST.
    public Sensitivity Classify(string? processName, string? automationId, string? rawName,
                                Func<bool> readIsPassword);
}
```

**Precedence is strict:** `readIsPassword` is evaluated first through
`RedactionPolicy.IsPasswordOrFailClosed`; if true the result is `(true, Os, null)` and no rule is
consulted. Rules are evaluated in file order; the first match wins and supplies `RuleName`.

### 5.3 Wire representation

The literal token stays **`"[REDACTED]"`** on every surface — existing tests pin it and agents
string-match it. Sensitivity provenance is carried **adjacently**, never by changing the token:

- **JSON payloads (family A):** a new optional field `redactedBy`, emitted only when a value was
  redacted. Values: `"os"` or `"rule:<name>"`. Absent ⇒ nothing was redacted.
- **Snapshot render (B1):** the existing bracketed state list (`SnapshotEngine.cs:134-138`, which already
  carries `enabled`/`focusable`/`focused`/`selected`) gains `redacted:os` or `redacted:rule:<name>`.
- **Diff (B2):** unchanged token; no provenance field (a diff line names a node, and the caller can
  snapshot it for provenance). Stated explicitly so a reviewer does not read it as an omission.
- **Pixel (C1):** no provenance — a blacked-out rectangle carries no field identity.
- **Oracle suppression (D):** no wire change. These paths already report "no match" and must continue
  to be indistinguishable from a genuine miss; adding provenance here would **re-create the oracle**.

`redactedBy` exists because a false positive is the top risk of this feature and is otherwise
undebuggable: an operator cannot trace a redaction back to the misfiring rule, and an agent cannot tell
an OS-declared field (permanent) from an operator rule (fixable).

### 5.4 Failure semantics

A missing `--redaction-rules` path, malformed JSON, unknown `version`, duplicate rule `name`,
uncompilable regex, a rule with no element predicate, or `global` together with `processName` ⇒ the
server **refuses to start**, with the offending rule name/index in the message.

Rationale: an operator who authored a rule file is depending on a shield. Starting with an empty rule
set silently voids protection they explicitly asked for; redacting everything is unusable and hides the
cause. A loud exit is the only defensible posture for a security mechanism. This is a deliberate
departure from `RedactionPolicy`'s per-read fail-closed idiom, which governs a *runtime read*, not a
*configuration error*.

## 6. P2 — architecture: classify once, apply four times

```
                      SensitivityClassifier.Classify()      <- the ONE decision
                                   |
        +--------------+-----------+-----------+--------------+
        |              |                       |              |
   (A) JSON       (B) rendered text       (C) pixels      (D) oracle
   ToolResponse   SnapshotEngine /         mask-rect       compare on the
   payload build  SnapshotDiff             builder         redacted form
```

**Why not one chokepoint.** `ToolResponse.Ok` (`src/FlaUI.Mcp.Server/Tools/ToolResponse.cs:14`) with its
single `JsonSerializerOptions` (`:12`) is a genuine funnel for family A. It cannot serve B (the render is
already a string by the time it arrives), C (not JSON), or D (a comparison, not a payload).

**Why not a Roslyn egress analyzer** (agy's initial rank-1, which it withdrew on challenge): tracing
taint across `async`/`await`, `Task.Run` STA hops and LINQ projections is brittle, and an analyzer that
silently loses taint manufactures false confidence — worse than none.

**Why not a reflection sweep over tool return types** (agy's revised proposal): **measured impossible
here.** 48 of the 49 tools return `Task<string>` — already-serialized JSON — and the 49th
(`ScreenshotTools.cs:18`) returns `Task<CallToolResult>`. There are no DTO properties to crawl.

**The guarantee mechanism (§7.2) is therefore a pinned surface inventory, not type reflection.**

## 7. Testing

### 7.1 Behavioural pins (headless where possible)

- Classifier unit tests: precedence (`Os` beats `Rule`), fail-closed on a throwing `IsPassword`, AND-ing
  of predicates, process scoping, first-match-wins ordering, `global` handling.
- Config loader: one test per §5.4 rejection reason, each asserting the message names the bad rule.
- Per-family pins that a rule-matched (not `IsPassword`) field is redacted at A1–A5, B1, B2, C1, and is
  **not matchable** at D1–D4.
- DEF-1: a throwing `IsPassword` read yields a mask rect (currently it does not).
- DEF-2: full-desktop capture masks password rects.

### 7.2 The guarantee: `RedactionSurfaceInventoryTests`

A source-level sweep over `src/` for the literal `"[REDACTED]"` and for `IsPasswordOrFailClosed`,
asserting the set of `(file, containing member)` pairs equals a **pinned list of the twelve sites**.

- A new site ⇒ test fails ⇒ the author must add it to the list, which is the moment they are forced to
  route it through the classifier. This is what makes the §1 success criterion checkable.
- A removed site ⇒ test fails ⇒ nobody deletes a redaction silently.
- Precedent: `test/FlaUI.Mcp.Tests/Server/ToolTrapFactInvariantTests.cs` already sweeps every tool
  method **and parameter** description by reflection and has caught a real regression. Its own docstring
  records the lesson this design reuses: *a tripwire is only as wide as the surface it actually reads* —
  which is why the inventory is per-site, not a single aggregate count.
- Every tripwire must be **mutation-verified**: delete a redaction, observe the named failure, restore.

### 7.3 Gates

Headless suite (789/0 at `57bc7ef`) stays green. The full Desktop suite (140/0/0) runs on the **main
thread, on a quiet machine, under a user-granted lease** — never concurrently with a subagent.

## 8. Out of scope, stated so the panel does not re-raise it

- The credential-store **denylist** (§3.2) is unchanged.
- **Value-shape detection** (§4.1) — excluded with reasons.
- `SnapshotOptions`/`PollOptions` semantics, `scopeRef`, and everything SP2 shipped.
- ROADMAP items 4, 8, 10 (SP4).
- The **stored descriptor** stays raw. See §9.

## 9. Binding constraints

**BC-1 — Redaction is strictly an egress/presentation transformation.** Internal state and identity
matching stay pristine so an agent can still target and interact with a redacted control. This is
load-bearing, not stylistic: `RefRegistry.cs:184-185` falls back to `Name`+`ControlType` as a ref's
identity key when `AutomationId` is absent, and `:337` compares `Name` on the cached fast path.
Redacting the stored descriptor makes a redacted element with no `AutomationId` permanently
unresolvable. ROADMAP item 7 was **retired as invalid** for proposing exactly that.
`RefRegistry.cs:211-212` (`Key()`) already guarantees the descriptor `Name` is never echoed in
diagnostics.

*This replaces SP2's "may not break any existing consumer", which SP3 cannot inherit — per-field
redaction changes payloads by design.*

**BC-2 — Over-redaction is a real cost, not a cosmetic one.** A false positive blinds the agent, and it
cannot distinguish an empty field from a hidden one. Every design decision above that looks
conservative (process scoping, opt-in, off by default, `redactedBy`, excluding value-shape) is paying
down BC-2.

## 10. Exhaustiveness self-audit

Run against this document before handoff.

**Contracts fully specified:** rule file schema incl. every field, type, default and rejection reason
(§5.1) · classifier API with exact signatures and precedence (§5.2) · wire representation per family,
including the two families that deliberately get none (§5.3) · failure semantics enumerated (§5.4).

**Placeholders:** none. No "TBD"/"decide later" remains.

**Requirement → section map:** guarantee-without-vigilance → §6, §7.2 · policy beyond `IsPassword` →
§4, §5.1 · opt-in/off by default → §4.3 · false-positive mitigation → §4.2, §5.3, BC-2 · the two
measured defects → §3.3, §7.1 · identity stays pristine → BC-1.

**Edges covered:** throwing `IsPassword` read (fail-closed, §5.2) · rule matching a denylisted process
(denylist refuses first — §3.2 is enforced upstream of every classifier call) · `global` + `processName`
together (rejected) · duplicate rule names (rejected) · no element predicate (rejected) · regex
catastrophic backtracking (timeout, §5.1) · provenance on oracle paths (deliberately absent — would
re-create the oracle, §5.3).

**Deliberately deferred to the PLAN, not gaps in the spec:**
1. The exact parse mechanism of the §7.2 source sweep (regex vs Roslyn syntax walk over `src/`). The
   *contract* — the pinned twelve-site list and its failure modes — is fixed here; the parse strategy is
   an implementation choice with no contract consequence.
2. Whether DEF-1 and DEF-2 land as one commit or two. Sequencing only; both are in scope either way.
3. Whether `redactedBy` on family A is added per-DTO or via a shared projection helper — depends on how
   many of A1–A5 use anonymous vs typed projections, which the plan must grep per site.

**Known residual risk:** the §7.2 inventory guarantees no *existing-style* site is added without notice,
but a developer could still egress a raw name through a genuinely novel path that never touches
`"[REDACTED]"` or `IsPasswordOrFailClosed` — e.g. a new tool that serializes `SnapshotNode.Name`
directly. Mitigation: the plan adds a second inventory assertion over reads of `SnapshotNode.Name` and
`ElementDescriptor.Name` outside the pinned set. Stated openly rather than claimed closed.
