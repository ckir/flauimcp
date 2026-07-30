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

/// <summary>Item 3's selector-miss path. `by`+`value` matching nothing in the CULLED poll model used to
/// throw "No element matched {by}={value}" unconditionally -- a false statement when the element exists
/// but was culled against the window bounds. Both branches below must describe what was SEARCHED, never
/// claim what does not exist: an unculled confirmation walk does not prove non-existence either
/// (UnculledPollOptions keeps IsOffscreen filtering by design, and MaxDepth still applies,
/// SnapshotEngine.cs:101-105).
///
/// Uses the same fixture elements SP1's wait tests already established: SpatialOffscreenButton is
/// present but spatially culled (WaitGoneCullTests.cs:8-9, WaitDiagnosticTests.cs:30 -- "reports the
/// reason and both rects"), and NoSuchElement_SP1 genuinely is not there
/// (WaitGoneCullTests.cs:32-49, WaitDiagnosticTests.cs:42-55).</summary>
[Trait("Category", "Desktop")]
public class WaitStableScopeMissTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitStableScopeMissTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Culled_but_present_names_the_cull_and_offers_includeOffscreen_or_scopeRef()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var ex = await Assert.ThrowsAsync<ToolException>(() => wait.WaitForStableAsync(
            handle, by: "automationId", value: "SpatialOffscreenButton",
            includeText: false, quietMs: 100, timeoutMs: 100, pollIntervalMs: 50));

        Assert.Equal(ToolErrorCode.SelectorNoMatch, ex.Code);
        Assert.Contains("culled", ex.Message);
        Assert.Contains("includeOffscreen", ex.SuggestedRecovery);
        Assert.Contains("scopeRef", ex.SuggestedRecovery);
    }

    [Fact]
    public async Task Genuinely_absent_does_not_assert_non_existence_and_still_offers_includeOffscreen()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var ex = await Assert.ThrowsAsync<ToolException>(() => wait.WaitForStableAsync(
            handle, by: "automationId", value: "NoSuchElement_SP1",
            includeText: false, quietMs: 100, timeoutMs: 100, pollIntervalMs: 50));

        Assert.Equal(ToolErrorCode.SelectorNoMatch, ex.Code);
        // "was found in the searched tree" is a claim about the SEARCH, not the element's existence.
        // "does not exist" / "not found" as a standalone existence claim is exactly what this fix removes.
        Assert.DoesNotContain("does not exist", ex.Message);
        Assert.Contains("searched", ex.Message);
        Assert.Contains("includeOffscreen", ex.SuggestedRecovery);
    }

    /// <summary>The happy path must pay NO extra walk for this confirmation machinery. WaitForStableAsync's
    /// poll increments only the plain WalkCount (WaitCoordinator.cs:149,159 call CountWalk, never
    /// CountPollWalk/CountConfirmationWalk), so that is the counter this pins: one CountWalk per poll
    /// iteration plus one for the final satisfy snapshot, and nothing more.</summary>
    [Fact]
    public async Task A_satisfied_wait_pays_no_confirmation_walk()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var r = await wait.WaitForStableAsync(handle, by: null, value: null, includeText: false,
            quietMs: 1, timeoutMs: 6000, pollIntervalMs: 50);

        Assert.True(r.Stable);
        // One CountWalk per poll iteration until stable, plus one for the final satisfy snapshot --
        // ScopeNotFound is never reached because there is no selector miss on this unscoped wait, so
        // ConfirmationWalkCount must be zero and WalkCount must be small (not inflated by a spurious
        // confirmation walk on every poll).
        Assert.Equal(0, wait.ConfirmationWalkCount);
        Assert.True(wait.WalkCount >= 1, $"expected at least the final satisfy walk, saw {wait.WalkCount}");
    }

    /// <summary>Would this fact catch the ToolException carve-out mutation (deleting
    /// `catch (ToolException) { throw; }` so a bare `catch` swallows it)? NO -- SpatialOffscreenButton's
    /// confirmation walk never throws ToolException, it just returns a model, so mutating the carve-out
    /// changes nothing observable here. See the report for what fact WOULD catch it (closing the window
    /// mid-wait to provoke WindowHandleStale from the confirmation's own BuildModelAsync call) and why it
    /// is not written here.</summary>
    [Fact]
    public async Task Culled_but_present_message_never_claims_the_element_does_not_exist()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var ex = await Assert.ThrowsAsync<ToolException>(() => wait.WaitForStableAsync(
            handle, by: "automationId", value: "SpatialOffscreenButton",
            includeText: false, quietMs: 100, timeoutMs: 100, pollIntervalMs: 50));

        Assert.DoesNotContain("does not exist", ex.Message);
        Assert.DoesNotContain("No element matched", ex.Message);
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
