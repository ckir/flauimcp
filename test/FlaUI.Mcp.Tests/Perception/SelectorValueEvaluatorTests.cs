using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>EvaluateSelectorValueAsync backs exactly one surface — desktop_wait_for until=valueEquals —
/// and its by="controlType" arm was rewritten from a managed string compare over EVERY descendant to a
/// native UIA ByControlType pushdown. That arm had no coverage at all, so a mistake in it would surface
/// as a valueEquals that silently never satisfies: indistinguishable from a slow app, and only on the
/// one selector kind nobody tests by hand.</summary>
[Trait("Category", "Desktop")]
public class SelectorValueEvaluatorTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SelectorValueEvaluatorTests(TestAppFixture app) => _app = app;

    private async Task<(PerceptionManager Mgr, WindowHandle Handle)> ArrangeAsync(WindowManager w)
        => (new PerceptionManager(w, new RefRegistry(), new SnapshotCache()), await w.OpenByPidAsync(_app.Process.Id));

    [Fact]
    public async Task ControlType_still_resolves_through_the_native_pushdown()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (perception, handle) = await ArrangeAsync(mgr);

        var (found, _) = await perception.EvaluateSelectorValueAsync(handle, "controlType", "Button");

        Assert.True(found, "the TestApp has on-screen Buttons; the native ByControlType condition must " +
                           "find one exactly as the old whole-tree string compare did");
    }

    [Fact]
    public async Task An_unparseable_controlType_finds_nothing_rather_than_throwing()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (perception, handle) = await ArrangeAsync(mgr);

        // The old code compared ControlType.ToString() to this string and simply never matched. The
        // pushdown cannot build a condition for a name that does not parse, so it must degrade the SAME
        // way — no match — and must not throw or match something arbitrary. Callers see a plain timeout.
        var (found, value) = await perception.EvaluateSelectorValueAsync(handle, "controlType", "NotARealControlType");

        Assert.False(found);
        Assert.Null(value);
    }
}
