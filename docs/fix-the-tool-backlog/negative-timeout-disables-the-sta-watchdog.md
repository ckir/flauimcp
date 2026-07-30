# negative-timeout-disables-the-sta-watchdog — `timeoutMs: -1` silently removes the STA hang guard

- **Captured:** 2026-07-29 (adversarial panel round 5, Adversarial Consumer seat — found in `WaitCoordinator`, swept from there)
- **Regression test:** none yet — see Test-gen note
- **Trait:** `Category=KnownDefect` (headless) once written

## Steps to Reproduce

Any `desktop_*` tool that accepts a `timeoutMs` and dispatches through `AutomationDispatcher`
(`desktop_get_text`, `desktop_get_grid_cell`, `desktop_read_terminal_tab`, …):

```
desktop_get_text window:"w1" ref:"e5" timeoutMs:-1
```

`AutomationDispatcher.AwaitWithTimeout` (`src/FlaUI.Mcp.Core/Threading/AutomationDispatcher.cs:56`) does:

```csharp
var done = await Task.WhenAny(work, Task.Delay(timeoutMs));
```

`Task.Delay(-1)` is `Timeout.Infinite` — it never completes. `WhenAny` therefore reduces to awaiting
`work` alone, which is precisely the case the watchdog exists to bound: a UIA call parked on an
unresponsive window. The tool call hangs until the process is killed.

Only `-1` is dangerous. Every other negative throws `ArgumentOutOfRangeException`, which surfaces as a
normal tool error — wrong, but loud. `-1` fails silently and looks like a slow app.

Related but ALREADY FIXED, and the reason this was found: `WaitCoordinator` had the same shape via
`Task.Delay(pollIntervalMs)`, where `pollIntervalMs: -1` parked the wait loop forever. That one is
clamped (`WaitCoordinator.SafeDelayMs`). Two sibling sites were checked and are safe:
`Vision/TextWaiter.cs:21` already clamps with `Math.Max(1, minIntervalMs)`, and
`Overlay/GdiActionOverlay` takes its duration from a server command-line argument guarded `> 0`
(`Program.cs:44`), not from a tool call.

## Code-level Mitigation

Clamp at the dispatcher, so every caller inherits the guard rather than each tool repeating it:

```csharp
private static async Task<T> AwaitWithTimeout<T>(Task<T> work, int timeoutMs)
{
    // A caller-supplied -1 is Timeout.Infinite, which turns this watchdog into a plain await and
    // removes the only bound on a parked UIA call. Clamp to a sane floor.
    var done = await Task.WhenAny(work, Task.Delay(timeoutMs < 0 ? DefaultTimeoutMs : timeoutMs));
    ...
```

### DESIGN RESOLVED 2026-07-30 (agy-consulted, every claim below re-verified by measurement)

**THE DEFECT IS BIGGER THAN THIS FILE ORIGINALLY SAID. Two corrections:**

**1. `-1` is NOT the only dangerous value.** `Task.Delay(int)` accepts any non-negative `int`, so
`timeoutMs: int.MaxValue` (~24.8 days) parks the watchdog just as effectively as `Timeout.Infinite` — and it
is a *legal* argument that throws nothing. The real hazard is "any budget that outlives the operator", not
the single value `-1`. A lower-bound guard alone therefore does NOT close this.

**2. The blast radius is the whole ACTION SURFACE, not one hung call.** `RunActionAsync` holds a slot from
`Interlocked.Increment` (`AutomationDispatcher.cs:25`) until the worker thread's `finally`
(`:42`) — which never runs while the UIA call is parked. `MaxPendingActions = 5` (`:15`), so **five**
such calls exhaust the pool and every subsequent action throws `TooManyPendingActions` (`:28-31`)
for the life of the process. That is a denial of every action tool, reachable from five tool calls.
Confirmed scope: 7 tool files pass `timeoutMs` through this path (`ContentTools`, `FindTextTools`,
`InputTools`, `InteractionTools`, `SnapshotTools`, `WatchTools`, `WindowTools`).
Not affected: `RunQueryAsync` (`:20-21`) takes no timeout and never reaches `AwaitWithTimeout`.

**F1 — a negative must be REFUSED (`InvalidArguments`), not clamped.** Clamping to a floor of 0 is actively
harmful: `Task.Delay(0)` completes immediately, so `done != work` and the caller gets
`ActionBlockedPending` — *"it likely opened a modal dialog"* (`:62-65`) — for a bug that was really their own
argument. Sending someone hunting a nonexistent dialog is worse than the hang. Refusing does change the
error surface of shipped tools, but from *silent hang* to *named error*, and no caller can be relying on a
hang.

**F3 — an UPPER bound is required, and it should CLAMP, following the house idiom.** Precedent already
exists and the consult missed it: `WaitForForeground.ClampTimeout` (`Attention/WaitForForeground.cs:24-25`)
enforces a `HardCapMs` and the tool description *documents* it ("server-capped to 45s",
`FindTextTools.cs:128,131`). So the pattern is a named, testable clamp helper plus disclosure.
⚠ **Do NOT copy that helper's semantics.** It maps invalid input to the MAXIMUM
(`requestedMs > 0 && <= HardCapMs ? requestedMs : HardCapMs`), which is benign for a bounded foreground wait
but exactly wrong for a hang watchdog — a negative would become a 60 s parked slot.
The asymmetry is principled, not inconsistent: **a negative has no valid interpretation (refuse); an
excessive value has an obvious one — "wait as long as you can" — so clamp and say so.**
Pick the cap above the largest shipped default, which is **45000** (`FindTextTools.cs:131`); 60000 leaves
15 s of headroom. Re-check that number before coding — a new default above 45 s would move it.

**F5 — no accessibility change needed.** `AwaitWithTimeout` can stay `private static` (`:54`). The public
`RunActionAsync` (`:23`) takes a `Func<T>`, so a test passes a delegate that blocks forever plus a bad
timeout and asserts a fast, named throw. A tool-surface repro would hang by construction; this does not.

**Still open for the USER:** the exact cap value, and whether refusing negatives on 7 tool files' worth of
surface is acceptable in one change.

## Test-gen note (only if no runnable test was generated)

The repro is expressible as a `desktop_*` call, but a Tier-2 partial repro would HANG the test run by
construction — the defect is an unbounded await, so an un-fixed test cannot fail fast. A regression test
should be written against `AwaitWithTimeout` directly, asserting that a negative timeout still completes
within a bounded period when the work never finishes, rather than driving the tool surface.
