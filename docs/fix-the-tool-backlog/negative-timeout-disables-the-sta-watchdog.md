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

Deliberately unchosen: whether a negative should clamp to the tool's default, clamp to a fixed floor, or
be rejected up front as `InvalidArguments`. Rejecting is the most honest (the caller asked for something
incoherent) but changes the error surface of several shipped tools at once; clamping preserves them.

Worth deciding alongside it: whether `timeoutMs` should also carry an upper bound. The documented threat
model has the MCP client itself possibly prompt-injected, and an unbounded `timeoutMs` is a legitimate-
looking way to occupy the single query STA indefinitely.

## Test-gen note (only if no runnable test was generated)

The repro is expressible as a `desktop_*` call, but a Tier-2 partial repro would HANG the test run by
construction — the defect is an unbounded await, so an un-fixed test cannot fail fast. A regression test
should be written against `AwaitWithTimeout` directly, asserting that a negative timeout still completes
within a bounded period when the work never finishes, rather than driving the tool surface.
