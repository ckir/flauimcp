# A1a — Hermetically Green Desktop Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take the `Category=Desktop` test suite from 6 failed / 102 passed to green, and keep it green on any dev machine and on a second consecutive run.

**Architecture:** Repair the shared WPF test fixture's root defect once — its single ~890 DIP column overflows a clamped window, so the bottom of the tree is spatially culled — then repair each failing test. Work lands in dependency order: budget raises first (they cannot break anything and they immunise the suite against the walk-cost inflation later tasks cause), then the two fixture-independent test repairs, then the fixture restructure, then the tests that depend on it.

**Tech Stack:** .NET 10, xUnit + Xunit.SkippableFact, WPF (the TestApp fixture), FlaUI/UIA3, Win32 `SendInput`.

**Source spec:** `docs/superpowers/specs/2026-07-28-a1a-desktop-suite-design.md` — approved, adversarial-panel GREEN at round 14. Read its **Design** section (D1–D7) before starting; the ledgers after it are review history and can be skipped.

**Binding constraint:** tests and test-fixture only. **No `src/` changes.** If a task appears to require one, stop and report it rather than making it.

---

## Preconditions for every task that runs the suite

- A **physical console** session — not RDP. `SendInput` does not deliver over RDP.
- An **active input lease**: ask the user to run `flaui-mcp unlock --minutes N --allow-shells`. Only a human can grant this; `--allow-shells` is mandatory or the terminal-tab tests fail `SinkInterlocked` for an unrelated reason.
- The user must step away from the machine — the suite drives real mouse and keyboard.
- Before and after every pass, kill orphans: `testhost.exe`, `FlaUI.Mcp.TestApp.exe`, and any `Code.exe` whose command line points at the suite's own profile parent directory (**not** `Code.exe` by bare name — see Task 8).

Tasks 1–2 and 5–7 do not need a lease to *write*, only to *run the suite*. Task 3–4 need a running VS Code. Plan lease requests accordingly rather than holding one open for the whole job.

## File structure

| File | Exists? | Responsibility | Tasks |
|---|---|---|---|
| `test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs` | yes, 45 lines | #3's budget + the false-assurance sibling repair | 1 |
| `test/FlaUI.Mcp.Tests/Server/WaitForTests.cs` | yes, 56 lines | #6's budget | 1 |
| `test/FlaUI.Mcp.Tests/Presence/PresenceDesktopTests.cs` | yes, 17 lines | #2 — synthesise input instead of assuming a human typed | 2 |
| `test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs` | yes, 124 lines | #1 — instance isolation, hydration polling, teardown | 3, 4 |
| `test/FlaUI.Mcp.TestApp/MainWindow.xaml` | yes, 104 lines | the shared fixture — three-column restructure | 5 |
| `test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs` | yes, 85 lines | rebuild handler must recreate all six items | 6 |
| `test/FlaUI.Mcp.Tests/Perception/FixtureIntegrityTests.cs` | **no — create** | D7, the containment regression guard | 7 |

---

### Task 1: Raise the wait budgets (spec D4 + D6)

Lands first because it is pure widening — it cannot turn a passing test red — and because Tasks 5–6 inflate the UIA node count, which raises the per-walk cost `P` for every wait in the suite. Landing them in the other order turns #6 red for a reason unrelated to the change under test, and the suite stops being bisectable.

Measured background: one full walk of the 94-node window costs **P ≈ 3.0 s**. `WaitForStableAsync` needs `ceil(quietMs/pollIntervalMs)` identical polls plus a final confirming walk, so `quiet=500, poll=250` has a floor of ~4 × P ≈ 12.5 s — against a 5000 ms budget it was unreachable by 2.5× even with a perfectly static tree.

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs:22` and `:27-32`
- Modify: `test/FlaUI.Mcp.Tests/Server/WaitForTests.cs:38`

- [ ] **Step 1: Raise #3's budget and record why in-test**

In `WaitForStableTests.cs`, replace lines 18–24 with:

```csharp
    [Fact]
    public async Task Structure_is_stable_despite_a_live_ticker()
    {
        // Budget: one full walk of this window measures P ~ 3.0s. WaitForStableAsync needs
        // ceil(quiet/poll) = 2 identical polls, i.e. 3 polls, PLUS a final confirming walk => a
        // ~4P ~ 12.5s floor. The old 5000ms budget was unreachable by 2.5x even against a
        // perfectly static tree. 25000ms leaves headroom for the node-count growth that the
        // fixture restructure and the added ListItems introduce.
        var (snap, handle) = await Setup();
        var json = await snap.DesktopWaitForStable(handle, null, null, false, 500, 25000, 250);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("stable").GetBoolean());
    }
```

- [ ] **Step 2: Repair the false-assurance sibling**

`IncludeText_on_a_live_ticker_times_out_unstable` currently asserts only that a wait times out, with a 1500 ms budget. Against P ≈ 3.0 s that is structurally guaranteed — poll #1 alone overruns it — so the test passes whether or not the ticker is destabilising anything. It would pass against a frozen window. Replace lines 26–32 with:

```csharp
    [Fact]
    public async Task IncludeText_on_a_live_ticker_times_out_unstable()
    {
        var (snap, handle) = await Setup();

        // The real claim: with includeText the live ticker's text changes every 120ms, so the
        // signature never repeats and the wait cannot settle even given a generous budget.
        var withText = await snap.DesktopWaitForStable(handle, null, null, true, 500, 25000, 250);
        Assert.False(JsonDocument.Parse(withText).RootElement.GetProperty("stable").GetBoolean());

        // Control: the SAME window under the SAME budget DOES settle without text, which is what
        // makes the assertion above meaningful. Without this the test passed on a timeout alone and
        // would have passed against a frozen tree -- coverage that could not fail.
        var withoutText = await snap.DesktopWaitForStable(handle, null, null, false, 500, 25000, 250);
        Assert.True(JsonDocument.Parse(withoutText).RootElement.GetProperty("stable").GetBoolean());
    }
```

- [ ] **Step 3: Raise #6's budget**

In `WaitForTests.cs`, replace line 38:

```csharp
        var json = await snap.DesktopWaitFor(handle, "automationId", "DelayedLabel", "exists", null, 5000, 500);
```

with:

```csharp
        // The label appears 600ms after the invoke -- after poll #1 has already enumerated
        // RootPanel's children -- so satisfaction lands on poll #2 at ~6.5s against P ~ 3.0s.
        // The old 5000ms budget survived only because WaitForAsync checks its deadline AFTER an
        // unsatisfied poll, so it cannot return false before one full walk completes. That is a
        // machine-speed assumption, and the fixture changes raise P. 25000ms removes it.
        var json = await snap.DesktopWaitFor(handle, "automationId", "DelayedLabel", "exists", null, 25000, 500);
```

- [ ] **Step 4: Build**

Run: `dotnet build -c Release test/FlaUI.Mcp.Tests`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Run the three affected tests**

Run:
```
dotnet test -c Release --filter "FullyQualifiedName~WaitForStableTests|FullyQualifiedName~WaitForTests"
```
Expected: `Structure_is_stable_despite_a_live_ticker` **passes** (it was failing on budget alone). `IncludeText_on_a_live_ticker_times_out_unstable` **passes**. `Delayed_control_becomes_satisfied_with_a_ref` **still fails** — it is blocked by the spatial cull, which Task 5 fixes; do not chase it here. `Missing_control_times_out_as_data` passes.

If `Structure_is_stable_despite_a_live_ticker` still fails, capture the full output (not grep-filtered) and stop — the measured floor has moved and the budget needs re-deriving before continuing.

- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs test/FlaUI.Mcp.Tests/Server/WaitForTests.cs
git commit -m "test(desktop): raise wait budgets past the measured walk-cost floor

One full walk of the TestApp window costs P ~ 3.0s, so wait_for_stable's
ceil(quiet/poll) polls plus its confirming walk floor at ~4P ~ 12.5s. The
5000ms budget was unreachable by 2.5x against a perfectly static tree.

Also repairs IncludeText_on_a_live_ticker_times_out_unstable, which asserted
only a timeout under a 1500ms budget that one poll already overruns -- it
would have passed against a frozen window. It now also asserts the same
window settles WITHOUT text under the same budget, so it can only pass when
the ticker is genuinely the cause."
```

---

### Task 2: Make the presence test synthesise its own input (spec D3)

`PresenceDesktopTests.cs:12-15` reads the real OS-wide `GetLastInputInfo` clock and asserts a **human** typed less than 60 s ago. It synthesises nothing. It fails whenever nobody has touched the machine recently — which is exactly the state the suite requires, since the user must step away.

Two things are load-bearing and neither is free choice:

1. **The mechanism must be Win32 `SendInput`.** `GetLastInputInfo` reports the last *low-level hardware* input event. A UIA `InvokePattern` call or a WPF `RaiseEvent` does not touch that clock, so either would leave the test failing.
2. **The input must be null-effect.** `SendInput` delivers to whatever window holds focus — possibly the developer's own editor. Synthesise the smallest event that moves the clock and nothing else. Never a character keystroke.

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Presence/PresenceDesktopTests.cs` (whole file)

Verified references this task depends on, all `public` in `src/FlaUI.Mcp.Core/Interaction/Win32Interop.cs`: `MOUSEEVENTF_MOVE = 0x0001` (line 18), `SendInput(uint, INPUT[], int)` (line 34), `INPUT` (line 51), `InputUnion` (line **54**), `MOUSEINPUT` (line 65). `FileLeaseProvider` is in the same `FlaUI.Mcp.Core.Interaction` namespace.

- [ ] **Step 1: Replace the file**

```csharp
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

// CONSOLE-MACHINE-ONLY: fires real SendInput; needs an active unlocked session + a granted lease.
// Carries the SyntheticInput trait for the same reason InputToolsTests does.
[Trait("Category", "Desktop")]
[Trait("Category", "SyntheticInput")]
public class PresenceDesktopTests
{
    private static bool InputLocked()
    {
        var lease = new FileLeaseProvider().Read(out _);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        return lease is null || !lease.IsValidNow(System.DateTime.UtcNow, sid);
    }

    /// <summary>
    /// A zero-delta relative mouse move: the smallest event that advances the GetLastInputInfo
    /// clock without delivering anything meaningful to whatever window currently has focus.
    /// Deliberately NOT a keystroke -- SendInput goes to the focused window, which during a suite
    /// run may be the TestApp, a launched VS Code, or the developer's own editor.
    /// </summary>
    private static void NudgeIdleClock()
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = 0, // INPUT_MOUSE
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0, dy = 0, mouseData = 0,
                        dwFlags = Win32Interop.MOUSEEVENTF_MOVE,
                        time = 0, dwExtraInfo = 0
                    }
                }
            }
        };
        var sent = Win32Interop.SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        Assert.Equal(1u, sent); // 0 means the input was blocked (UIPI/lease) -- a real failure
    }

    [SkippableFact]
    public void Real_idle_source_reports_active_right_after_input()
    {
        Skip.If(InputLocked(), "no active input lease — grant one on a console with `flaui-mcp unlock`");

        NudgeIdleClock();

        var idle = new Win32IdleSource().IdleMs();
        Assert.True(idle >= 0);
        Assert.True(idle < 60_000, $"expected the synthesised input to move the idle clock, got {idle} ms");
        Assert.Equal(Activity.Active, IdleActivity.Bucket(idle, 60_000, 300_000));
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release test/FlaUI.Mcp.Tests`
Expected: `Build succeeded`. If `INPUT`, `InputUnion` or `MOUSEINPUT` are not accessible, **stop and report** — making them accessible would be a `src/` change, which the constraint forbids.

- [ ] **Step 3: Measure that the mechanism actually works**

This is the step that decides the task. Run with a lease held:

Run: `dotnet test -c Release --filter "FullyQualifiedName~PresenceDesktopTests" -v n`
Expected: PASS.

**If it fails on the `idle < 60_000` assertion**, the zero-delta move was coalesced or ignored by the OS and did not advance the clock. Do **not** route around it by weakening the assertion or by reaching for the retired lease-free fallback. Escalate to the user with the measured idle value, and try in this order: a 1-pixel relative move followed by its inverse (net-zero cursor displacement, two real events), then a modifier-only key event (`VK_SHIFT` down+up). Report which one moved the clock.

- [ ] **Step 4: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Presence/PresenceDesktopTests.cs
git commit -m "test(desktop): presence test synthesises its own input

It read the real OS-wide GetLastInputInfo clock and asserted a HUMAN had
typed within 60s, while synthesising nothing -- so it failed whenever nobody
had touched the machine, which is the state the suite requires.

Uses Win32 SendInput because GetLastInputInfo tracks low-level hardware
input only: a UIA invoke or a WPF RaiseEvent does not move that clock. The
event is a zero-delta relative mouse move -- the smallest thing that advances
the clock without delivering a keystroke to whatever window has focus."
```

---

### Task 3: Instrument `DesktopWakeTests` before repairing it (spec D2)

**The recorded root cause for failure #1 is not supported by its own measurement, so this task establishes the cause and changes nothing else.** The story was "VS Code is single-instance, so `LaunchAppAsync` returns early and the fixed `Delay(1500)` measures too soon". But the warm re-run failed in 8 s with `got 16` **after the `< 30` opaque-baseline assert had already passed** — so a window *was* resolved and *was* opaque. Resolution succeeded; hydration failed. Replacing the delay with a 20 s poll would turn a fast red into a slow red and fix nothing.

`WindowManager.cs:400,424-432` shows `LaunchAppAsync` snapshots pre-existing PIDs and accepts only the launched PID or a **new** same-named one, so it cannot latch onto an ambient window — but it can return a `(handle, pid)` pair whose `pid` is a transient launcher while the window belongs elsewhere, and `WakeAsync(handle.Id, pid)` would then wake a process that does not own the window being measured. That reproduces `got 16` exactly.

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs:71-90`

- [ ] **Step 1: Add diagnostics to the failing test only**

Replace the body of `Waking_hydrates_the_tree_while_held` (lines 71–90) with:

```csharp
    [Fact]
    public async Task Waking_hydrates_the_tree_while_held()
    {
        using var rig = await LaunchAsync();

        // DIAGNOSTIC (temporary, removed in the follow-up task): the recorded root cause for this
        // failure is not supported by the measurement -- the opaque-baseline assert below PASSED
        // before the hydration assert reported 16 nodes, so a window was resolved and hydration,
        // not resolution, is what failed. Establish whether the pid we wake actually owns the
        // window we measure.
        var owningPid = await rig.Windows.RunWithWindowAndDesktopAsync(rig.Handle,
            (win, _) => win.Properties.ProcessId.ValueOrDefault);
        System.Console.WriteLine($"[wake-diag] launchedPid={rig.Pid} owningPid={owningPid} match={rig.Pid == owningPid}");

        var before = await rig.Perception.StatsByWindowAsync(rig.Handle);
        System.Console.WriteLine($"[wake-diag] opaque baseline={before.Total}");
        Assert.True(before.Total < OpaqueNodeCeiling,
            $"expected an opaque baseline (<{OpaqueNodeCeiling} nodes), got {before.Total}");

        var wakeId = await rig.Wake.WakeAsync(rig.Handle.Id, rig.Pid);

        for (var i = 1; i <= 10; i++)
        {
            await Task.Delay(2000);
            var poll = await rig.Perception.StatsByWindowAsync(rig.Handle);
            System.Console.WriteLine($"[wake-diag] t={i * 2}s nodes={poll.Total}");
        }

        var after = await rig.Perception.StatsByWindowAsync(rig.Handle);
        Assert.True(after.Total > WokenNodeFloor,
            $"expected the tree to hydrate while held (>{WokenNodeFloor} nodes), got {after.Total}");

        await rig.Wake.ReleaseAsync(wakeId);
    }
```

- [ ] **Step 2: Run it twice — cold, then immediately warm — and capture FULL output**

Close all VS Code windows first. Then:

```
dotnet test -c Release --filter "FullyQualifiedName~Waking_hydrates_the_tree_while_held" -v n
dotnet test -c Release --filter "FullyQualifiedName~Waking_hydrates_the_tree_while_held" -v n
```

Do **not** pipe through a narrow grep — the previous session lost this failure's assert message to a grep filter and had to re-run the whole suite to recover it.

- [ ] **Step 3: Read the diagnostics and decide**

Three outcomes, and they lead to different follow-ups:

| `[wake-diag]` shows | Meaning | Task 4 does |
|---|---|---|
| `match=False` on the warm run | The wake targets a process that does not own the measured window | Instance isolation is the fix, and the PID guard becomes a hard assert |
| `match=True`, node count flat at ~16 across all 10 polls | Hydration never starts — AXMode is not activating for this instance | Instance isolation is still the fix; report that timing was never the issue |
| `match=True`, node count climbing but slowly past 1500 ms | Genuinely a timing problem | Poll-until-hydrated is the fix |

Record which one occurred. **Report it to the user before proceeding** — it determines whether the spec's D2 remedy is right for the right reason.

- [ ] **Step 4: Commit the instrumentation**

```bash
git add test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs
git commit -m "test(desktop): instrument the wake-hydration failure before repairing it

The recorded root cause is not supported by its own measurement: the opaque
baseline assert passed before the hydration assert reported 16 nodes, so a
window was resolved and was opaque -- resolution succeeded and hydration
failed. Log the launched pid, the window's owning pid, and the node count
every 2s so the follow-up fixes the actual mechanism."
```

---

### Task 4: Repair `DesktopWakeTests` (spec D2)

**Gated on Task 3's measurement.** Do not start until the diagnostic outcome is recorded and reported.

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs:23` (path), `:53-69` (`LaunchAsync`), `:41-50` (`Rig.Dispose`), `:71-90` (the test)

Four independent changes; each has a stated reason and none is optional.

**(a) Resolve the VS Code path.** Line 23 pins `C:\Program Files\Microsoft VS Code\Code.exe`. A user-local install or none at all fails immediately, which by itself defeats hermeticity. Probe machine-wide → user-local → `PATH`; if none resolves, fail with a message naming what to install.

**(b) Isolate the instance.** Pass `--user-data-dir <fresh dir> --new-window`.

**(c) Suppress the first-run experience.** A pristine `--user-data-dir` is not a clean tree, it is the *first-run* tree — trust prompts, welcome tabs and extension toasts steal focus and change the node count, which is exactly what the assertion measures. Without this, the single-instance race is merely traded for a first-run race.

**(d) Poll against an ADDITIVE threshold.** `after.Total >= before.Total + 100`. **Never multiplicative.** Hydration adds a roughly fixed population — the Chromium document's accessibility tree — it does not scale the shell's node count. A `5 ×` threshold inverts into a false green on exactly this defect: with a 3-node baseline it demands only 15, and the measured failure state was `got 16`, so the poll would satisfy on the broken tree and pass. The spike this test was built on measured 14 opaque / 231 woken, i.e. ~217 added.

- [ ] **Step 1: Replace the path constant and `LaunchAsync`**

Replace line 23 and lines 53–69 with:

```csharp
    private static readonly string[] VsCodeCandidates =
    {
        @"C:\Program Files\Microsoft VS Code\Code.exe",
        System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            @"Programs\Microsoft VS Code\Code.exe"),
    };

    private static string ResolveVsCode()
    {
        foreach (var c in VsCodeCandidates)
            if (System.IO.File.Exists(c)) return c;

        var onPath = (System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator)
            .Select(d => System.IO.Path.Combine(d.Trim(), "Code.exe"))
            .FirstOrDefault(System.IO.File.Exists);
        if (onPath is not null) return onPath;

        throw new System.InvalidOperationException(
            "VS Code not found. These Desktop tests need it as an Electron/Chromium fixture. " +
            "Install it machine-wide or user-local, or put Code.exe on PATH.");
    }

    /// <summary>Parent for all throwaway profiles; swept on start (see SweepStaleProfiles).</summary>
    private static string ProfileParent => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "flaui-mcp-wake-profiles");

    private static async Task<Rig> LaunchAsync()
    {
        SweepStaleProfiles();

        // GUID, not pid: --user-data-dir must be built BEFORE Process.Start(), but the OS assigns
        // the pid only AFTER the process exists, so a pid-named directory is circular. The owner
        // record is written into the directory at creation and updated once the pid is known.
        var profile = System.IO.Path.Combine(ProfileParent, System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(profile);
        System.IO.File.WriteAllText(System.IO.Path.Combine(profile, "owner.txt"), "launching");

        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var registry = new WakeRegistry();
        var source = new Uia3EventSource(mgr);
        var wake = new WakeService(source, registry, mgr);
        var perception = new PerceptionManager(mgr, refs, new SnapshotCache());

        // A pristine --user-data-dir yields the FIRST-RUN tree, not a clean one: trust prompts,
        // welcome/release-notes tabs and extension toasts steal focus and change the node count,
        // which is exactly what the hydration assertion measures.
        var args = string.Join(' ',
            $"--user-data-dir \"{profile}\"",
            "--new-window",
            "--disable-workspace-trust",
            "--skip-welcome",
            "--skip-release-notes",
            "--disable-telemetry",
            "--disable-extensions");

        // 40s, not 20s: a first-run launch is slower than the 8-15s measured on a warm profile.
        var (handle, pid) = await mgr.LaunchAppAsync(ResolveVsCode(), args, 40000);

        System.IO.File.WriteAllText(System.IO.Path.Combine(profile, "owner.txt"),
            $"{pid}|{System.Diagnostics.Process.GetProcessById(pid).StartTime.Ticks}");

        return new Rig
        {
            Dispatcher = dispatcher, Windows = mgr, Registry = registry, Wake = wake,
            Perception = perception, Handle = handle, Pid = pid, ProfileDir = profile
        };
    }

    /// <summary>
    /// Reap abandoned profiles. Liveness is the sole retention criterion; age only decides how
    /// aggressively DEAD profiles are reaped, and never overrides a live owner -- a developer
    /// paused on a breakpoint keeps a genuinely live instance for hours.
    /// </summary>
    private static void SweepStaleProfiles()
    {
        if (!System.IO.Directory.Exists(ProfileParent)) return;

        foreach (var dir in System.IO.Directory.GetDirectories(ProfileParent))
        {
            var record = System.IO.Path.Combine(dir, "owner.txt");
            var text = System.IO.File.Exists(record) ? System.IO.File.ReadAllText(record) : null;

            // No record yet, or still "launching": genuinely UNKNOWN, not dead. A concurrent run
            // may be mid-launch. Leave it alone until it ages out.
            if (text is null || text == "launching")
            {
                if (System.IO.Directory.GetCreationTimeUtc(dir) < System.DateTime.UtcNow.AddHours(-3))
                    TryDelete(dir);
                continue;
            }

            // A record EXISTS but cannot be verified => provably not ours (our own process is
            // inspectable by us), so the directory is dead-owner and sweepable. Every failure mode
            // means "not ours" and none may escape this loop: GetProcessById throws
            // ArgumentException when the process is gone, StartTime throws Win32Exception when the
            // pid has been recycled onto an elevated process a non-elevated runner cannot inspect,
            // and InvalidOperationException when it exits mid-check.
            var live = false;
            try
            {
                var parts = text.Split('|');
                using var p = System.Diagnostics.Process.GetProcessById(int.Parse(parts[0]));
                live = !p.HasExited && p.StartTime.Ticks == long.Parse(parts[1]);
            }
            catch { live = false; }

            if (!live) TryDelete(dir);
        }
    }

    private static void TryDelete(string dir)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { System.IO.Directory.Delete(dir, recursive: true); return; }
            catch { System.Threading.Thread.Sleep(200 * (attempt + 1)); }
        }
    }
```

Add `public required string ProfileDir { get; init; }` to the `Rig` class alongside `Pid`.

- [ ] **Step 2: Replace `Rig.Dispose` (lines 41–50)**

```csharp
        public void Dispose()
        {
            // Order matters and every part of it is load-bearing.
            // 1. Open and HOLD handles for the whole tree BEFORE signalling termination. Querying
            //    the tree after Kill returns nothing useful (parent gone, children reparented), and
            //    GetProcessById on an already-dead child throws -- which would crash teardown
            //    before the delete loop runs. A handle opened before the kill stays valid after
            //    the process exits; a raw integer pid does not.
            var held = new System.Collections.Generic.List<System.Diagnostics.Process>();
            try
            {
                var root = System.Diagnostics.Process.GetProcessById(Pid);
                held.Add(root);
                held.AddRange(System.Diagnostics.Process.GetProcessesByName("Code")
                    .Where(p => { try { return p.Id != Pid && !p.HasExited; } catch { return false; } }));
            }
            catch { /* already gone */ }

            // 2. Kill the tree.
            try { if (held.Count > 0 && !held[0].HasExited) held[0].Kill(entireProcessTree: true); }
            catch { }

            // 3. WaitForExit on EVERY held handle, not just the root. Kill is non-blocking, and
            //    WaitForExit blocks only on the handle it is called on -- waiting on the root alone
            //    returns while the GPU/renderer children still hold the profile's SQLite locks.
            foreach (var p in held)
            {
                try { p.WaitForExit(5000); } catch { }
                try { p.Dispose(); } catch { }
            }

            try { Windows.Dispose(); } catch { }
            try { Dispatcher.Dispose(); } catch { }

            // 4. Delete with backoff -- the observable to poll is "the directory is gone".
            TryDelete(ProfileDir);
        }
```

- [ ] **Step 3: Replace the test body with poll-until-hydrated**

Replace `Waking_hydrates_the_tree_while_held` (the instrumented version from Task 3) with:

```csharp
    [Fact]
    public async Task Waking_hydrates_the_tree_while_held()
    {
        using var rig = await LaunchAsync();

        var owningPid = await rig.Windows.RunWithWindowAndDesktopAsync(rig.Handle,
            (win, _) => win.Properties.ProcessId.ValueOrDefault);
        Assert.True(rig.Pid == owningPid,
            $"the launched process must own the measured window; launched={rig.Pid} owning={owningPid}. " +
            "A mismatch means --user-data-dir isolation failed and the wake targets the wrong process.");

        var before = await rig.Perception.StatsByWindowAsync(rig.Handle);
        Assert.True(before.Total < OpaqueNodeCeiling,
            $"expected an opaque baseline (<{OpaqueNodeCeiling} nodes), got {before.Total}");

        var wakeId = await rig.Wake.WakeAsync(rig.Handle.Id, rig.Pid);

        // ADDITIVE, never multiplicative: hydration adds a roughly fixed population (the Chromium
        // document's accessibility tree), it does not scale the shell's node count. A relative
        // threshold inverts into a false green on exactly this defect -- with a 3-node baseline a
        // 5x rule demands only 15, and the measured failure state was 16.
        var target = before.Total + HydrationDelta;
        var deadline = System.DateTime.UtcNow.AddSeconds(20);
        var after = before;
        while (System.DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            after = await rig.Perception.StatsByWindowAsync(rig.Handle);
            if (after.Total >= target) break;
        }

        Assert.True(after.Total >= target,
            $"expected the tree to hydrate while held: baseline={before.Total}, " +
            $"needed>={target}, got={after.Total}");

        await rig.Wake.ReleaseAsync(wakeId);
    }
```

Replace the `WokenNodeFloor` constant (line 29) with:

```csharp
    // The spike this test was built from measured 14 nodes opaque / 231 woken -- ~217 added.
    // 100 sits far below that and far above any non-hydrated reading.
    private const int HydrationDelta = 100;
```

Leave `OpaqueNodeCeiling = 30` unchanged; instance isolation is what makes it sound.

- [ ] **Step 4: Build and run twice, the second time with VS Code deliberately open**

Run:
```
dotnet build -c Release test/FlaUI.Mcp.Tests
dotnet test -c Release --filter "FullyQualifiedName~DesktopWakeTests" -v n
```
Then open a normal VS Code window and run it again. Expected: PASS both times. The second run is the hermeticity proof — the original failure appeared only when VS Code was already running.

- [ ] **Step 5: Confirm no profile leak**

Run: `ls "$env:TEMP/flaui-mcp-wake-profiles"` (PowerShell) — expected: empty or absent after the run completes.

- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs
git commit -m "test(desktop): isolate the VS Code instance and poll for hydration

Resolves the VS Code path (machine-wide -> user-local -> PATH) instead of
pinning one absolute path; launches into a fresh --user-data-dir with
first-run suppression so the profile is clean rather than first-run; and
polls to an ADDITIVE threshold (baseline + 100) rather than an absolute or
relative one -- a relative threshold would satisfy on the very 16-node broken
tree this test exists to catch.

Teardown holds process handles taken BEFORE the kill (a raw pid cannot be
waited on once recycled or exited), waits on every child rather than the root
alone, and deletes with backoff. Profiles are GUID-named with an owner record
inside; the sweep treats an unverifiable record as dead and a missing one as
unknown-not-dead."
```

---

### Task 5: Restructure the fixture into three columns (spec D1)

The single defect behind failure #6, and the reason the ambiguity fixtures are partly invisible to every snapshot test. `MainWindow.xaml` is one vertical `StackPanel` summing to ~890 DIP; `Height="880"` was clamped by the work area to a **788 px** window, so the tree ends at `e90`, the second `DupRow` GroupBox is entirely culled, and `Row1Btn`'s Text survives on a 2 px sliver. The overflow is screen-size dependent — exactly the non-hermeticity this work removes.

**The budget is two-dimensional.** Declared minimum machine is 1366×768 @150%:

| | Screen DIP | − 48 DIP taskbar | − ~32 DIP chrome | Client |
|---|---|---|---|---|
| Height | 512 | 464 | ~432 | **432 DIP** |
| Width | 910 | 910 | ~894 | **894 DIP** |

The taskbar is 48 physical px at 100% and scales with DPI, so it is **48 DIP at every scale** — do not divide it by the scale factor. Three columns totalling ≤ ~890 DIP means roughly **290 DIP per column**, each ≤ 432 DIP tall.

**Files:**
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml` (whole file)
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs` — add the sizing clamp

- [ ] **Step 1: Add the sizing clamp to the code-behind**

In `MainWindow.xaml.cs`, add to the constructor after `InitializeComponent();`:

```csharp
        // Min(), never bind, and never a bare literal. An unclamped literal is not hermetic
        // because a work area's size IN DIPS shrinks as display scaling rises. Binding the width to
        // the work area is worse: on a very wide primary monitor it could grow the window past the
        // Canvas.Left="5000" sentinel and invert OffscreenCullTests. Min can only ever shrink.
        //
        // NOTE: SystemParameters.WorkArea wraps SPI_GETWORKAREA and reports the PRIMARY monitor,
        // which on a multi-monitor setup may not be the display hosting this window. That is
        // tolerable only because the design constants below already fit the declared floor machine
        // unaided -- the clamp is belt-and-braces, not the mechanism this depends on.
        Width = System.Math.Min(880, SystemParameters.WorkArea.Width);
        Height = System.Math.Min(420, SystemParameters.WorkArea.Height);
```

- [ ] **Step 2: Restructure the XAML**

Replace `MainWindow.xaml` in full. Every control keeps its `x:Name`, `AutomationId`, `Content` and its containing `GroupBox`/`Canvas`/`Border` parent — only column placement changes.

```xml
<Window x:Class="FlaUI.Mcp.TestApp.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="FlaUI.Mcp TestApp"
        TextElement.FontFamily="Segoe UI" TextElement.FontSize="12"
        AutomationProperties.AutomationId="MainWindow">
    <!-- Font metrics are PINNED at the root so the column budget is host-independent. Without
         this a different default font, a custom theme or larger system text inflates every column,
         and column 1's ~90 DIP of slack is exactly what DelayRevealButton_Click's runtime append
         needs. Losing it reproduces the DelayedLabel cull this restructure exists to fix.
         Window Width/Height are clamped in code-behind, not declared here. -->
    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="280"/>
            <ColumnDefinition Width="280"/>
            <ColumnDefinition Width="280"/>
        </Grid.ColumnDefinitions>

        <!-- COLUMN 1 is RootPanel, and it stays a vertical StackPanel: MainWindow.xaml.cs calls
             RootPanel.Children.Add(tb) to append DelayedLabel. If RootPanel were the Grid, that
             call would silently place the label in cell (0,0) on top of Input -- it compiles, it
             runs, and it quietly changes the layout contract the code-behind depends on.
             This is deliberately the SHALLOWEST column so that append lands inside the window. -->
        <StackPanel x:Name="RootPanel" Grid.Column="0" Margin="0,0,8,0">
            <TextBox x:Name="Input" AutomationProperties.AutomationId="Input" Margin="0,0,0,8"/>
            <PasswordBox x:Name="Secret" AutomationProperties.AutomationId="Secret" Margin="0,0,0,8"/>
            <TextBox x:Name="TextDoc" AutomationProperties.AutomationId="TextDoc"
                     AcceptsReturn="True" Height="60" Margin="0,0,0,8"
                     Text="line one&#10;line two&#10;line three"/>
            <DataGrid x:Name="Grid" AutomationProperties.AutomationId="Grid"
                      AutoGenerateColumns="True" IsReadOnly="True" CanUserAddRows="False"
                      SelectionUnit="Cell"
                      Height="100" Margin="0,0,0,8"/>
            <Button x:Name="OkButton" Content="OK"
                    AutomationProperties.AutomationId="OkButton" Click="OkButton_Click"/>
            <TextBlock x:Name="Status" AutomationProperties.AutomationId="Status" Text="ready"/>
            <Button x:Name="RebuildItemsButton" Content="Rebuild Items"
                    AutomationProperties.AutomationId="RebuildItemsButton" Click="RebuildItemsButton_Click" Margin="0,8,0,0"/>
            <Button x:Name="ClearItemsButton" Content="Clear Items"
                    AutomationProperties.AutomationId="ClearItemsButton" Click="ClearItemsButton_Click" Margin="0,4,0,0"/>
        </StackPanel>

        <StackPanel Grid.Column="1" Margin="0,0,8,0">
            <!-- No fixed Height: it flows to its natural size. A pinned height would let the items
                 grow under a different theme while the box stayed put, scrolling the last ones out
                 of the UIA tree. Flowing means an unexpectedly tall list makes this COLUMN taller,
                 which the D7 guard catches loudly instead of silently dropping items. -->
            <ListBox x:Name="ItemList" AutomationProperties.AutomationId="ItemList" Margin="0,0,0,8">
                <ListBoxItem AutomationProperties.AutomationId="ItemA" Content="A"/>
                <ListBoxItem AutomationProperties.AutomationId="ItemB" Content="B"/>
                <ListBoxItem AutomationProperties.AutomationId="ItemC" Content="C"/>
                <ListBoxItem Content="NamedOnly"/>
            </ListBox>
            <!-- Stays Visible (so it keeps a UIA peer) but the explicit IsOffscreenBehavior=Offscreen
                 hint makes the peer report IsOffscreen=true — the deterministic way to get "in tree
                 but off-screen" in WPF (plain visibility/scroll/clip do not flip IsOffscreen here). -->
            <Button x:Name="OffscreenButton" Content="Offscreen"
                    AutomationProperties.AutomationId="OffscreenButton"
                    AutomationProperties.IsOffscreenBehavior="Offscreen"
                    Width="80" Height="24"/>
            <!-- SENTINEL. Laid out far outside a clipped 1px Canvas: it keeps a UIA peer and reports
                 IsOffscreen=FALSE, but its bounding rect falls outside the window, so only the
                 SPATIAL cull catches it. DERIVED INVARIANT: the window's width must stay far below
                 5000 DIP or OffscreenCullTests breaks -- the sentinel offset and the window width
                 are coupled. The D7 guard EXEMPTS this subtree for the same reason. -->
            <Canvas Height="1" ClipToBounds="True">
                <Button x:Name="SpatialOffscreenButton" Content="Spatial"
                        AutomationProperties.AutomationId="SpatialOffscreenButton"
                        Canvas.Left="5000" Canvas.Top="0" Width="80" Height="24"/>
            </Canvas>
            <CheckBox x:Name="Check" AutomationProperties.AutomationId="Check" Content="toggle me" Margin="0,8,0,0"/>
            <Expander x:Name="Exp" AutomationProperties.AutomationId="Exp" Header="expander" Margin="0,4,0,0">
                <TextBlock Text="inner"/>
            </Expander>
            <Button x:Name="FocusReveal" AutomationProperties.AutomationId="FocusReveal"
                    Content="focus reveals label" GotFocus="FocusReveal_GotFocus" Margin="0,4,0,0"/>
            <TextBlock x:Name="RevealedLabel" AutomationProperties.AutomationId="RevealedLabel"
                       Text="" Margin="0,2,0,0"/>
        </StackPanel>

        <StackPanel Grid.Column="2">
            <Button x:Name="ModalButton" AutomationProperties.AutomationId="ModalButton"
                    Content="open modal" Click="ModalButton_Click" Margin="0,0,0,4"/>
            <!-- Freezes the UI thread with a real Thread.Sleep — a genuine, COM-pumping-stops freeze
                 (unlike WPF ShowDialog, which keeps the UIA pipeline responsive). -->
            <Button x:Name="FreezeButton" AutomationProperties.AutomationId="FreezeButton"
                    Content="freeze UI" Click="FreezeButton_Click" Margin="0,4,0,0"/>
            <Button x:Name="DelayRevealButton" AutomationProperties.AutomationId="DelayRevealButton"
                    Content="reveal after delay" Click="DelayRevealButton_Click" Margin="0,4,0,0"/>
            <TextBlock x:Name="Ticker" AutomationProperties.AutomationId="Ticker" Text="0" Margin="0,4,0,0"/>
            <Border Background="#FFE0E0E0" Height="36" Margin="0,8,0,0">
                <TextBlock x:Name="MenuTarget"
                           AutomationProperties.AutomationId="MenuTarget"
                           Text="right-click me"
                           HorizontalAlignment="Center" VerticalAlignment="Center">
                    <TextBlock.ContextMenu>
                        <ContextMenu>
                            <MenuItem Header="Alpha" AutomationProperties.AutomationId="MenuAlpha"/>
                            <MenuItem Header="Beta" AutomationProperties.AutomationId="MenuBeta"/>
                        </ContextMenu>
                    </TextBlock.ContextMenu>
                </TextBlock>
            </Border>
            <!-- Ambiguity fixtures. The ancestor containers are GroupBox, NOT a bare StackPanel:
                 WPF layout panels never override OnCreateAutomationPeer, so an AutomationId set on
                 one is invisible to UIA and can never be resolved as an AncestorAutomationId scope.
                 GroupBox (a HeaderedContentControl) has a real peer. -->
            <GroupBox AutomationProperties.AutomationId="DupHost" Margin="0,8,0,0">
                <StackPanel Orientation="Horizontal">
                    <Button Content="DupA" AutomationProperties.AutomationId="DupAid"/>
                    <Button Content="DupB" AutomationProperties.AutomationId="DupAid"/>
                    <Button Content="DupName"/>
                    <Button Content="DupName"/>
                </StackPanel>
            </GroupBox>
            <!-- TWO ancestors sharing one AutomationId, each holding ONE same-aid target. -->
            <GroupBox AutomationProperties.AutomationId="DupRow" Margin="0,8,0,0">
                <StackPanel Orientation="Horizontal">
                    <Button Content="Row1Btn" AutomationProperties.AutomationId="RowBtn"/>
                </StackPanel>
            </GroupBox>
            <GroupBox AutomationProperties.AutomationId="DupRow" Margin="0,8,0,0">
                <StackPanel Orientation="Horizontal">
                    <Button Content="Row2Btn" AutomationProperties.AutomationId="RowBtn"/>
                </StackPanel>
            </GroupBox>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: Build and eyeball the window**

Run: `dotnet build -c Release test/FlaUI.Mcp.TestApp`
Then launch `test/FlaUI.Mcp.TestApp/bin/Release/net10.0-windows/FlaUI.Mcp.TestApp.exe` and confirm visually that all three columns fit with no clipping and the `DupRow` GroupBoxes are both visible.

- [ ] **Step 4: Measure that the overflow is gone**

With the app running, take a default snapshot and confirm `Row2Btn` is present — it was culled before. Confirm the four `DupHost` buttons all appear.

- [ ] **Step 5: Run the FULL Desktop suite**

This is the gate for this task, not the six tests — the fixture is shared by every Desktop test.

Run:
```
dotnet test -c Release --filter "Category=Desktop&FullyQualifiedName!~PopupGrafting" -v n
```
Expected: `Delayed_control_becomes_satisfied_with_a_ref` now **passes** (the cull is gone and Task 1 gave it budget). `OffscreenCullTests` still passes both assertions. No previously-passing test regresses. `FindTests` two failures **remain** — Task 6 fixes those.

Capture full output. If a previously-passing test regressed, the likely causes in order are: a geometry-sensitive test (`InputToolsTests.Click_at_a_window_point_returns_no_error` clicks at 0.5/0.5; `DesktopFindTextTests` OCRs the window and takes the top match), or traversal-order/ref pinning. Report rather than patching blind.

- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.TestApp/MainWindow.xaml test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs
git commit -m "test(fixture): three-column layout that fits a clamped window

The single ~890 DIP column was clamped by the work area to a 788px window, so
the tree ended at e90: the second DupRow GroupBox was entirely culled and
Row1Btn's Text survived on a 2px sliver. The ambiguity fixtures were partly
invisible to every snapshot test and the overflow was screen-size dependent.

Three columns against a two-dimensional budget derived from a 1366x768 @150%
floor machine (894 x 432 DIP of client). Window size is Min(constant,
WorkArea) in both axes -- a clamp that only shrinks, so it never approaches
the Canvas.Left=5000 sentinel and never exceeds the screen. Font metrics are
pinned at the root so the budget is host-independent rather than merely
probable. RootPanel stays a StackPanel: the code-behind appends DelayedLabel
to it by name."
```

---

### Task 6: Give `FindTests` items that actually match (spec D5)

Both `FindTests` failures share one cause: the ListBoxItems' UIA **Name is their Content** — `"A"`, `"B"`, `"C"`, `"NamedOnly"` — so `name contains "Item"` filtered to `ListItem` matches **nothing**. Confirmed live: `find(controlType=ListItem)` returns 4 matches, all `isOffscreen:false`, un-culled. The tests confuse `AutomationId` with `Name` and have been wrong since they were authored; `Category=Desktop` is deliberately not a CI job, so nothing caught it.

The tests keep their original query and their ListItem coverage. Only the fixture gains matching elements.

**Files:**
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml` — the `ItemList` block from Task 5
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs:33-42`

- [ ] **Step 1: Add two matching items**

In the `ItemList` from Task 5, add two items after `NamedOnly`:

```xml
                <ListBoxItem AutomationProperties.AutomationId="ItemD" Content="ItemOne"/>
                <ListBoxItem AutomationProperties.AutomationId="ItemE" Content="ItemTwo"/>
```

- [ ] **Step 2: Make the rebuild handler recreate all six**

Replace `RebuildItemsButton_Click` (lines 33–42):

```csharp
    // Clear and re-create the items as NEW ListBoxItem objects (same AutomationIds). This destroys
    // the old elements, so a held ref's cached UIA element goes invalid and its RuntimeId no longer
    // matches — forcing the option-C descriptor RE-WALK (the cache fast-path can't short-circuit).
    //
    // All SIX, matching the XAML. Recreating only ItemA/B/C would permanently drop ItemOne/ItemTwo
    // and make any later `contains "Item"` query in the same app instance return 0. That cannot
    // bite today -- every FindTests [Fact] builds its own TestAppFixture, so the rebuilding test
    // cannot contaminate the querying ones -- but that is a fixture-LIFETIME argument, and the
    // classes that share one app (WaitForTests, AmbiguousResolutionTests, both
    // IClassFixture<TestAppFixture>) would not be protected by it.
    private void RebuildItemsButton_Click(object sender, RoutedEventArgs e)
    {
        ItemList.Items.Clear();
        foreach (var (aid, content) in new[]
                 {
                     ("ItemA", "A"), ("ItemB", "B"), ("ItemC", "C"),
                     ("", "NamedOnly"), ("ItemD", "ItemOne"), ("ItemE", "ItemTwo")
                 })
        {
            var item = new System.Windows.Controls.ListBoxItem { Content = content };
            if (!string.IsNullOrEmpty(aid))
                System.Windows.Automation.AutomationProperties.SetAutomationId(item, aid);
            ItemList.Items.Add(item);
        }
    }
```

- [ ] **Step 3: Build and verify WPF did not virtualize the new items away**

Run: `dotnet build -c Release test/FlaUI.Mcp.TestApp`
Launch the TestApp and confirm by measurement that `find(controlType=ListItem)` returns **6** matches, all `isOffscreen:false`. If WPF has virtualized the added items out of the UIA tree, stop and report — the documented fallback is to retarget the query at `contains "Items"` + `Button` (proven live by the passing `Find_ignoreCase_contains_matches_multiple_across_case` sibling), accepting the loss of ListItem name coverage.

- [ ] **Step 4: Run FindTests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~FindTests" -v n`
Expected: all 9 pass, including `Find_by_name_contains_matches_substring`, `Find_truncates_and_reports_totalMatches`, and `Find_minted_ref_re_resolves_after_tree_mutation` (which clicks Rebuild).

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.TestApp/MainWindow.xaml test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs
git commit -m "test(fixture): add ListItems whose NAME contains 'Item'

Both FindTests failures share one cause: a ListBoxItem's UIA Name is its
Content, so the items were named A/B/C/NamedOnly and `name contains \"Item\"`
filtered to ListItem matched nothing. The tests confused AutomationId with
Name and have been wrong since they were authored -- Desktop is not a CI job,
so nothing caught it.

The tests keep their original query and their ListItem coverage; the fixture
gains matching elements. RebuildItemsButton_Click now recreates all six so
the handler stays consistent with the declared contents."
```

---

### Task 7: Fixture-integrity regression guard (spec D7)

**This task is intentionally specified at intent level, not line level: the file does not exist yet and neither does the post-Task-5 layout it measures.** Write the test against the contracts below and the real column structure as built, rather than against line numbers invented here.

**Files:**
- Create: `test/FlaUI.Mcp.Tests/Perception/FixtureIntegrityTests.cs`

**What it must assert:** walk **every descendant of the window root** in the UIA tree; for each one except the exemption named below, its bottom and right edges lie inside the window's own UIA `BoundingRectangle`.

**Do not try to scope the walk "per column".** WPF layout panels — `Grid`, `StackPanel`, `Canvas`, `Border` — never override `OnCreateAutomationPeer`, so **none of them appears in the UIA tree at all**; the fixture's own XAML says so at `MainWindow.xaml:79-83`, which is why `DupHost`/`DupRow` had to be `GroupBox`es to be resolvable as ancestor scopes. The three column `StackPanel`s are therefore invisible to UIA and the tree under the window is effectively flattened. "Every descendant of every column `StackPanel`" and "every descendant of the window" denote the **same set of elements**; only the second is expressible.

**Three properties are load-bearing, and each one is a weaker version that failed review:**

1. **Compare against the window's UIA `BoundingRectangle`, not a computed "client" rect.** That rect includes the non-client frame and the drop-shadow aura, and it is exactly what `SnapshotEngine.cs:65-70` binds `cullBounds` to. A tighter client box built from `SystemParameters.WindowNonClientFrameThickness` yields **false RED** — the guard failing on content that overflows into the border while the cull's own `IntersectsWith` still includes it. Use the cull's own rectangle so the guard and the mechanism it protects cannot disagree.

2. **Every descendant — not a container, and not direct children.** Asserting on a container is an assertion that can never fail: a `Grid` in a clamped window is arranged to the client area regardless of content, so "the Grid is inside the window" holds while fifty children overflow. Direct children have the same problem one level down — `DupHost` and `DupRow` are `GroupBox`es arranged to their available width whatever their content does, so widened inner buttons overflow and cull while the `GroupBox` rect stays neatly inside. Only a full descendant walk closes both.

3. **Exempt `SpatialOffscreenButton` by its `AutomationId` — not "the Canvas by name".** It sits at `Canvas.Left="5000"` *by design*, and `OffscreenCullTests.cs:45` depends on it being outside the window; an unexempted walk finds it at x≈5080, compares it against a ~894 DIP width, and fails **permanently**, forbidding the exact overflow it exists to create. The exemption must key on the button's own `AutomationId`, because the clipped `Canvas` that "contains" it has no UIA peer and cannot be located or skipped as a subtree — in the UIA tree the button is simply another descendant of the window.

**Why this is safe under constraint 4:** it asserts a property of the fixture's *layout*, not of the cull, so it stays valid whichever way the deferred `wait_for`/`find` decision goes. Nothing here asserts about culling in either direction.

- [ ] **Step 1: Write the test**

Use `[Trait("Category", "Desktop")]`, a per-test `TestAppFixture`, and `WindowManager`/`PerceptionManager` set up the way `FindTests.OpenAsync` does (`FindTests.cs:14-22`). Walk the tree with FlaUI from the window root, skip the exempt `Canvas` subtree, and assert containment per element with a failure message naming the offending element's `AutomationId` and both rectangles.

- [ ] **Step 2: Prove the guard can fail**

Temporarily add ~20 filler buttons to one column, rebuild, and run the test. Expected: **FAIL**, naming the overflowing element. A guard that passes here is one of the tautologies described above — fix it before continuing. Remove the filler afterwards.

- [ ] **Step 3: Run against the real fixture**

Run: `dotnet test -c Release --filter "FullyQualifiedName~FixtureIntegrityTests" -v n`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Perception/FixtureIntegrityTests.cs
git commit -m "test(fixture): geometric containment guard against future overflow

Asserts every descendant of every column StackPanel -- minus the deliberately
out-of-bounds sentinel Canvas subtree -- has its bottom and right edges
inside the window's UIA BoundingRectangle, the same rect the snapshot cull
binds cullBounds to.

Descendants rather than the container or its direct children: a Grid in a
clamped window is arranged to the client area regardless of content, and a
GroupBox is arranged to its column width regardless of content, so both
weaker forms are assertions that can never fail."
```

---

### Task 8: Full verification

**Files:** none modified — this task is the gate.

- [ ] **Step 1: Ask the user for a physical console and an input lease**

Say plainly what is needed: a physical (non-RDP) console session, `flaui-mcp unlock --minutes 60 --allow-shells`, and that they step away for the duration. Do not attempt to self-serve the lease or work around its absence.

- [ ] **Step 2: Kill orphans**

Kill `testhost.exe`, `FlaUI.Mcp.TestApp.exe`, and any `Code.exe` **whose command line points at `%TEMP%\flaui-mcp-wake-profiles`**.

Do **not** kill `Code.exe` by bare process name: Step 4 requires an ambient VS Code deliberately left open, and a name-scoped kill would assassinate the very precondition that step exists to test — the suite would then "prove" hermeticity in a pristine environment it had silently created for itself. Kill it as a process **tree**; killing the main process alone reparents the Electron GPU/renderer/extension-host children, which stay alive holding the profile's SQLite locks.

- [ ] **Step 3: Run the main suite**

Run:
```
dotnet test -c Release --filter "Category=Desktop&FullyQualifiedName!~PopupGrafting" -v n
```
Expected: **108 passed, 0 failed, 0 skipped.**

- [ ] **Step 4: Run it a second time with VS Code deliberately open**

Same command, with a normal VS Code window open. This is the hermeticity proof for failure #1 — it originally failed only when VS Code was already running. Expected: identical result.

- [ ] **Step 5: Confirm zero skips**

A skip is not a pass. Several Desktop tests are `[SkippableFact]` guarded by `Skip.If(InputLocked())`, and Task 2 adds another; xUnit exits 0 on a skipped test, so a lease-less run reports a clean exit while bypassing every assertion that matters. Confirm the summary reports **0 skipped**. If anything skipped, the lease was not active — re-request it and re-run rather than accepting the green.

- [ ] **Step 6: Run the PopupGrafting half**

Run:
```
dotnet test -c Release --filter "FullyQualifiedName~PopupGrafting" -v n
```
**This must PASS for A1a to be green.** It has never been run — it needs synthetic input on a physical console. If it surfaces failures unrelated to this plan, report them to the user as a scope decision; do not silently exclude them from the gate.

- [ ] **Step 7: Report**

Report both runs' full counts, the PopupGrafting result, and any test that behaved differently between run 1 and run 2. Capture full output, never grep-filtered — the previous session lost a failure's assert message to a narrow grep and had to re-run the whole suite to recover it.

---

## Self-review

**Spec coverage:** D1→Task 5, D2→Tasks 3+4, D3→Task 2, D4→Task 1, D5→Task 6, D6→Task 1, D7→Task 7. The spec's Implementation order (budget raises → D3 → D2 → D1 → D5 → D7 → verification) is Tasks 1→2→3,4→5→6→7→8. Verification steps 1–7 all appear in Task 8; the "capture full output" rule is repeated in Tasks 3, 5 and 8 where it bites. Standing preconditions appear at the top and again in Task 8.

**Placeholder scan:** no TBD/TODO. Task 7 is intent-level by design and says so explicitly, with its three contracts and its exempt subtree fully specified — that is the plan-vs-spec discipline, not a placeholder, since the file and the layout it measures do not exist yet.

**Type consistency:** `HydrationDelta` replaces `WokenNodeFloor` and is used in Task 4 only. `ProfileDir` is added to `Rig` in Task 4 Step 1 and consumed in Step 2. `TryDelete`/`SweepStaleProfiles`/`ResolveVsCode`/`ProfileParent` are all defined in Task 4 Step 1 before use. `NudgeIdleClock`/`InputLocked` are defined in Task 2's single file. `RootPanel` keeps its name and type across Tasks 5 and 6.

**Known gaps, each with an owner:**
1. Task 2 Step 3 may find the zero-delta move does not advance the idle clock — it carries an explicit escalation path with two named alternatives rather than a silent workaround.
2. Task 6 Step 3 may find WPF virtualizes the added items — documented fallback, decided by measurement.
3. Task 5 Step 5 may surface a geometry-sensitive regression — the two candidate tests are named.
4. Task 8 Step 6 may surface PopupGrafting failures out of scope — reported to the user, not absorbed.
5. Task 4's per-column DIP estimates are arithmetic, not measurement; Task 5 Step 3 and Task 7 are the checks.
