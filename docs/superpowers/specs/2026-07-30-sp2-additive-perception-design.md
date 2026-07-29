# SP2 — additive perception

**Status:** design approved 2026-07-30 (user decided every fork; agy consulted on both open ones).
**Release:** the third phase of the fixes + opportunistic-hardening increment after `v0.20.0`.
**Predecessors:** SP0 + SP1 merged to `master` (`c1dc41f --no-ff`, then `94e932a`, `5892ddc`, `ac430b0`).

**THE BINDING CONSTRAINT.** SP2 exists as a subproject for exactly one recorded reason: items 3, 5 and 7
are *"genuinely additive; cannot break a consumer."* That is the whole basis of the risk cut that
separated them from SP1 — the only behaviour-changing piece, now shipped. **No change in SP2 may break an
existing caller.** A change that needs to break one belongs in a later subproject, not here. This
constraint already overturned one recommendation during design (see Fork A).

## Goal

Close the three additive opportunistic-hardening items without changing any existing caller's outcome:
make `desktop_wait_for_stable` scopeable by ref (and stop it blaming the wrong cause when its selector
scope was culled), give terminal-tab ordinals a discoverable source, and pin the password-`Name`
guarantee that item 7 wrongly assumed needed building.

---

## Measured ground truth

Every claim below was read off the code on 2026-07-30. **Cite these; do not re-derive them.**

**`SnapshotOptions.RootRef` already exists.** `SnapshotOptions.cs:6` declares it; `PerceptionManager.cs:425-427`
resolves it (`isFullWindow = string.IsNullOrEmpty(options.RootRef)`, else
`refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop))`). `desktop_snapshot`
already exposes it as `root` (`SnapshotTools.cs:22`, `:31`). **Item 3 therefore builds no new walk
machinery** — it reuses a rooting path that has shipped since Phase 10.

**A ref-rooted walk's scope root is structurally immune to every drop-filter.** `SnapshotEngine.Visit`
gates all of them on `depth > 0`: popup dedup `:62`, the `IsOffscreen` property filter `:65`, the spatial
cull `:66-70`. The root is visited at `depth 0` (`:46`), and `:74` forces `include = true` at depth 0.
So a culled scope root cannot occur on the ref path — this is a structural guarantee, not a mitigation.

**Five filters sit between the raw child array and an emitted node; four can drop it.** Popup dedup
`:62-64`, `IsOffscreen` `:65`, `CullToWindowBounds` `:66-70`, `MaxDepth` `:95-98` all drop. `InteractiveOnly`
`:74` cannot drop a `TabItem`, because `ControlType.TabItem` is in `InteractiveTypes` (`:19`). This is why
a snapshot-derived tab ordinal is not usable as a tab *index* (Item 5).

**`WaitForStableAsync` polls with the spatial cull ON, and scopes by selector against the culled model.**
`WaitCoordinator.cs:133` builds with `PollOptions` (`:93` — `IncludeOffscreen = false`, cull left at its
default `true`); `:134` calls `Subtree(model, by, value)`; `:135-136` throws `SelectorNoMatch` —
*"No element matched {by}={value} to scope stability."* — whenever the scope is absent **from the culled
model**. `Subtree` (`:112-121`) finds the first match by index and takes the contiguous deeper run.

**That throw is a false statement when the scope exists but was culled.** It is the same defect class SP1
removed from `gone`: confidently naming a cause that is not the real one. SP1's own rationale
(`WaitCoordinator.cs:53-68`) states blaming the wrong cause is worse than the bare timeout it replaces.
The memory index records this explicitly as item 3's problem, not a separate item.

**The confirmation walk is nearly free, and needs no latch.** The throw sits *inside*
`if (scopeRequested && sub.Count == 0)`. On the success path the block is skipped entirely, so a
confirmation walk there costs **zero** extra walks. On the miss path it costs **one** — on a call that
today returns nothing at all. SP1's latch exists because a `gone` confirmation recurs every poll; here the
miss path `throw`s and terminates the call, so there is no subsequent poll to double. **No latch.**

**`WaitForStableAsync` passes a THROWAWAY registry, which blocks a naive `RootRef`.**
`WaitCoordinator.cs:133` passes `new RefRegistry()` — deliberate, per the class docstring `:18-19`
("ONE short query-STA Build with a THROWAWAY RefRegistry (no durable-registry growth)"). But
`BuildModelAsync` resolves `RootRef` against **that same** registry (`PerceptionManager.cs:427`). Passing a
caller's ref through unchanged resolves it against an empty registry and fails `REF_NOT_FOUND` on **every**
call. The durable registry is `_refs`, which `SnapshotAsync` (`:601`) and `SnapshotModelForWaitAsync`
(`:609`) both pass. **The two roles must be separated** — see Item 3, design point 3.

**The descriptor's `Name` is the resolver's only lookup key when `AutomationId` is absent.**
`RefRegistry.cs:169-182`: identity key is `AutomationId` if present, else
`cf.ByName(d.Name).And(cf.ByControlType(d.ControlType))`. The cached fast path also requires a `Name`
match (`:334`). `SnapshotEngine.cs:87` stores the **raw** name into `ElementDescriptor`.

**The descriptor's `Name` is already never echoed.** `RefRegistry.cs:206-209`: the diagnostic label `Key(d)`
uses `AutomationId` only, with the comment *"Name is frequently user content and is NEVER echoed."*
`WaitCoordinator.cs:85-91` matches on the redacted name; the renderer masks before the wire.

**The codebase already documented item 7's defect.** `PerceptionManager.cs:580` carries the comment
*"Descriptor uses the RAW name - a redacted `[REDACTED]` would break Name-based re-resolution."* The
conclusion below is therefore not a new analysis overriding the ROADMAP — it is a pre-existing, deliberate
invariant that the ROADMAP entry was written without noticing.

**Item 7's premise is false.** `ROADMAP.md:327-328` calls redacting the descriptor `Name` *"pure
defense-in-depth"* on the grounds that *"`Name` is empty for conformant password controls today, so no
secret is stored."* Both halves fail: for a **non-conformant** control (`IsPassword` true, `Name`
non-empty, no `AutomationId`) — precisely the population the item exists to protect — redaction replaces
the only working lookup key with `"[REDACTED]"`, so `cf.ByName("[REDACTED]")` matches nothing and every
ref to that element becomes permanently `REF_STALE_UNRESOLVABLE`. You cannot redact the key you query the
OS with. And the value it would protect is already never echoed, so the redaction buys only removal from
the server's own managed heap.

**Existing coverage of the password `Name` path: none.** `PasswordRedactionTests.cs` contains exactly one
test, `Password_field_value_is_redacted_in_the_snapshot` (`:19`) — the **value**, not the `Name`, and it is
Desktop-gated (`IClassFixture<TestAppFixture>`). Nothing pins the never-echoed `Name` guarantee.

**Tool descriptions are capped at 1500 chars, enforced by reflection over every tool.**
`ToolTrapFactInvariantTests.cs:23` (`DescriptionBudget = 1500`), enumerated at `:46-50` across all
`McpServerToolType` types. A **new** tool is therefore covered automatically and needs no registration
list update. Its docstring `:21-22` states raising the number to fit a new description is the failure it
exists to catch — **trim the description instead**. (This test turned a "doc-only, zero-risk" edit red
during SP1; treat it as a hard gate.)

**`TerminalTabReader.EnumerateTabs` is already a pure read.** `TerminalTabReader.cs:51-60` — locates the
strip (`:35-47`, bounded to depth ≤ 2), filters `ControlType.TabItem`, throws `PatternUnsupported` on an
unrecognized layout. `IsSelected` `:62-66` is a read. `Select` `:68-73` is the only mutation, and
`EnumerateTabs` does not call it.

---

## Item 3 — `desktop_wait_for_stable`: ref scoping, and an honest selector path

Three changes, all additive.

**1. Add optional `scopeRef` scoping.** New parameter on `DesktopWaitForStable` (`SnapshotTools.cs:79-91`),
threaded to `WaitForStableAsync` and mapped to `SnapshotOptions.RootRef`. The walk is then rooted at that
element, so per-poll cost falls with the subtree's node count instead of the window's — the point of the
change.

Exact signatures (so the implementer invents nothing). Both new parameters are appended **last** with
defaults, so every existing call site and every positional caller keeps compiling and behaving unchanged:

The parameter is named **`scopeRef`** — NOT `root`, and NOT `ref`. It scopes the *wait*; it does not scope
the *returned snapshot*. The success path's `snapshotId` continues to come from a whole-window walk
(`WaitCoordinator.cs:141-143`, `PollOptions` with no root), **unchanged**.

Why not `root`: `desktop_snapshot`'s `root` (`SnapshotTools.cs:22`) scopes the tree it returns. If this
parameter were also called `root` while the returned snapshot stayed whole-window, one name would mean two
different things across sibling tools — and a caller chaining `desktop_snapshot_diff` or `desktop_find` onto
the returned `snapshotId` would be reasoning about the wrong tree. `scopeRef` states its actual effect.

Why not also scope the returned snapshot (which would have made `root` honest): a caller who waits for a
spinner's subtree to settle almost always wants to inspect the **whole window** next — that is the point of
having waited. Returning a subtree snapshot would force an immediate second full walk, giving back more than
the saving. And the saving is small: that final walk happens once per successful call, not once per poll, so
it is not where `wait_for_stable`'s cost lives. Leaving that call untouched is also strictly less change,
which is what an additive subproject should prefer.

*(History, recorded so it is not re-litigated. agy first advised renaming to `root` for cross-tool
consistency; I then amended the spec to scope the returned snapshot so that `root` would be truthful; agy
then argued the amendment was a hard break. **Its argument was factually wrong** — it claimed the truncation
applied "regardless of whether the wait was scoped via by/value", but `scopeRef` is null on the selector
path, so no existing caller can reach the new behaviour at all. Its **conclusion** was nonetheless right,
for the ergonomic and minimal-change reasons above. Folded on the correct reasoning, not the stated one.)*

```csharp
// SnapshotTools.cs — DesktopWaitForStable, appended after pollIntervalMs
[Description("Optional ref to scope stability to (from a prior snapshot of this window). Cheaper than by+value: the poll walk is rooted here, not at the window. Mutually exclusive with by/value. Scopes the WAIT only - the returned snapshotId is still the whole window.")] string? scopeRef = null,
[Description("Include off-screen elements and elements past the window edge (default false, matching desktop_snapshot).")] bool includeOffscreen = false

// WaitCoordinator.cs — WaitForStableAsync, appended after pollIntervalMs
string? scopeRef = null, bool includeOffscreen = false
// scopeRef flows ONLY into the per-poll BuildModelAsync options (SnapshotOptions.RootRef).
// It must NOT be passed to the final SnapshotModelForWaitAsync at :141-143 — that snapshot
// stays whole-window. See the naming rationale above; this is a deliberate asymmetry.

// PerceptionManager.cs — BuildModelAsync, appended last; defaults to the registration
// registry so ALL existing call sites (:601, :609, :734) are byte-identical no-ops
RefRegistry? resolveRefs = null
```

`ROADMAP.md:62` records the motivating arithmetic: one full walk costs ~3.0s, so `wait_for_stable`
floors at ~12.5s against its own 5000ms default, unreachable by 2.5×. Rooting at a small subtree is the
only lever that makes the default budget reachable, because per-node property reads are ~90%+ of walk cost.

Scoping precedence, stated explicitly because three scoping inputs now coexist:
- `scopeRef` supplied → ref-rooted poll walk; `by`/`value` are **rejected** if also supplied
  (`InvalidArguments`), never silently ignored. Two scopes in one call is a caller bug worth surfacing.
- `by`+`value` supplied → today's behaviour, plus change 2 below.
- neither → whole window, exactly as today.
- `by` without `value` (or vice versa) → today's behaviour is to treat the scope as absent
  (`scopeRequested` at `:129` requires both). **Preserved unchanged** — it is a published behaviour and
  SP2 may not break it.

The `scopeRef` path needs no `Subtree` call: the polled model is already the subtree, and its root is
`nodes[0]`.

Remaining parameter interactions, specified so none is decided at the keyboard:
- **`scopeRef` + `includeOffscreen` together is legal and meaningful.** It disables both filters for the
  subtree's *descendants*; the root was already exempt. Not contradictory, so not rejected.
- **A `scopeRef` minted from a different window** fails `REF_NOT_FOUND` — `refs.Resolve` is keyed by
  `handle.Id` (`PerceptionManager.cs:427`). Existing behaviour of an existing code; nothing to add.
- **A `scopeRef` whose element is legitimately gone** fails `REF_STALE_UNRESOLVABLE`, mid-wait included. The
  call errors rather than silently widening scope to the window — silently re-scoping would be a
  confused-deputy retarget, which `RefRegistry.cs:211-214` already refuses on the ancestor path.
- **`stable:true` and the returned snapshot are a DIRTY READ of each other, and the description must say so.**
  The snapshot is taken *after* stability is decided (`WaitCoordinator.cs:139-143`), so the subtree can change
  again in between and the caller can receive `stable:true` alongside a snapshot whose subtree no longer
  matches the signature that satisfied the wait. **Pre-existing and inherent, not introduced by `scopeRef`** —
  it is equally true of today's `by`/`value` path and of the unscoped whole-window path, because no
  atomic "decide and capture" exists across two separate UIA walks. What the result means, stated plainly:
  the subtree *was* quiet for the required consecutive polls; it is not frozen, and nothing can freeze it.
  Documented rather than fixed, because the only real fix is a guarantee UIA cannot give.
- **The final whole-window snapshot can still throw and destroy an earned `stable:true`** if the window dies
  between the last poll and that walk (`WaitCoordinator.cs:139-143`). This is **pre-existing, unchanged
  behaviour** — the final walk resolves no ref, so `scopeRef` neither creates nor worsens it. Recorded
  because the panel raised it against an earlier draft that DID root that walk; fixing it is not SP2's.
- **`includeOffscreen` alone, with no scope,** applies to the whole-window walk. Legal, and the case the
  hazard note below is about.

**2. Make the selector miss path tell the truth.** Inside `if (scopeRequested && sub.Count == 0)`, do one
walk with `UnculledPollOptions` (`WaitCoordinator.cs:104-105` — spatial cull off, `IsOffscreen` filter
**kept**, for the reasons documented there) and re-run `Subtree`:
- now present → throw stating the scope element exists but was culled against the window bounds, naming
  both escapes (`includeOffscreen: true`, or scope by `scopeRef`).
- still absent → throw a **corrected** message. Today's text — *"No element matched {by}={value} to scope
  stability"* — asserts non-existence, which the code **cannot prove** even after the unculled walk:
  `UnculledPollOptions` keeps the `IsOffscreen` filter (`WaitCoordinator.cs:104-105`) and `MaxDepth` still
  applies (`SnapshotEngine.cs:95-98`), so the element may exist and be off-screen, or sit deeper than the
  depth limit. The message must state only what was **searched** rather than asserting what does not exist.

  **The recovery names only ACTIONABLE causes.** `includeOffscreen` and scoping by `scopeRef` are both
  parameters the caller can set, so both are named alongside correcting the selector. `MaxDepth` is
  deliberately **not** named: `desktop_wait_for_stable` exposes no `maxDepth` parameter
  (`SnapshotTools.cs:79-91`), so telling a caller their element might be too deep hands them a cause they
  cannot act on — noise dressed as diagnosis. Adding a `maxDepth` parameter to make it actionable is scope
  creep past SP2's three items and is not adopted. The message says what was searched; the recovery says
  what to change.

  **Changing that message is safe.** It occurs at exactly one place in the repo
  (`WaitCoordinator.cs:136`) and **no test asserts it** — verified by search. A diagnostic string is not a
  behavioural contract, so correcting it does not breach SP2's constraint; shipping a false statement to
  preserve its wording would be the worse trade. This is the same call the launch diagnostic made earlier in
  this release.

**The confirmation walk must not be able to destroy the answer with its own failure.** `WaitForStableAsync`
`:130-147` has no `try`/`catch`, so if the extra unculled build throws — the window closes between the poll
walk and the confirmation walk, or a transient COM fault fires — that exception propagates and the caller
receives a generic UIA/COM error *instead of* the legible `SelectorNoMatch` it would otherwise have received.
A diagnostic must never be able to destroy the diagnosis it exists to improve. **Required:** wrap the
confirmation walk in `try`/`catch` and fall back to throwing the corrected-but-unconfirmed `SelectorNoMatch`.
The confirmation is an *upgrade* to the message, never a precondition for producing one.

**But the catch must NOT be blanket — let `ToolException` propagate.** A window closing mid-wait surfaces
`WindowHandleStale` / `WindowNotFound` from the build (`WindowManager.cs:181-199`, `:545-565`), and
a denied target surfaces `TargetDenied` (`PerceptionManager.cs:420-423`). Swallowing those and reporting
`SelectorNoMatch` would transmute a true, actionable fatal error into a false one, sending the caller to
edit a selector when their window is gone — the exact defect class this whole release removes. **Catch only
non-`ToolException` faults** (raw COM/UIA); any `ToolException` from the confirmation walk is a truer answer
than the one it replaced, so it wins.

**Both cases keep `ToolErrorCode.SelectorNoMatch`; only the message and `SuggestedRecovery` differ.** No
new enum value. The error taxonomy is a published contract, and adding a code obliges every consumer's
error mapping to learn it, while the actionable information belongs in the message and recovery. This is
the exact pattern this release already used for the launch diagnostic — one `LaunchTimeout` code, two
messages and two recoveries selected by a measured condition (`WindowManager.LaunchTimeoutFor`, pinned by
`LaunchDiagnosticTests`). Follow that precedent, including its lesson: the message must assert only what
the code can prove.

The second case is new information on a path that previously produced a wrong diagnosis. It remains a
throw, not a silent success: proceeding to measure stability over a scope we only found by disabling the
cull would change what the tool measures, which is not additive.

**2a. The popup scan is a FIXED per-poll cost, and on the ref path it currently runs TWICE.**
Measured on the code, not inferred. `BuildModelAsync` calls `PopupFinder.FindOwnerPopups(desktop, win)`
unconditionally at `PerceptionManager.cs:424`, **before** `RootRef` is even examined at `:425`. Then `:427`
resolves the root against `PopupFinder.SearchRoots(win, desktop)` — and `SearchRoots` calls
`FindOwnerPopups` **again** (`PopupFinder.cs:17`). Each scan is `desktop.FindAllChildren()` plus roughly six
cross-process property reads per desktop child (`PopupFinder.cs:35-47`), which is the ~593 ms term in the
walk attribution (`ROADMAP.md:337-344`). So a naive ref-rooted poll pays ~1.2 s of fixed cost before it
reads a single node of the subtree.

**Required:** call `FindOwnerPopups` **once per build** and feed its result to both consumers — but they take
**different lists**, and conflating them corrupts the walk:

- the model's popup grafting takes **`popups` only** (what `PerceptionManager.cs:424` passes today);
- the root ref's resolution takes **`{ win }` followed by `popups`** — i.e. what `SearchRoots` builds
  (`PopupFinder.cs:16-17`). Its documented invariant is load-bearing: *"searchRoots[0] MUST be the window
  root (IndexPath is window-relative)"* (`PopupFinder.cs:12-13`).

Passing `popups` alone to the resolver makes every window-rooted ref unresolvable; passing `{win} + popups`
to the grafting loop makes `SnapshotEngine` visit the window a second time as its own popup root
(`SnapshotEngine.cs:47-54`), duplicating the whole tree and breaking the window-relative `IndexPath`
invariant. Only the **scan** is shared; the two lists stay distinct.

Rejected alternatives, with reasons, so they are not re-proposed:
- *Skip popup finding when `RootRef` is set* — would regress the popup-blindness defect SP1 just fixed,
  and the ref may itself live in a popup, so `Resolve` needs those roots.
- *Cache the popup list across the poll loop* — a popup opening or closing mid-wait would then be invisible,
  trading a cost win for exactly the wrong-belief class this contract exists to prevent.

**Consequence for the cost claim:** rooting at a subtree removes the per-node property traffic, which is the
dominant term, but it does **not** reduce to zero — a fixed floor of one popup scan per poll remains.
The spec therefore does **not** claim a specific speedup. **The plan must MEASURE the ref-rooted poll cost
on a real window and record it**, exactly as the walk attribution was measured, before any claim that
`wait_for_stable`'s default 5000 ms budget is reachable. Inference lost to measurement repeatedly in this
release; do not restart that habit here.

**2b. The scope root must resolve STRICTLY, or the wait silently changes what it measures.**
`refs.Resolve` (`RefRegistry.cs:143-156`) falls through to `ResolveDescriptor(d, searchRoots, @ref)` with the
default `mode` — and that default is `RefResolveMode.Lenient` (`:163-164`), which **re-walks the descriptor**
and rebinds to whatever now matches `AutomationId`, else `Name`+`ControlType`. Because the poll walk resolves
the ref on **every** poll, a scoped wait against an element that dies and is re-created with the same
AutomationId would seamlessly rebind to the **new instance** and then report `stable:true` about an object
the caller never named. Lenient is right for reads, where "find me that thing again" is the intent; it is
wrong here, because a caller who passes a ref is asking about **that instance** — if they wanted a logical
selector that survives re-creation, `by`/`value` is exactly that, and it is still available.

**Required:** resolve the scope root with `RefResolveMode.Strict`, which matches only the exact element by
live RuntimeId and never rebinds (`RefRegistry.cs:160-162`), **defaulting to `Lenient`** everywhere else so
`desktop_snapshot`'s `root` and every other existing caller keep their current behaviour untouched.

Exactly where it threads — three edits, no invention required:

```csharp
// 1. SnapshotOptions.cs — beside RootRef (:6), because it governs how RootRef resolves
public RefResolveMode RootResolveMode { get; init; } = RefResolveMode.Lenient;   // default = today

// 2. PerceptionManager.cs:427 — pass it through
refs.Resolve(handle.Id, options.RootRef!, roots, options.RootResolveMode)

// 3. RefRegistry.cs:143 — Resolve gains an optional mode and forwards it at :155
public AutomationElement Resolve(string windowId, string @ref,
    IReadOnlyList<AutomationElement> searchRoots,
    RefResolveMode mode = RefResolveMode.Lenient)
    => ...  ResolveDescriptor(d, searchRoots, @ref, mode);
```

`WaitForStableAsync` then sets `RootResolveMode = RefResolveMode.Strict` in the poll options it builds.
Nothing else changes: every existing `Resolve(...)` and `SnapshotOptions` construction omits the new
member and gets `Lenient`.

**The cached fast path stays enabled under `Strict`, deliberately.** `Resolve`'s fast path
(`RefRegistry.cs:148-153`) verifies the LIVE RuntimeId along with ControlType, offscreen state and Name, so
it already satisfies what `Strict` demands — bypassing it would add a full descriptor re-walk per poll for no
gain. The load-bearing part is the **fall-through** at `:155`: it must carry `mode`, or `Strict` silently
degrades to a lenient rebind on exactly the path where identity matters. That single argument is the whole
fix; getting it wrong reintroduces the defect while looking correct.

**Consequence, and it is the intended one:** a re-created scope now fails `REF_STALE_UNRESOLVABLE` mid-wait
instead of silently following the replacement. That is the honest outcome, and it is the same trade INV-8
already makes on every state-changing path. It also means a caller scoping something virtualized — where UIA
may legitimately reissue a RuntimeId (`RefRegistry.cs:131-133` notes the recycling hazard) — should scope by
`by`/`value` instead. The tool description must say which parameter to reach for.

**3. Separate the registry's two roles.** `BuildModelAsync` must resolve `RootRef` against the **durable**
`_refs` while still registering walked nodes into the caller's **throwaway** registry. Add an optional
resolve-registry parameter to `BuildModelAsync`, defaulting to the registration registry so every existing
call site (`:601`, `:609`, and the diff path at `:734`) keeps its current behaviour with no edit. Only the
wait path passes both. Rejected alternative: passing `_refs` as the registration registry, which would
grow the durable registry by every node of every poll — the exact cost the throwaway exists to avoid.

**4. Add `includeOffscreen`.** Additive, default `false` = today's behaviour, mirroring what SP1 added to
`desktop_wait_for` and what `desktop_snapshot` already has. It is the documented escape hatch that change 2
points at, so it must exist for that message to be actionable.

### Hazards, named

- **An unculled stability signature may never settle.** The SP1 sequencing note (memory, and this spec's
  predecessor) warns that off-window animation can churn forever, which is why `wait_for_stable` was left
  out of SP1. `includeOffscreen: true` is opt-in, and the tool already returns `{stable:false}` on timeout
  rather than hanging, so the failure mode is a truthful timeout. The tool description must say so.
- **A stale or foreign `ref`.** `refs.Resolve` throws `REF_NOT_FOUND` / `REF_STALE_UNRESOLVABLE`; both are
  existing, documented codes and surface through `ToolResponse.Guard`. No new error taxonomy.
- **Ref scoping cannot follow a re-created subtree.** If the app rebuilds the scoped element, the ref goes
  stale mid-wait and the call errors instead of silently re-scoping — correct, and the honest behaviour.
- **`Wakeable` is not computed for a rooted build** (`PerceptionManager.cs:433`). Irrelevant here:
  `WaitStableResult` (`WaitCoordinator.cs:16`) carries no `wakeable` field.
- **A reparented-but-alive root — LOW, accepted, recorded.** `RefRegistry.cs:148-153` returns the cached
  element when RuntimeId, ControlType, not-offscreen and Name all match, and it does not re-verify that the
  element's ancestry still reaches the original window. An element torn out of the window while keeping its
  RuntimeId (a dragged tear-off tab) could therefore be returned, and the wait would then measure a subtree
  in a different window. Rated LOW because UIA normally reissues a RuntimeId on cross-window reparenting, and
  because a dead element throws and falls through to the cache-free re-walk (`:151-152`). agy independently
  reached the same LOW rating. Not fixed here: tightening ref identity is a change to the resolution
  contract, which every tool shares — far outside an additive subproject.

---

## Item 5 — `desktop_list_terminal_tabs`

A new tool in `ContentTools.cs`, beside `DesktopReadTerminalTab`, annotated `ReadOnly = true` and wrapped
in `ToolResponse.Guard` (**not** `GuardWrite`):

```csharp
// ContentTools.cs — new tool
desktop_list_terminal_tabs(string window)
  -> { tabs: [ { index: int, title: string, active: bool } ], activeTabIndex: int }

// TerminalTabReader.cs — new pure-read entry point beside Run
public readonly record struct TabListing(int Index, string Title, bool Active);
public static (IReadOnlyList<TabListing> Tabs, int ActiveTabIndex) List(AutomationElement win);

// PerceptionManager.cs — query-STA hop, mirroring ReadTerminalTabAsync's shape (:391-399)
public Task<(IReadOnlyList<TerminalTabReader.TabListing> Tabs, int ActiveTabIndex)>
    ListTerminalTabsAsync(WindowHandle handle);
```

`List` reuses `EnumerateTabs` verbatim, reads `Name` (via the existing `NameOf`, which swallows faults to
`""`) and `IsSelected` per tab, and calls `Select` **never**.

**`activeTabIndex` is `-1` when no tab reports selected.** That is what `tabs.FindIndex(IsSelected)`
returns (`TerminalTabReader.cs:102` already relies on this, and `:112` treats `activeIndex >= 0` as
"something to restore"). `-1` is a real, reachable state — `IsSelected` `:62-66` returns `false` on any
COM fault — so it is specified rather than left to the implementer, and the description must state it.
This matches `desktop_read_terminal_tab`'s existing `activeTabIndex`, which can already return `-1` via
`NowActive` (`:195-196`).

**`-1` is deliberately AMBIGUOUS, and the wording must not over-claim.** `IsSelected` swallows every COM
fault to `false` (`:62-66`), so `-1` conflates "no tab is selected" with "the selection state could not be
read" — and if a future UIA or WT change stopped exposing `SelectionItemPattern` on tabs, this tool would
return a perfectly well-formed response with silently wrong data, the worst failure shape. The mitigation is
honest wording, not a fake distinction: the field means **"no tab reported itself selected"**, which is
literally true in both cases, and the description says exactly that rather than "no tab is selected". The
same reasoning is why `active` per tab is reported as *reported-selected* rather than as ground truth.
Distinguishing the two cases would need a separate "pattern unavailable" signal, which is a new wire field
and belongs to whoever needs it — not invented here on speculation.

Unlike `Run`, `List` runs on the **query** STA, not the transient action STA: it mutates nothing, so it
needs neither the action-STA hop nor the in-flight action cap. Use `RunWithWindowAndDesktopAsync`, the same
path `BuildModelAsync` uses (`PerceptionManager.cs:416`). Consequences, each the reason for the shape:

- **The index space matches `desktop_read_terminal_tab` by construction**, because it is produced by the
  same function. This is the decisive property. Both ROADMAP options were rejected for lacking it or for
  what they cost to get it.
- **No visible flicker and no `Restore` risk**, since nothing is selected.
- **Works under `--read-only-mode`** — it uses `ToolResponse.Guard`, not `GuardWrite` (`ContentTools.cs:94`).
- **No human-approval prompt** for pure discovery, because it is not annotated `Destructive`.

Rejected, with reasons recorded so they are not re-proposed:
- *Ordinal on the snapshot node descriptor* (`ROADMAP.md:319-321` option A) — the ordinal would be derived
  through five filters, four of which drop `TabItem`s, then consumed by an unfiltered
  `EnumerateTabs`. A published wire field that silently names the wrong tab.
- *Echo the map from `desktop_read_terminal_tab`* (option B) — correct index space, but discovery would
  require firing a `Destructive` tool that selects a tab: flicker, blocked in `--read-only-mode`, and a
  failed `Restore` can leave the user's terminal on the wrong tab.
- *A `tabIndex: -1` discovery sentinel* — repurposes a value that today throws `InvalidArguments`
  (`TerminalTabReader.cs:107-109`) and that the published description promises errors *"without switching
  on an out-of-range tabIndex"*; and it would still sit behind `GuardWrite` and the `Destructive`
  annotation. This project has already been bitten once this release by a negative int repurposed as a
  flag (`Task.Delay(-1)` = `Timeout.Infinite`, filed as
  `docs/fix-the-tool-backlog/negative-timeout-disables-the-sta-watchdog.md`).

### Hazards, named

- **`ROADMAP.md:136` — only the ACTIVE tab's buffer is populated.** This tool returns titles only. It must
  not imply it can read background buffers; `desktop_read_terminal_tab` remains the only way, and the
  description must point there.
- **Titles are a hint, never a filter.** The `driving-flaui-mcp` skill already teaches this, and
  `ROADMAP.md:116-125` records a live driver error caused by trusting a title. Handing an agent a tidy
  title list makes title-trusting *more* tempting, so the description must carry the same warning
  `desktop_read_terminal_tab`'s already does.
- **`EnumerateTabs` throws `PatternUnsupported` on a non-WT window.** Existing, correct behaviour; the new
  tool inherits it unchanged.
- **Non-unique titles are expected**, which is why `index` is the identity and `title` is descriptive.
- **Every title is untrusted, attacker-influenced text.** A tab title is set by whatever program runs in the
  tab, so this tool delivers N strings of guest-controlled content into an LLM's context in one call, where
  `desktop_read_terminal_tab` delivered one. Recorded as a **widening of an existing surface, not a new
  class**: that tool already returns a title, and it returns the entire terminal *buffer* — far more
  guest-controlled text — with no sanitization. Adding stripping only to titles would be inconsistent with
  the buffer path and would not reduce the real exposure, so no sanitization is specified here. The
  description must carry the same title-distrust warning the read tool's does, which is the mitigation that
  actually fits the threat (the driving skill already teaches it, and `ROADMAP.md:116-125` records a live
  driver error caused by trusting a title). Whole-surface treatment belongs with SP3's redaction work, not
  here. *(agy proposed stripping control characters; I could not verify that Windows Terminal ever exposes
  raw escape sequences in a `TabItem`'s UIA `Name` — it consumes the sequence and stores the resulting text
  — so a mitigation for an unmeasured threat is not specified. Recorded rather than silently dropped.)*

---

## Item 7 — retire the redaction, pin the guarantee

**Do not redact the stored descriptor `Name`.** The measured ground truth above shows it would make
exactly the targeted population unresolvable while protecting a value that is already never echoed.

Instead:

1. **Pin the never-echoed guarantee with a tripwire test**, in
   `test/FlaUI.Mcp.Tests/Perception/PasswordRedactionTests.cs` (the existing home for this concern; it
   currently holds one Desktop test, so the new headless facts join it rather than starting a new file).
   An `IsPassword` element whose `Name` carries a secret and which has no `AutomationId` must not leak that
   string into anything crossing the wire. Three distinct wire surfaces, each asserted separately because
   each is a different code path:
   - the rendered snapshot line (`SnapshotEngine.Render`),
   - `wait_for`'s match (`WaitCoordinator.Matches` `:85-91` — already redacts, pin it),
   - the `REF_STALE_UNRESOLVABLE` diagnostic message (`RefRegistry.Key` `:206-209` — already omits it).

   Assert both the secret's **absence** and `[REDACTED]`'s presence: absence alone would pass against an
   empty string, which is the vacuous-test trap this release already hit once.

   **AUDIT EXISTING COVERAGE FIRST — the redaction boundary is wider than those three surfaces, and several
   are already pinned.** `[REDACTED]` appears at `SnapshotEngine.cs:133`, `WaitCoordinator.cs:88`,
   `SnapshotDiff.cs:25`, `WatchPayloadBuilder.cs:34`, `PerceptionManager.cs:183/313/350/565`, and
   `FindQuery.cs:63`; and `DiffRedactionTests`, `WaitNameOracleTests`, `FindTests`,
   `WatchPayloadBuilderTests` and `SnapshotModelPinTests` all already reference it. Enumerate what is
   genuinely unpinned before writing anything — a duplicate test adds maintenance and false confidence, and
   this release already lost time to a test that asserted nothing.

   **Headless reachability, decided per surface rather than deferred:**
   - `SnapshotEngine.Render` — `public static`, headless. Write it.
   - `WaitCoordinator.Matches` — `internal static`, headless (`WaitNameOracleTests` already does this).
   - `RefRegistry.Key` — `private static` (`:208`). Reach it **through the thrown message**, which embeds
     `Key(d)` (`:195-197`), by driving `ResolveDescriptor` to fail. Do **not** widen the member's visibility
     for a test: agy proposed promoting it to `internal`, but the observable behaviour is the exception text,
     and asserting the real wire surface is both stronger and less invasive than relaxing an access modifier.
2. **Correct `ROADMAP.md:327-328`** to record why the entry as written was wrong, so it is not re-filed.
3. **Add the same fact where it will be read during a change**: a comment at `SnapshotEngine.cs:87` noting
   the raw `Name` is deliberate and load-bearing for `RefRegistry` resolution, with the redaction boundary
   named. The next person to "harden" this line is the risk being managed.

This closes the item with evidence rather than leaving it open, and follows SP0's precedent — a tripwire
where a claim was assumed.

### Hazard, named

**Accepted residual:** raw names of non-conformant password fields remain in the server's managed heap for
the snapshot's lifetime. Reachable only by an attacker who can already read this process's memory, which
implies a compromise that dwarfs it. Recorded as accepted, not overlooked. Note the whole-window
credential denylist (`PerceptionPolicy.IsDenied`, `PerceptionManager.cs:420-423`) is the actual floor for
real credential stores, and SP3 (item 9, per-field redaction) is where this surface gets its real work.

---

## Testing strategy

Mirroring SP0/SP1: pin the contract, and make every new test fail for the right reason before it passes.

- **Item 3 — headless where the logic allows.** `Subtree` and the precedence rules are pure. The
  culled-scope-root distinction needs a model whose scope element is culled: assert the two miss-path
  branches produce **different** errors, and that the success path's walk count is **unchanged** — the
  walk counters exist for exactly this (`WaitCoordinator.cs:36-41`, and SP1's lesson that asserting on the
  total cannot distinguish per-poll from once-per-call).
- **Item 3 — the registry split is the highest-risk edit**, because a default-argument mistake silently
  changes every existing call site. Pin that `SnapshotAsync` and the diff path still register into the
  registry they always did.
- **Item 5** — headless for the response shape and the index/title/active projection; Desktop for the live
  WT read. The **no-`Select`** property is the whole value of the tool, and a shape test would pass even if
  it selected every tab — so the property is guaranteed structurally rather than by an automated proof of
  absence, for the reason below.

  **The obvious assertion is a false-GREEN.** Checking that the originally-active tab is still active after
  the call passes even if the tool selected every tab and restored the original — that is precisely what
  `Run` already does, and WT applies selection asynchronously with a settle loop
  (`TerminalTabReader.cs:178-188`), so the end state is indistinguishable.

  **A UIA-event gate was considered and REJECTED as unbuildable.** Subscribing to the selection-changed
  event and failing if it fires looks like a proof of absence, but UIA event callbacks arrive on COM RPC
  threads (`Watch/Uia3EventSource.cs`), asynchronously and out of band. A synchronous `List` call can return,
  the test can assert "no event fired", and the queued event can land milliseconds later — a false GREEN. The
  only way to close that window is an arbitrary `Task.Delay`, which is a flaky gate, and this release has
  already been burned once by a timing-dependent test failure that turned out to be co-run interference.
  A gate that can pass while the property is violated is worse than no gate, because it manufactures
  confidence.

  **What ships instead — the no-`Select` property is made STRUCTURAL, and the tests assert what they
  actually can:**
  - `List` reaches no `Select` call by construction: `Select` (`TerminalTabReader.cs:68-73`) is reachable
    only from `Run`. Keep `List` free of it, and state in its docstring that this is load-bearing, not
    incidental — the next person to "helpfully" add a settle loop is the risk.
  - Headless: the projection, the index/title/active mapping, and `activeTabIndex == -1` when **no tab
    reports itself selected** (the wording matters — see the `-1` ambiguity above; a test named "when nothing
    is selected" would re-assert the over-claim the spec just removed).
  - Headless: **pin the new tool's anonymous JSON projection**, the way SP0 pinned `truncatedFrom` and
    `Hint`. Note the precedent is narrower than it looks — SP0 pinned two specific *fields* that had been
    dropped, not every tool's projection — so this is adopted because a brand-new projection with a nullable
    `activeTabIndex` and a nested array is exactly the shape that silently loses a field, not because a rule
    requires it.
  - Desktop: call `List` on a window whose active tab is **not** index 0, and assert `activeTabIndex` still
    reports that tab and the returned titles are complete. Necessary-but-not-sufficient, and labelled as
    such in the test so nobody mistakes it for a proof of absence.
  - The real gate is review-level, and the spec says so plainly rather than dressing it as automated.
- **Item 7** — the tripwire above. It must fail if the redaction boundary is removed; verify by mutation.
- **Gates:** headless suite green (772 passed / 0 skipped at `ac430b0`), plus every branch-touched Desktop
  class green in isolation on a physical console. The full Desktop suite runs on the **main thread only** —
  a ~10min suite against Bash's 600s cap is the A1a T8 trap.

## Documentation surfaces this touches

A new public tool and two changed signatures are not done when the code compiles. Each surface below is a
place the repo currently tells the truth about the tool set and would start lying:

- **`docs/agent-contract.md:73`** — a table enumerating every tool with its annotation and parameters.
  `desktop_list_terminal_tabs` must be added with `ReadOnly`, and `desktop_wait_for_stable`'s row must gain
  `scopeRef` and `includeOffscreen`. Omitting this leaves the contract doc silently incomplete, which is
  worse than absent because it reads as authoritative.
- **`docs/architecture-and-safety.md:36`** — classifies tools by lease requirement. The new tool is
  read-only and lease-free; it belongs with the perception tools, explicitly NOT with
  `desktop_read_terminal_tab`, which that line groups with the state-changing set.
- **`.claude/skills/driving-flaui-mcp/SKILL.md:241`** — teaches the terminal-tab recipe, which currently
  says to enumerate tabs via `desktop_snapshot`. That instruction becomes the second-best path once the new
  tool exists, so the recipe should name the new tool first while keeping the title-distrust rule.
  ⚠ **Two constraints here, both already bit this project.** The skill has a byte-identical twin at
  `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md`, pinned by
  `SkillLoadLineTests.The_two_tracked_copies_are_byte_identical` — editing one alone reds the headless gate.
  And its `AUTOTRAIN:GROWTH` region sits at **25 of a hard 30 lines**, so if any of this lands as a growth
  rule rather than in the hand-authored floor, it must fit or compress something.
- **`ROADMAP.md`** — mark item 5 done (pointing at the new tool and why both original options were
  rejected), correct item 7's premise per Item 7 above, and record item 3's outcome including the measured
  per-poll fixed-cost floor.
- **`CHANGELOG.md`** — no manual heading. `scripts/release.ps1` owns the heading and drafts the body from
  commits at release time; a hand-written entry here would collide with it. Verified during SP1; do not
  "fix" this.

**Verified NOT to exist — do not go looking for these.** A panel round asserted that adding a tool would
require updating a registered-tool-count assertion and a `--read-only-mode` allowlist test. Neither exists:
a search of `test/FlaUI.Mcp.Tests` for a tool-count assertion or a read-only-mode allowlist returns nothing.
Tools are discovered purely by attribute reflection (`ToolTrapFactInvariantTests.cs:46-50`), so a new tool
needs **no registration list update anywhere**. Recorded because "almost every project has one" is exactly
the kind of plausible claim that sends an implementer hunting for a file that was never there.

## Out of scope, deliberately

- **Any breaking change**, per the binding constraint. Selector scoping stays.
- **SP3** (item 9, per-field redaction) and **SP4** (items 4, 8, 10 — land one at a time; item 10
  `WM_RENDERFORMAT` is the estimate-blower, time-box it).
- **`desktop_list_terminal_processes`** (`ROADMAP.md:143-154`) — the zero-flicker process-identity tool,
  and its hard PID→ordinal last mile. Item 5 deliberately solves only ordinal *discovery*.
- **The `CacheRequest` walk optimization** (`ROADMAP.md:337-355`) — the biggest latency lever, measured,
  but it trades staleness for speed and must be weighed against `wait_for`'s never-report-a-wrong-belief
  contract. Item 3 gets its win by walking fewer nodes, not by caching reads.
- **Feature work** (ROADMAP Track B) — a separate brainstorm.

## Design forks, and how they were settled

Recorded so a later session does not reopen them.

| Fork | Settled | By |
|---|---|---|
| Item 5's shape | New read-only `desktop_list_terminal_tabs`; both ROADMAP options rejected | agy and I both recommended it; **user approved** |
| Item 3's depth | Scope **and** root the walk at the subtree, not just the signature | **user approved** |
| Fork A — the culled scope root | Additive hybrid: add `scopeRef` scoping, keep selector scoping and make it honest, add `includeOffscreen` | agy first picked deprecating selector scoping; **withdrawn** when given SP2's cannot-break-a-consumer constraint, which it agreed the deprecation fails |
| Fork B — item 7 | Retire the redaction **and** pin the guarantee | agy picked retire, I picked pin; both agreed they are complementary; **user approved** |
| The scope parameter's name | **`scopeRef`** — not `root`, not `ref` | Settled after two reversals across panel rounds 1→3. `root` was adopted for cross-tool consistency, then found to imply it scopes the returned snapshot; scoping that snapshot was then adopted to make `root` truthful, then reverted on ergonomics (a caller who waits on a subtree wants the whole window next) and minimal change. `scopeRef` states the actual effect. See Item 3's naming rationale for the full history |
| The no-`Select` gate for item 5 | **Structural + review-level**, not an automated proof of absence | A UIA-event gate was proposed and then withdrawn by agy: event callbacks arrive on COM RPC threads, so a test can assert "no event fired" before the queued event lands. A gate that can pass while the property is violated manufactures confidence |

agy's corrections I accepted: the `depth > 0` structural proof for the immune scope root, and that
redacting a resolver's lookup key is self-defeating. agy's claim I overturned: that the selector path
should be deprecated — it fails SP2's binding constraint, a fact agy did not have until I supplied it.
agy's cost objection to the confirmation walk was withdrawn after tracing the miss path; I verified the
zero-extra-walks-on-success claim myself rather than accepting it.
