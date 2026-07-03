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
    private const string VsCodePath = @"C:\Program Files\Microsoft VS Code\Code.exe";

    // Opaque Electron/Chromium shell windows collapse to a handful of nodes (root Pane + a couple of chrome
    // frames); a woken tree explodes into hundreds. These thresholds mirror the plan's confirmed spike numbers
    // (14 -> 231) with slack for VS Code version drift.
    private const int OpaqueNodeCeiling = 30;
    private const int WokenNodeFloor = 100;

    private sealed class Rig : IDisposable
    {
        public required AutomationDispatcher Dispatcher { get; init; }
        public required WindowManager Windows { get; init; }
        public required WakeRegistry Registry { get; init; }
        public required WakeService Wake { get; init; }
        public required PerceptionManager Perception { get; init; }
        public required WindowHandle Handle { get; init; }
        public required int Pid { get; init; }

        public void Dispose()
        {
            // Best-effort teardown: kill the VS Code process (window Close may already have exited it), then
            // dispose the WindowManager/dispatcher. Order matters — Windows.Dispose() tears down the automation
            // base; do the process kill first so it can't race a live query.
            try { using var p = Process.GetProcessById(Pid); if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            try { Windows.Dispose(); } catch { }
            try { Dispatcher.Dispose(); } catch { }
        }
    }

    private static async Task<Rig> LaunchAsync()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var registry = new WakeRegistry();
        var source = new Uia3EventSource(mgr);
        var wake = new WakeService(source, registry, mgr);
        var perception = new PerceptionManager(mgr, refs, new SnapshotCache());
        // VS Code is slow to paint its first frame; give it a generous window.
        var (handle, pid) = await mgr.LaunchAppAsync(VsCodePath, null, 20000);
        return new Rig
        {
            Dispatcher = dispatcher, Windows = mgr, Registry = registry, Wake = wake,
            Perception = perception, Handle = handle, Pid = pid
        };
    }

    [Fact]
    public async Task Waking_hydrates_the_tree_while_held()
    {
        using var rig = await LaunchAsync();

        // Opaque baseline: VS Code's un-woken shell exposes only a handful of UIA nodes.
        var before = await rig.Perception.StatsByWindowAsync(rig.Handle);
        Assert.True(before.Total < OpaqueNodeCeiling,
            $"expected an opaque baseline (<{OpaqueNodeCeiling} nodes), got {before.Total}");

        var wakeId = await rig.Wake.WakeAsync(rig.Handle.Id, rig.Pid);
        // Let AXMode actually activate and the tree realize (spike observed this settles within ~1-2s).
        await Task.Delay(1500);

        var after = await rig.Perception.StatsByWindowAsync(rig.Handle);
        Assert.True(after.Total > WokenNodeFloor,
            $"expected the tree to hydrate while held (>{WokenNodeFloor} nodes), got {after.Total}");

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
