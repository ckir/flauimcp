using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP2 Task 4's required pin: BuildModelAsync now takes TWO registries — <c>refs</c>, which every
/// walked node registers into, and <c>resolveRefs</c>, which the ROOT REF resolves against (default null
/// => refs, so every pre-SP2 caller is a byte-identical no-op). The spec calls this the highest-risk edit
/// in SP2 "because a default-argument mistake silently changes every existing call site", and requires an
/// explicit pin rather than reliance on the existing suite: the suite covers this invariant only
/// INCIDENTALLY, so a regression would surface as some unrelated Desktop test failing for a reason nobody
/// could attribute.
///
/// Desktop-gated because both halves need a live UIA tree — a ref cannot be resolved without one.
///
/// ⚠ SP2 Task 5 adds a HEADLESS class to this same file. It must be a SEPARATE class: the
/// class-level Desktop trait below applies to every fact in the class, so a headless fact added here
/// would be excluded from the headless gate while looking green.</summary>
[Trait("Category", "Desktop")]
public class WaitStableScopeTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitStableScopeTests(TestAppFixture app) => _app = app;

    /// <summary>THE DURABLE HALF. A ref-rooted snapshot resolves its RootRef against the registry the
    /// caller passed — the durable _refs — because resolveRefs defaults to null and falls back to it.
    /// Mis-default resolveRefs (e.g. to `new RefRegistry()`) and this throws REF_NOT_FOUND: the ref was
    /// minted into _refs, so a fresh registry has never heard of it.
    ///
    /// Note WHY this is the sensitive shape: a plain snapshot-then-resolve would NOT catch the mis-default,
    /// because resolveRefs is only consulted on the RootRef path. The second snapshot must be ref-ROOTED.</summary>
    [Fact]
    public async Task A_ref_rooted_snapshot_resolves_its_root_against_the_durable_registry()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (_, model) = await perception.SnapshotModelForWaitAsync(
            handle, new SnapshotOptions { InteractiveOnly = false });
        // Depth>0 so the root ref names a real descendant, not the window itself.
        string scopeRef = model.Nodes.First(n => n.Depth > 0).Ref;

        var (_, scoped) = await perception.SnapshotModelForWaitAsync(
            handle, new SnapshotOptions { InteractiveOnly = false, RootRef = scopeRef });

        Assert.NotEmpty(scoped.Nodes);
    }

    /// <summary>THE THROWAWAY HALF — the reason the throwaway exists at all. Each stability poll builds
    /// with `new RefRegistry()` (WaitCoordinator.cs:133) so per-poll walks never grow the durable
    /// registry.
    ///
    /// WHY THIS PIN BITES, precisely: BeginSnapshot replaces a window's ref dictionary
    /// (RefRegistry.cs:36) but does NOT touch `_counter`, and Register increments that counter
    /// monotonically (`:70-71`) — only EvictWindow ever clears it (`:56`). So if a poll walk ever passed
    /// the durable registry as its `refs`, the first poll would not merely clear `heldRef`: the counter
    /// guarantees that ref number is NEVER re-minted, so Lookup throws RefNotFound (`:86-92`) and this
    /// fact goes red. The class docstring states the same property (`:8-9`). That monotonic counter is
    /// what makes the observable proxy below sound rather than merely suggestive.
    ///
    /// This is the observable-proxy form the plan permits: the throwaway's own refs are unobservable from
    /// outside, so the assertion is that a ref held across the wait still resolves afterwards.
    ///
    /// THE WAIT MUST TIME OUT, and that is not an accident of tuning. A wait that SETTLES ends by taking a
    /// deliberate durable snapshot (WaitCoordinator.cs:141-142 -> SnapshotModelForWaitAsync -> _refs), which
    /// legitimately supersedes held refs — so on the success path a surviving ref is unobservable and this
    /// pin would assert something false. Only the timeout path (`:145` returns without that final snapshot)
    /// leaves the poll walks as the ONLY builds, which is exactly the isolation the invariant needs. quietMs
    /// is set unreachably high so `needed` can never be met inside the budget.</summary>
    [Fact]
    public async Task Poll_walks_do_not_supersede_a_ref_held_across_the_wait()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (_, model) = await perception.SnapshotModelForWaitAsync(
            handle, new SnapshotOptions { InteractiveOnly = false });
        string heldRef = model.Nodes.First(n => n.Depth > 0).Ref;

        var r = await wait.WaitForStableAsync(handle, by: null, value: null, includeText: false,
            quietMs: 100_000, timeoutMs: 6000, pollIntervalMs: 100);

        Assert.False(r.Stable, "quietMs is unreachable inside the budget, so this must be the timeout path");
        Assert.True(wait.WalkCount >= 2, $"needed >=2 poll walks to make the pin meaningful, saw {wait.WalkCount}");

        // The held ref must still resolve AFTER those poll walks. Root a snapshot at it: that is the code
        // path that consults the durable registry.
        var (_, scoped) = await perception.SnapshotModelForWaitAsync(
            handle, new SnapshotOptions { InteractiveOnly = false, RootRef = heldRef });

        Assert.NotEmpty(scoped.Nodes);
    }
}

/// <summary>Item 3's argument contract. scopeRef roots the POLL walk; by+value keeps working unchanged.
/// Supplying both is a caller bug and must be REFUSED, never silently resolved in favour of one --
/// silently picking a scope the caller did not ask for is the wrong-belief class SP1 spent eleven rounds
/// removing. HEADLESS: this is validation, not UIA.</summary>
public class WaitStableScopeArgumentTests
{
    [Fact]
    public async Task Supplying_both_scopeRef_and_a_selector_is_refused()
    {
        var coordinator = new WaitCoordinator(null!);

        var ex = await Assert.ThrowsAsync<ToolException>(() => coordinator.WaitForStableAsync(
            new FlaUI.Mcp.Core.Windows.WindowHandle("w1"), by: "automationId", value: "X",
            includeText: false, quietMs: 100, timeoutMs: 100, pollIntervalMs: 10,
            scopeRef: "e5", includeOffscreen: false));

        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
        Assert.Contains("scopeRef", ex.Message);
    }
}
