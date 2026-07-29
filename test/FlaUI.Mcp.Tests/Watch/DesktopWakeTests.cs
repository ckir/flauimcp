// test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Watch;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

/// <summary>Phase 9 Task 5 regression: desktop_wake_accessibility actually hydrates a Chromium/Electron window's
/// UIA tree while held, auto-releases the hold when the window closes (Phase-6 WindowInvalidated chokepoint),
/// and desktop_release_accessibility removes the registry entry. Fixture: VS Code
/// (C:\Program Files\Microsoft VS Code\Code.exe) — a live spike measured its tree at 14 nodes opaque / 231
/// nodes woken. Runs on a live console only (Category=Desktop) — VS Code launch/close is slow and requires a
/// real desktop session.</summary>
[Trait("Category", "Desktop")]
public class DesktopWakeTests
{
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

    // Opaque Electron/Chromium shell windows collapse to a handful of nodes (root Pane + a couple of chrome
    // frames); a woken tree explodes into hundreds. These thresholds mirror the plan's confirmed spike numbers
    // (14 -> 231) with slack for VS Code version drift.
    private const int OpaqueNodeCeiling = 30;

    // Measured, not guessed. The original spike saw 14 opaque / 231 woken (~217 added); Task 3
    // measured 14 / 142 (~128 added) on this machine -- the tree has drifted -41% already. 50
    // keeps 61% slack against further drift while staying 25x the broken-state delta of 2
    // (baseline 14 -> the failing 16), so it still separates hydrated from not-hydrated cleanly.
    // The claim is that hydration HAPPENED, not that it reached a particular milestone.
    private const int HydrationDelta = 50;

    private sealed class Rig : IDisposable
    {
        public required AutomationDispatcher Dispatcher { get; init; }
        public required WindowManager Windows { get; init; }
        public required WakeRegistry Registry { get; init; }
        public required WakeService Wake { get; init; }
        public required PerceptionManager Perception { get; init; }
        public required WindowHandle Handle { get; init; }
        public required int Pid { get; init; }
        public required string ProfileDir { get; init; }

        public void Dispose()
        {
            // 1. Hold the ROOT handle, opened BEFORE the kill. A handle opened before termination
            //    stays valid afterwards; a raw integer pid does not, and GetProcessById on an
            //    already-dead process throws -- which would crash teardown before the delete runs.
            System.Diagnostics.Process? root = null;
            try { root = System.Diagnostics.Process.GetProcessById(Pid); }
            catch { /* already gone */ }

            // 2. Kill the whole tree; this signals our children too.
            try { if (root is not null && !root.HasExited) root.Kill(entireProcessTree: true); }
            catch { }
            try { root?.WaitForExit(5000); } catch { }
            try { root?.Dispose(); } catch { }

            try { Windows.Dispose(); } catch { }
            try { Dispatcher.Dispose(); } catch { }

            // 3. DELETING THE DIRECTORY IS THE REAL OBSERVABLE, and it is deliberately the ONLY thing
            //    guarding against still-exiting children.
            //
            //    Two earlier versions enumerated Code.exe to wait on the children individually. Both
            //    were wrong, and the second was wrong in the opposite direction from the first:
            //      - waiting on EVERY Code.exe blocks the full timeout on the developer's own editor,
            //        which we never kill. Measured at 11 ambient processes: 4s -> 59s per test.
            //      - waiting only on pids absent from a pre-launch snapshot misclassifies in BOTH
            //        directions -- a recycled pid makes one of OUR children look ambient so it is
            //        never waited on, and an ambient process spawned mid-test looks like ours so we
            //        block 5s on something we never killed.
            //    Distinguishing our process tree from a foreign one is not reliably expressible here,
            //    so it is the wrong mechanism. Polling the observable we actually care about is the
            //    right one, and it is immune to both misclassifications by construction.
            //
            //    Kill is non-blocking and WaitForExit binds to a single handle, so GPU/renderer
            //    children can briefly outlive the root while still holding the profile's SQLite
            //    locks. The backoff simply retries until they let go.
            TryDelete(ProfileDir);
        }
    }

    private static async Task<Rig> LaunchAsync()
    {
        // Never let housekeeping fail the launch it precedes. The sweep inspects directories other
        // runs may own, so it is exposed to races it cannot control; a crash here would fail a test
        // for reasons entirely unrelated to what that test asserts.
        try { SweepStaleProfiles(); } catch { }

        // GUID, not pid: --user-data-dir must be built BEFORE Process.Start(), but the OS assigns
        // the pid only AFTER the process exists, so a pid-named directory is circular. The owner
        // record is written into the directory at creation and updated once the pid is known.
        var profile = System.IO.Path.Combine(ProfileParent, System.Guid.NewGuid().ToString("N"));

        // EVERYTHING that allocates lives inside the try below -- the directory, the owner record, the
        // COM objects, and the launch itself. Nothing above it acquires anything, so there is no path
        // that leaks without reaching the catch. Earlier versions wrapped only the launch call, then
        // only everything after the directory creation; each left a sliver where a throw leaked the
        // directory with no catch able to reach TryDelete.
        AutomationDispatcher? dispatcher = null;
        WindowManager? mgr = null;
        // Declared OUTSIDE the try on purpose. If the launch succeeds and something after it throws,
        // we own a running VS Code tree that no Rig will ever dispose -- and a pid scoped inside the
        // try is invisible to the catch, so the tree would be orphaned permanently.
        var launchedPid = 0;
        try
        {
            // Inside the try, deliberately. An earlier version created the directory and wrote the
            // first owner record ABOVE it, which left a sliver where an IO failure -- access denied,
            // an antivirus lock -- leaked the directory with no catch to reach TryDelete.
            System.IO.Directory.CreateDirectory(profile);
            System.IO.File.WriteAllText(System.IO.Path.Combine(profile, "owner.txt"), "launching");

            dispatcher = new AutomationDispatcher();
            mgr = new WindowManager(dispatcher);
            var refs = new RefRegistry();
            var registry = new WakeRegistry();
            var source = new Uia3EventSource(mgr);
            var wake = new WakeService(source, registry, mgr);
            var perception = new PerceptionManager(mgr, refs, new SnapshotCache());

            // Instance isolation. NOTE the reason, which is NOT what an earlier draft claimed: this
            // does not prevent waking a process that does not own the measured window --
            // LaunchAppAsync (WindowManager.cs:396-439) snapshots pre-existing pids and accepts a
            // window only from the launched pid or a NEW same-named one, so it can never latch an
            // ambient instance. What it WOULD do without isolation is fall through to the
            // LaunchTimeout throw at :436 whenever a developer already has VS Code open. Isolation is
            // what makes this green on any dev machine.
            //
            // A pristine --user-data-dir yields the FIRST-RUN tree, not a clean one: trust prompts,
            // welcome/release-notes tabs and extension toasts steal focus and change the node count,
            // which is exactly what the hydration assertion measures. First-run suppression is
            // therefore not optional decoration -- it is forced by the isolation above.
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
            launchedPid = pid;

            System.IO.File.WriteAllText(System.IO.Path.Combine(profile, "owner.txt"),
                $"{pid}|{System.Diagnostics.Process.GetProcessById(pid).StartTime.Ticks}");

            return new Rig
            {
                Dispatcher = dispatcher, Windows = mgr, Registry = registry, Wake = wake,
                Perception = perception, Handle = handle, Pid = pid, ProfileDir = profile
            };
        }
        catch
        {
            // Ownership has NOT transferred to a Rig, so this is the only place any of it gets
            // released. The launched process comes FIRST: if the launch succeeded and a later step
            // threw -- the owner.txt write, or GetProcessById on a process that is already exiting --
            // then a full VS Code tree is running with nothing holding a reference to it, and killing
            // it here is the only thing standing between that and a permanent orphan. It also has to
            // die before TryDelete, or its children still hold the profile's locks.
            if (launchedPid != 0)
            {
                System.Diagnostics.Process? launched = null;
                try { launched = System.Diagnostics.Process.GetProcessById(launchedPid); }
                catch { /* already gone */ }

                // Kill and wait live in SEPARATE try blocks, exactly as Rig.Dispose does. Grouping
                // them means a Kill that throws -- Win32Exception when the process is already
                // mid-termination -- skips the wait entirely, letting TryDelete race children that
                // have not yet let go of the profile's locks.
                try { if (launched is not null && !launched.HasExited) launched.Kill(entireProcessTree: true); }
                catch { }
                try { launched?.WaitForExit(5000); } catch { }
                try { launched?.Dispose(); } catch { }
            }

            // Order mirrors Rig.Dispose: WindowManager tears down the automation base via the
            // dispatcher, so it must go first while the dispatcher is still alive.
            try { mgr?.Dispose(); } catch { }
            try { dispatcher?.Dispose(); } catch { }
            TryDelete(profile);
            throw;
        }
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
            string? text;
            try
            {
                text = System.IO.File.Exists(record) ? System.IO.File.ReadAllText(record) : null;
            }
            catch
            {
                // Unreadable -- most likely a concurrent run mid-write to its own record, which is a
                // sharing violation and not evidence of death. Treat it exactly as a MISSING record:
                // unknown, not dead, and therefore resolved by age below.
                //
                // Deliberately NOT `continue`. Skipping outright would exempt the directory from the
                // age check as well, so a permanently unreadable record (corrupted ACLs, a stuck lock)
                // would never be reaped by ANY future sweep and would leak forever.
                text = null;
            }

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

    /// <summary>
    /// Retry-with-backoff delete. This is now the SOLE guard against a still-exiting Electron child
    /// holding the profile's SQLite locks, so it is deliberately more patient than a bare delete:
    /// 8 attempts at 250ms increments is ~9s of total tolerance, against children that normally let go
    /// within a second of a tree kill. If it still cannot delete, the stale-profile sweep reaps the
    /// directory on a later run -- that backstop is why giving up here is safe rather than silent.
    /// </summary>
    private static void TryDelete(string dir)
    {
        // Already gone is SUCCESS, not something to retry. Without this, a directory that a concurrent
        // run deleted -- or one whose creation itself failed -- costs the full ~9s backoff and then
        // emits a "GAVE UP" line about a directory that never existed. Note Directory.GetCreationTimeUtc
        // does NOT throw for a missing path, it returns the 1601 epoch, so a vanished directory sails
        // through the sweep's age check and lands here.
        if (!System.IO.Directory.Exists(dir)) return;

        System.Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { System.IO.Directory.Delete(dir, recursive: true); return; }
            catch (System.IO.DirectoryNotFoundException) { return; } // vanished mid-retry: also success
            catch (System.Exception ex) { last = ex; System.Threading.Thread.Sleep(250 * (attempt + 1)); }
        }

        // Exhausted. Deliberately does NOT throw: this runs from Dispose and from a catch block, and
        // turning a passing test red over a cleanup hiccup would misreport what actually happened.
        // But it must not be SILENT either -- a give-up that leaves no trace is indistinguishable from
        // a clean delete, so a genuine leak would look exactly like success. Say so, loudly enough to
        // find afterwards, and let the stale-profile sweep reap it on the next run.
        System.Console.WriteLine(
            $"[wake-teardown] GAVE UP deleting profile after 8 attempts (~9s): {dir} -- " +
            $"something still holds it ({last?.GetType().Name}: {last?.Message}). " +
            "The stale-profile sweep will reap it on a later run.");
    }

    [Fact]
    public async Task Waking_hydrates_the_tree_while_held()
    {
        using var rig = await LaunchAsync();

        // Task 3 measured match=True on both a cold and a consecutive run, and LaunchAppAsync
        // (WindowManager.cs:396-439) cannot return a foreign window anyway: it accepts only the
        // launched pid or a NEW same-named pid, so an ambient instance's pre-existing pids are
        // filtered out and the call throws LaunchTimeout instead. This assert is therefore a cheap
        // standing invariant guard, NOT the isolation check an earlier draft claimed it was.
        var owningPid = await rig.Windows.RunWithWindowAndDesktopAsync(rig.Handle,
            (win, _) => win.Properties.ProcessId.ValueOrDefault);
        Assert.True(rig.Pid == owningPid,
            $"the launched process must own the measured window; launched={rig.Pid} owning={owningPid}. " +
            "This invariant has held on every measured run; a mismatch means LaunchAppAsync's pid " +
            "filtering changed and the wake may target a process that does not own this window.");

        var before = await rig.Perception.StatsByWindowAsync(rig.Handle);
        Assert.True(before.Total < OpaqueNodeCeiling,
            $"expected an opaque baseline (<{OpaqueNodeCeiling} nodes), got {before.Total}");

        // CONTROL, and it is load-bearing: prove the tree does NOT hydrate on its own before crediting
        // the wake with hydrating it. Every StatsByWindowAsync call is itself a UIA request and
        // Chromium hydrates lazily in response to UIA activity, so without this the polling loop below
        // could be what woke the tree while WakeAsync contributed nothing -- and the test would pass
        // for a reason unrelated to what it claims. 4s is past the point where a woken tree has already
        // climbed clear of the threshold (measured ~97 nodes by t=2s).
        await Task.Delay(4000);
        var unwoken = await rig.Perception.StatsByWindowAsync(rig.Handle);
        Assert.True(unwoken.Total < before.Total + HydrationDelta,
            $"the tree hydrated WITHOUT any wake (baseline={before.Total}, after 4s of polling=" +
            $"{unwoken.Total}, threshold={before.Total + HydrationDelta}); this test cannot attribute " +
            "hydration to WakeAsync, so its central claim is unverifiable rather than merely failing");

        var wakeId = await rig.Wake.WakeAsync(rig.Handle.Id, rig.Pid);

        // ADDITIVE, never multiplicative: hydration adds a roughly fixed population (the Chromium
        // document's accessibility tree), it does not scale the shell's node count. A relative
        // threshold inverts into a false green on exactly this defect -- with a 3-node baseline a
        // 5x rule demands only 15, and the measured failure state was 16.
        //
        // POLLING, not a fixed delay, is the actual repair. Task 3 measured hydration as a RAMP:
        // baseline 14, then 97 nodes at t=2s on one run -- already below the old floor of 100 --
        // reaching ~142 by t=6s. The original fixed Delay(1500) sampled EARLIER than any of those,
        // which is why it reported 16 against a baseline of 14.
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

    [Fact]
    public async Task Closing_the_window_auto_releases_its_wake()
    {
        using var rig = await LaunchAsync();

        var wakeId = await rig.Wake.WakeAsync(rig.Handle.Id, rig.Pid);
        Assert.Contains(rig.Wake.List(), w => w.WakeId == wakeId);

        // Phase-6 WindowInvalidated chokepoint: CloseAsync closes the window then fires Invalidate, which
        // WakeService.OnWindowInvalidated listens to and uses to evict any wake held on that window.
        await rig.Windows.CloseAsync(rig.Handle);
        await Task.Delay(1000); // give the (possibly ThreadPool-offloaded) dispose+evict a moment to land

        Assert.DoesNotContain(rig.Wake.List(), w => w.WakeId == wakeId);
    }

    [Fact]
    public async Task Release_removes_the_wake_from_the_registry()
    {
        using var rig = await LaunchAsync();

        var wakeId = await rig.Wake.WakeAsync(rig.Handle.Id, rig.Pid);
        Assert.Contains(rig.Wake.List(), w => w.WakeId == wakeId);

        await rig.Wake.ReleaseAsync(wakeId);

        // NOTE (spike β): we deliberately do NOT assert an immediate post-release node-count collapse here.
        // Chromium holds AXMode warm and only re-collapses the tree lazily once it goes idle, so asserting a
        // collapse right after release would be flaky. The registry-removal assertion below is the real,
        // load-bearing check for this path.
        Assert.DoesNotContain(rig.Wake.List(), w => w.WakeId == wakeId);
    }
}
