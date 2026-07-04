# FlaUI.Mcp Phase 10 — Consumer-Ergonomics — Design Spec

Status: DRAFT (spec, not a line-level plan). Author: Claude (the designated consumer of these
tools). Date: 2026-07-04. Base: v0.9.0 (`0a247c2`).

## Why (motivation)

After live-driving v0.9.0 end-to-end (Phase-9 smoke + skill test), the remaining friction is
**not capability or trust** — the lease, deny-list, strict refs, wake, and OCR all landed well.
The friction is **round-trip ergonomics**: getting from "a window exists" to "I acted on an
element" costs several serialized calls, and element refs (`eN`) churn on every re-snapshot /
window restore, forcing constant re-minting just to act.

This spec proposes three changes, consumer-prioritized. It is **forward design**: #2 and #3
need design consensus before any line-level plan; #1 has a design fork (below) that must be
picked first. Nothing here weakens a safety invariant.

## Non-goals (explicit — these do NOT change)

- **No self-unlock / no weakening the input lease.** The out-of-band dead-man's-switch stays.
- **`desktop_find_text` stays targeting, not reading.** OCR resolves click coordinates; it does
  not transcribe text back as data.
- **No loosening strict RuntimeId resolution on state-changing tools.** #2 lives *inside* a
  fail-closed guarantee, never around it.

## Scope

### #1 — Opt-in handle on `desktop_list_windows`

**Intent.** `desktop_list_windows(includeHandles:true)` returns each window's reusable handle
(`wN`) inline, eliminating the mandatory `desktop_open_window` round-trip that precedes almost
every workflow.

**Wire contract.** Additive, opt-in, default off. When `includeHandles:true`, each `WindowInfo`
gains a `handle:"wN"` field (JsonIgnore-when-null so the default response is byte-identical to
today's). Handle is mint-or-reuse per (hwnd) so repeated calls don't leak new ids for the same
window.

**DESIGN FORK (code-verified — this is the real decision).** `WindowManager.ListWindowsAsync`
is deliberately **pure Win32** (`EnumTopLevel` via `EnumWindows`/`GetWindowTextW`) with an
explicit comment that a UIA Title/ProcessId read on the query STA "blocks with NO timeout on ANY
momentarily-unresponsive desktop window." But minting a handle goes through `Register(window,
pid)` → `_automation.FromHandle(hwnd).AsWindow()` — a **UIA touch per enumerated window**. So
`includeHandles` cannot be "just a flag + dictionary insert"; done naively it reintroduces the
exact per-window blocking risk the current design avoids (one hung window stalls the whole list).

Options:
- **(1a) Lazy handle (preferred).** Mint an id and populate only `_hwnds[id]=hwnd` + the
  process-exit watch from the pure-Win32 (hwnd,pid) already in hand; **defer** the UIA
  `FromHandle().AsWindow()` into `_handles[id]` to first actual use. Rationale: the state-changing
  action path (`RunOnWindowActionAsync`) already resolves off the cached **HWND** with a transient
  automation, not off `_handles`; and `RunOnWindowAsync` (reads) is where the UIA `Window` is
  needed and can create it lazily. Net: `includeHandles` stays pure-Win32 and non-blocking.
  Requires: verify every current `_handles`-consumer tolerates lazy creation; define behavior if
  the window dies between list and first use (→ `WindowHandleStale`, already the contract).
  Concrete instance of that "verify" (AGY-AFTER R2, lifecycle seat): an action-only agent that
  lists via #1 and acts via #2 may **never** populate `_handles[id]` (the action path uses `_hwnds`),
  so window-eviction/cleanup MUST tolerate `_handles` missing a key that `_hwnds` holds — use safe
  `Remove(key, out _)`/`TryGetValue`, never a direct indexer that assumes `_hwnds`↔`_handles` 1:1.
  (Plan MUST audit the eviction path against this asymmetry; treated as verify-at-plan, not an
  asserted bug.)
  Panel refinements (AGY-AFTER), folded with measured corrections:
  - *Leak on poll (ops seat):* mint MUST be **mint-or-reuse keyed by hwnd** so a `list(includeHandles)`
    poll loop reuses ids instead of minting a fresh `wN` per window per call. Eviction is already
    covered: `ListWindowsAsync` calls `PruneClosedWindows()` at entry (pure-Win32 `IsWindow` over
    `_hwnds`), so a lazy handle whose window closed *without* a process exit is swept on the next
    list — **provided the lazy handle populates `_hwnds`** (it must; that is the crux of 1a).
    Correction to the seat: the Win32 close-eviction path exists today; the requirement is to ensure
    lazy handles enter `_hwnds`, not to build a new hook.
  - *Double-instantiation race (adversarial seat):* largely **neutralized by the existing single
    query-STA invariant** — `AutomationDispatcher` serializes all reads (`RunOnWindowAsync`) on one
    STA, so two "parallel" reads on the same new `wN` cannot truly race the lazy `FromHandle`.
    Correction to the seat: still make the lazy get **idempotent** (`GetOrAdd` on `_handles`) for
    defensiveness, but there is no cross-thread double-instantiation given the single-STA design.
  - *HWND-recycle identity (AGY-AFTER R1, general-adversarial — ACCEPTED):* Windows recycles HWND
    integers, so a `wN` minted at `list` time whose window closes before first use can, once lazily
    resolved, bind `FromHandle(hwnd)` to a **different** window that inherited the same HWND.
    `PruneClosedWindows`' `IsWindow(hwnd)` does NOT catch this (a recycled HWND is a *valid* window).
    **Requirement for 1a:** lazy resolution MUST re-verify the HWND still belongs to the **recorded
    pid** (`GetWindowThreadProcessId(hwnd) == pid` captured at enumeration) before binding; mismatch
    → `WindowHandleStale`. (This latent risk exists in the eager/`_hwnds` action path today too — the
    mint→first-use gap in 1a merely widens it — so the pid-reverify is worth applying at the shared
    handle-resolution chokepoint, not just in 1a.)
- **(1b) Eager + guarded.** Keep `Register` as-is but wrap the per-window `FromHandle` in a
  bounded timeout, skipping (handle omitted) any window that doesn't resolve in time. Simpler, but
  a partial/timeout-dependent result is a worse contract and still pays UIA cost per window.

Recommendation: **1a**. Fork owner: user (with agy read).

**Plan-vs-spec.** After the fork is chosen, #1 is a **line-level PLAN** (contained to
`WindowManager` + `WindowTools` + `WindowInfo`). Not before — 1a changes handle lifecycle.

### #2 — First-class selectors on existing interaction tools (the main feature)

**Intent.** Kill the re-snapshot churn: let interaction tools target a **stable selector**
(automationId, or name+controlType, optional scope) resolved at action time, so a known control
can be acted on across state changes without re-snapshotting to mint a fresh `eN`.

**Wire contract (agy's shape, adopted over a new tool).** Do **not** add `*_by_selector` tools
(surface bloat). Add an optional `selector` object *alongside* the existing ref param on the
existing interaction tools, enforcing **exactly-one-of** `{ ref | selector }` (both or neither →
`InvalidArguments`):

```json
{ "selector": { "automationId": "submitBtn", "name": "Submit", "controlType": "Button", "scope": "e12" } }
```

Selector semantics reuse the existing `desktop_find` query shape (`FindQuery`) for consistency.

**Preconditions & versioning (Round-2 panel folds):**
- **Under-constrained selector fast-fails.** A selector with no material constraint (none of
  `automationId` / `name` / `controlType`) MUST fail `InvalidArguments` **at the tool layer, before
  touching UIA** — do not translate `{}` to a `TrueCondition` `FindAll` that walks the entire tree
  only to (correctly but wastefully) fail `AMBIGUOUS_MATCH`. Require ≥1 material field.
- **Schema relaxation is intended and back-compat.** Making the existing element/ref param no longer
  `required` (to allow the mutually-exclusive `selector`) relaxes the tool's published JSON schema —
  safe for an MCP minor/patch bump, breaks no existing request. The `handle` / `resolvedElement`
  additions are JsonIgnore-when-null, so no shipped response shape is mutated by default.

**SAFETY INVARIANT (load-bearing).** A selector has **no prior ref**, so it **cannot** inherit
the RuntimeId exact-match guarantee (that guarantee is about a *previously-snapshotted* element
being recycled — there is nothing prior here). What it inherits instead is
**unique-match-at-action-time**:
- **Always window-rooted (AGY-AFTER R1 correction).** The selector search reuses the existing
  window-rooted `FindAsync(new WindowHandle(window), FindQuery, scope)` — every interaction tool
  already takes a **mandatory `window` handle** as its first param (verified `InteractionTools.cs`),
  and `FindAsync` roots the walk at that window, **never** at the Desktop root (verified
  `FindTools.cs`; `RefRegistry.Resolve` doc: "never the whole Desktop"). So a bare selector can
  neither walk the entire desktop nor escape the window/lease boundary; `scope` (an `eN`) only
  narrows *further* within that window. (Round-1 panel raised a "missing window-context" hole and an
  "unrooted FindAll → cross-process desktop walk / lease-escape" — both REJECTED as false-premise on
  measurement: the window is always supplied and the find is always window-scoped.)
- The resolver MUST evaluate with **FindAll** semantics and require **exactly one** match.
- `0` matches → `REF_STALE_UNRESOLVABLE`-class miss (target not present) — never a guess. The
  recovery string MUST also name the fix (AGY-AFTER R1, UX seat), mirroring the `>1` case: e.g.
  "0 matches — the target isn't present in this window right now; if you expect it, reveal it (act
  or `desktop_wait_for`) then retry, or run `desktop_snapshot` to see the current state."
- `>1` matches → **`AMBIGUOUS_MATCH`** — **never** `FindFirst` silent-pick. The recovery string
  MUST explicitly name the fallback so a stuck agent doesn't loop guessing `name` variants: e.g.
  "N matches — refine the selector (add `controlType`/`automationId`), add a `scope`, or run
  `desktop_snapshot` and target a unique `eN`." (Mirrors the project's `suggestedRecovery`
  philosophy — errors that carry their own fix.)
- This is a *different but appropriate* guarantee to RuntimeId strictness, and the spec/docs MUST
  say so plainly rather than imply "same strictness." (It matches how *read* resolution already
  fails closed on duplicate identity.)

**`scope` resolution.** `scope` is an `eN` ref; it resolves under the **existing** ref rules
first, then the selector search runs within that subtree. Scope narrows ambiguity; it does not
relax the count==1 rule. **Error-taxonomy separation (AGY-AFTER R2, failure-mode seat — ACCEPTED):**
a `scope` failure MUST stay legibly distinct from the selector's own 0-match — they are different
faults with different fixes. Report the scope fault with its existing code *about the scope*
(`RefNotFound` if the `eN` isn't in the registry at all; `RefStaleUnresolvable` if it was there but
its element is gone), and only report the selector's 0-match (target-not-present) when the scope
resolved successfully but the `FindAll` within it returned zero. The recovery strings MUST name
which failed ("scope `e12` is stale — re-snapshot" vs "0 matches inside `e12`"), so the agent
doesn't re-issue a doomed selector against a dead anchor or vice-versa.

**Plan-time hardening (AGY-AFTER R3 — all ACCEPTED as plan requirements; none a spec blocker).**
- *Auditable target attribution (observability seat).* A selector's `resolvedElement` `eN` is volatile
  (dies on the next re-walk), so wire-payload logging alone can't prove *which* element a
  state-changing selector acted on after the fact. The action-STA closure MUST emit a durable audit
  trace of the resolved target's **stable identity** — RuntimeId + AutomationId + bounding rect —
  captured at resolution, immediately before the action. **My caveat (agy omitted it):** the trace MUST
  respect redaction — never log the raw `Name` of a password/redacted element (log RuntimeId +
  AutomationId + bounds only), consistent with the project's `[REDACTED]` rule. (If ref-based actions
  don't already emit this, it's a pre-existing audit gap that selectors make more acute — plan should
  close it for both paths.)
- *Bounded action-STA walk (adversarial-UI seat).* A selector with a broad-but-material constraint
  (e.g. `controlType:"Text"`) passes the "≥1 material field" gate yet forces a full
  `TreeScope.Descendants` `FindAll` on the **single action STA** — on a pathological/huge tree (dense
  data grid, hostile nesting) that walk can hang the interaction pipeline (the same UIA-blocking risk
  the design already cites for `ListWindowsAsync`). The action-STA selector `FindAll` MUST carry a
  bound — a time budget and/or node-count cap (reuse/extend the existing `FLAUI_MCP_REF_MAXSCOPES`
  fan-out guard) — failing with a "selector too broad / timed out" error rather than blocking. Note the
  `count==1` rule only rejects `>1` *after* the walk, so bounding the walk is the actual liveness fix.
- *Native-condition pushdown (cost/perf seat).* The ergonomic win (skip the `desktop_snapshot`
  round-trip) is a true latency win **only if** the selector translates to native UIA
  `PropertyCondition`/`AndCondition` so the fan-out runs in native code before crossing COM. `automationId`
  / exact `name` / `controlType` push down natively (as `desktop_find` already does); **`nameMatch=contains`
  inherently post-filters in managed code** (UIA has no substring condition) → costlier, same as
  `desktop_find` today. Plan should confirm the pushdown and note `contains` selectors are the weaker
  case — not a blocker, but sets honest expectations.

**Which tools.** All state-changing pattern/interaction tools that today take a ref
(`desktop_invoke`, `set_value`, `toggle`, `expand`, `select`, `set_focus`, `scroll`,
`scroll_into_view`, `click`, `set_caret`, `select_text_range`, `paste_text`, `key` when
ref-targeted). Deny-list / lease / TOCTOU re-verify apply **after** resolution exactly as today —
a selector does not bypass any gate. Password/redacted targets: a selector matching a redacted
`name` is not matchable by name (mirrors `desktop_find`).

**Plan-vs-spec.** SPEC only. Line-level planning waits on a deeper read of `FindQuery` /
`RefRegistry` / `Interactor` / `InteractionTools` and on the fork resolutions below.

**Resolved forks for #2 (folded from AGY-AFTER panel, cascade `f0977c1d`):**
- (2-i) Resolution bridge — **DECIDED: resolve on the action STA inside `RunOnWindowActionAsync`,
  atomic with the TOCTOU re-verify.** Panel (safety seat) escalated this from a "lean" to
  **mandatory**: resolving the selector on a *different* thread/instant than the action opens a
  TOCTOU window where the UI can shift between the `FindAll` count check and the interaction,
  defeating the fail-closed `AMBIGUOUS_MATCH` guarantee. The atomic search + re-verify + act on one
  STA is the only correct ordering.
- (2-ii) Return the resolved `eN` — **DECIDED: yes.** Response carries `resolvedElement:"eN"` when a
  selector resolved. Additive/back-compat, and it lets the agent pay the `FindAll` cost once, pin
  the volatile `eN`, then fire rapid strict-ref follow-ups (e.g. filling a form). **Durability caveat
  (from live smoke):** the returned `eN` is good only until the *next re-walk* — any `desktop_snapshot`
  / `wait_for` / `diff` re-issues refs and the pinned `eN` dies `RefNotFound` (proven: `e40`→`RefNotFound`
  after one re-snapshot). Docs MUST frame `resolvedElement` as "reuse for immediate follow-ups, do not
  cache across a re-walk — re-run the selector instead." This is exactly why selector-first (#2) beats
  ref-caching.
  - *Ref-lifecycle / monotonic-growth (AGY-AFTER R1, release-ops seat — ACCEPTED, plan-time):* a
    selector-minted `eN` goes into `RefRegistry`'s **durable** `_byWindow` layer, which is **unbounded**
    and swept only by `BeginSnapshot` (next `desktop_snapshot`) or window-close `EvictWindow` — NOT
    per-action (verified `RefRegistry.cs`; only the *event* layer is capped at `EventRefCap=64`). Since
    #2's whole point is to avoid re-snapshotting, a long pure-selector loop would accumulate refs (and,
    if the `Entry.Cached` COM element is pinned, hold COM pins). NOTE this **pre-exists** with
    `desktop_find`'s additive refs — #2 amplifies it, not introduces it.
  - *STA COM-safety → descriptor-only is a MANDATE, not an option (AGY-AFTER R2, concurrency seat —
    ACCEPTED; this SUPERSEDES the R1 "plan-time choice" framing above).* Because 2-i resolves the
    selector on the **action STA**, a minted `eN` that pinned that STA's live `AutomationElement`
    (`RefRegistry.Entry.Cached`) would be poison to any later **query-STA** read of the same `eN` —
    cross-STA COM access throws (`InvalidCast`/RPC thread-mismatch). The codebase already encodes this
    rule: `RefRegistry.ResolveDescriptor`'s doc states the action STA "must NOT touch the query-STA
    cached element." Therefore selector-minted refs **MUST** be stored **descriptor-only** (RuntimeId +
    descriptor, `Cached = null`) so the query STA safely re-binds from the descriptor on next use. This
    also disposes of the monotonic-COM-pin half of the growth concern (descriptor records are light;
    still consider an LRU bound if paranoid). Net: descriptor-only is required for correctness *and*
    incidentally caps the pin leak.
  - *Plumbing reality (AGY-AFTER R2, sequencing seat — PARTIALLY accepted; "core refactor" claim
    rejected on measurement).* Returning `resolvedElement` is **not** a pure-local JSON-literal add: the
    `eN` is minted inside the action-STA closure and must be threaded out through the tool's `Act`
    helper into the response. But the action wrapper is **already generic** — `RunOnWindowActionAsync<T>`
    / `RunOnRefActionAsync<T>` return `Task<T>` (the interaction lambdas merely pass `bool` today), so
    this needs **no core delegate-signature change**, just a localized `T = <result>` threading in the
    selector path. (agy's "must refactor the core wrapper signature" was overstated; verified against
    `WindowManager.cs` / `PerceptionManager.cs`.)
- (2-iii) **Param naming — verify at plan time.** The spec writes `ref` for the existing element
  parameter; the plan MUST use the tool's *actual* current parameter name (confirm against
  `InteractionTools`/`InputTools` — likely `ref`) so `exactly-one-of {<existing> | selector}` does
  not rename or break the shipped contract.

**Honest limitation (consumer note).** #2's payoff scales with how many target controls carry a
stable `automationId`. Where they do, it's a large win (act across state changes, no re-snapshot);
where a control has no `automationId` **and** a non-unique `name`, the selector safely degrades to
`AMBIGUOUS_MATCH` (fail closed) and you fall back to the snapshot path — not a regression, but not a
universal cure. The spec/docs should state this plainly rather than oversell #2.

**Live validation (2026-07-04 lease smoke, Calculator).** The #2 premise was proven end-to-end,
not assumed:
- *Selector primitive:* `desktop_find automationId=num5Button` → exactly one actionable ref, then
  `desktop_invoke` succeeded (`pathUsed:pattern`, display went `0`→`5`). Today that is **two** calls
  (find + act); #2 collapses them into **one** atomic action-STA call.
- *Ref churn is real:* a re-snapshot renumbered **every** ref (`e1–e49` → `e51–e99`, `num5Button`
  moved `e40`→`e90`), and invoking the held old ref `e40` then failed **`RefNotFound`** ("take a
  fresh desktop_snapshot") — precisely the re-establish step #2 removes.
- *Ambiguity is real:* an earlier `desktop_find name="Selection"` returned **3** matches — the exact
  case the `count != 1` fail-closed invariant exists to catch.

**Second live smoke (2026-07-04, session 2, Calculator) — findings folded into the plan requirements.**
Re-drove the tool to pressure-test #2's contract against today's `desktop_find` (which already implements
the resolution #2 reuses). Four findings:
- *Under-constrained walk is real, not hypothetical.* `desktop_find` with **no material field** returned
  **52 matches (truncated)** — today's `find` has no guard and does the full `TrueCondition` tree walk.
  This **empirically earns** the fold "selector MUST require ≥1 material field and fast-fail
  `InvalidArguments` at the tool layer before UIA" — it is a real gate today's `find` lacks (plan should
  decide whether to also retrofit `find`, or scope the gate to the selector resolver only).
- *Ambiguity is easy to hit; `count==1` is well-earned.* `controlType=Button` → **36**; `name="Memory"
  nameMatch=contains` → **5**; but `name="Five" + controlType=Button` → **exactly 1**. Confirms the
  no-`automationId` fallback (name+controlType) CAN be unique, and that broad selectors correctly land in
  `AMBIGUOUS_MATCH` territory (fail closed) — validating the recovery-string design.
- *NEW — `nameMatch=contains` is CASE-SENSITIVE (Ordinal).* `contains "Memory"` matched the five
  capital-**M** controls but **missed** "Clear all **m**emory" and "Open **m**emory flyout". Since the
  selector reuses this matcher, an agent querying `"memory"` would silently miss half the controls — a
  quiet foot-gun. **DECIDED (user, 2026-07-04): case-INSENSITIVE.** The selector's `name` matching is
  case-insensitive (both `eq` and `contains`), so an agent that doesn't know exact casing still resolves
  the target. **Mechanism (AGY-AFTER R5 — the "playground-divergence" fix, folded):** expose an explicit
  **`ignoreCase: bool` on the shared `FindQuery` wire shape**, ONE mechanism for both tools —
  `desktop_find` defaults it **`false`** (preserves shipped Ordinal back-compat), the selector defaults
  it **`true`** (the ergonomic call above). This closes a real trap agy caught: if the selector were
  silently case-insensitive while `desktop_find` stayed Ordinal, an agent would *test* a query with
  `desktop_find` (1 match → "unique/safe"), embed it as a selector, and have it widen to `AMBIGUOUS_MATCH`
  only at action time — the testing tool disagreeing with the executing tool. With a shared `ignoreCase`,
  the agent previews selector matching via `desktop_find(ignoreCase:true)` BEFORE committing, and can set
  `ignoreCase:false` on a selector to disambiguate a genuine `Submit`/`submit` collision (else it's forced
  off the selector path onto snapshots). The `AMBIGUOUS_MATCH` recovery string MUST name the
  `ignoreCase:false` escape. Matching is **culture-invariant** (`OrdinalIgnoreCase` for the managed
  `contains` post-filter; native `PropertyConditionFlags.IgnoreCase` for the `eq` pushdown — plan-time
  verify FlaUI's `ConditionFactory` wires this flag) so it stays deterministic across machines and does
  NOT break the R3 native-pushdown requirement. Case-insensitivity **widens** the match set, so it
  interacts with `count==1`: it can turn a previously-unique `name` into `AMBIGUOUS_MATCH` — correct
  fail-closed behavior, covered by the (now `ignoreCase`-aware) recovery string.
- *Coordinate space is consistent (non-finding, cleared).* A `find` vs snapshot bounds gap for `num5Button`
  (`[627,535]` vs `{191,529}`) was purely the **window having moved** (same +436px x-offset on the window
  itself); width/height identical. `find` and `snapshot` share one physical-px space, so `resolvedElement`
  bounds are consistent with snapshot bounds — no wire concern.

### #3 — Composite `desktop_orient(pid|title)` (deferred, may be dropped)

**Intent.** One call returns `{handle, snapshot}` and, when the window is `wakeable`, *optionally*
(opt-in flag, never implicit) wakes it and returns the hydrated tree + the held `wakeId`.

**Status.** **Deferred, and a candidate to drop.** agy's point (which I agree with): once #1 +
#2 land, `list_windows(includeHandles)` + selector-based action already yields a very tight loop,
so `orient`'s marginal value may not justify its complexity (it holds wake state, which must be
opt-in and reported for release). Revisit only after #2 ships and is dogfooded.

## Sequencing

1. **#1** (line-level plan after fork 1a/1b) — isolated, shippable win.
2. **#2** (this spec → plan after forks 2-i/2-ii) — the main Phase-10 feature.
3. **#3** — revisit post-#2; likely dropped.

## Verified facts (grounding)

- `WindowTools.DesktopListWindows` → `_windows.ListWindowsAsync(includeBounds)`;
  `WindowInfo` is a record with JsonIgnore-when-null optional fields (`Bounds`, `ZOrder`).
- `ListWindowsAsync` is pure Win32 (`EnumTopLevel`), no UIA, by explicit design (non-blocking).
- Handles are minted by `Register(window,pid)` which calls `_automation.FromHandle(hwnd).AsWindow()`
  (UIA) and populates `_handles` + `_hwnds` + a process-exit watch.
- The state-changing action path (`RunOnWindowActionAsync`) resolves off cached **`_hwnds`** HWND
  with a transient `UIA3Automation`, not off `_handles` — the basis for the 1a "lazy handle" option.
- Existing find/query shape lives in `FindQuery` / `FindTools` (reused by #2's `selector`).

## Review gate

AGY-FIRST consult done (cascade `f0977c1d`) — sequencing + #2 shape + plan/spec split folded,
with my correction to agy's "#1 is trivial" claim (it is not; see fork).

**AGY-AFTER panel — round 1 (cascade `62e57860`) = verdict downgraded BLOCK → GO-WITH-FIXES on
measurement.** 5 seats fired. Folded 3 valid findings: (#1) HWND-recycle pid-reverify hardening for
fork 1a; (ops) selector-ref monotonic-growth in the durable `RefRegistry` layer → plan-time
descriptor-only/LRU decision under 2-ii; (UX) actionable 0-match recovery string. **REJECTED 2
headline findings as false-premise (verified against code):** agy's "missing window-context hole" and
"unrooted FindAll → cross-process desktop walk / lease-escape" both assume interaction tools lack a
window root — but every interaction tool already takes a mandatory `window` param
(`InteractionTools.cs`) and `FindAsync` is always window-rooted (`FindTools.cs`). agy's BLOCK rested
primarily on those two; corrected, the residual findings are non-blocking refinements. **AGY-AFTER panel — round 2 (rotated seats: concurrency / lifecycle / error-taxonomy / back-compat /
sequencing) = GO-WITH-FIXES.** 4 new flaw-classes (back-compat seat: no new findings). Folded: (R2-1,
concurrency — the sharpest) selector refs resolve on the **action STA**, so pinning their COM element
would poison a later query-STA read → **descriptor-only is now a correctness MANDATE**, superseding
R1's "plan-time option" framing (grounded in `RefRegistry.ResolveDescriptor`'s existing "action STA
must not touch query-STA cached element" rule — this was a valid CHALLENGE to my own R1 wording, folded
in agy's favor); (R2-2, lifecycle) action-only agents may never populate `_handles`, so 1a eviction
must tolerate the `_hwnds`/`_handles` asymmetry — concrete instance of 1a's existing verify-item; (R2-3,
error-taxonomy) keep `scope`-fault codes distinct from the selector 0-match. **Trimmed R2-4 on
measurement:** the action wrapper is already generic `Task<T>`, so returning `resolvedElement` needs no
"core delegate-signature refactor" (agy overstated) — only localized threading. **AGY-AFTER panel — round 3 (seats: data-integrity / observability / adversarial-UI / doc-coherence /
cost-perf) = GO-WITH-FIXES.** 2 seats clean (data-integrity confused-deputy; doc-coherence — the design
now holds), 3 plan-time hardening items folded into "Plan-time hardening (R3)" above: durable audit
attribution of the resolved RuntimeId (+ my redaction caveat agy omitted); a bounded action-STA
`FindAll` (broad-but-material selector can't hang the single STA); native-condition pushdown for the
latency win (`contains` is the weaker case). Character has shifted from design flaws (R1/R2) to a
plan-time checklist — the diminishing-returns signal. Round 4 to confirm a clean landing.

**AGY-AFTER panel — round 4 = CLEAN LANDING, verdict GO.** All seats (architecture/safety,
lifecycle/state-machine, API/contract, operability/perf) returned "no new findings" — a full panel
landing no live challenge, the defined convergence stop. Four rounds total: R1 caught 2 false-premise
findings (rejected) + 3 folds; R2 landed the descriptor-only-STA mandate (a valid self-challenge to my
R1 framing) + 3 more; R3 folded 3 plan-time hardening items; R4 clean. Spec is design-sound and its
plan-time requirements are captured — ready to drive the #1 line-level plan once the user picks the
forks below. (Forks remain the USER's call: #1 1a-vs-1b, #2 selector-alongside-ref shape, #3
keep-deferred-or-drop.)

**AGY-AFTER panel — rounds 5–6 (post-second-smoke fold).** After a second live Calculator smoke
(session 2) I folded four findings under #2 (under-constrained walk confirmed real; ambiguity/count==1
confirmed; `nameMatch=contains` found CASE-SENSITIVE; bounds coordinate space cleared as a non-finding)
and the USER decided the selector's `name` matching is **case-insensitive**. R5 (focused delta panel)
caught the "playground-divergence trap" — a selector silently case-insensitive while `desktop_find`
stayed Ordinal would make the agent's *testing* tool disagree with its *executing* tool → surprise
`AMBIGUOUS_MATCH` at action time. Folded the fix: an explicit **`ignoreCase: bool` on the shared
`FindQuery`** (find defaults false / selector defaults true; previewable + escapable; native
`PropertyConditionFlags.IgnoreCase` pushdown verified feasible). **R6 confirmation = clean GO**, no new
flaw introduced by the fix. Spec re-converged; #2's line-level plan is the next step (user chose to
build the automationId selector).
