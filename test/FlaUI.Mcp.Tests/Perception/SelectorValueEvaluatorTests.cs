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

    /// <summary>INV-5 on the value path. wait_for(until:valueEquals) reports whether an element's value
    /// equals a caller-supplied string, so without a password floor it is an ORACLE: no secret crosses
    /// the wire, but a guess can be CONFIRMED, and confirming is the attack. The fixture's PasswordBox
    /// happens to return an empty ValuePattern value, so this passed before the floor existed too —
    /// which is exactly why the assertion is written against the ORACLE (can a correct guess ever be
    /// confirmed?) rather than against the returned string. A provider that sets IsPassword and still
    /// exposes the value through LegacyIAccessible would satisfy the wait; it now cannot.</summary>
    [Fact]
    public async Task A_password_field_is_never_a_valueEquals_oracle()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (perception, handle) = await ArrangeAsync(mgr);
        var wait = new WaitCoordinator(perception);

        // The literal the TestApp puts in its PasswordBox (MainWindow.xaml.cs). The test project does
        // not reference the app assembly, so it is repeated here as ContentToolsTests already does.
        const string secret = "hunter2-NEVER-LEAK";

        var (found, value) = await perception.EvaluateSelectorValueAsync(handle, "automationId", "Secret");
        Assert.True(found);              // the element resolves — it is not hidden, only redacted
        Assert.Null(value);              // and yields NO value to compare against
        Assert.NotEqual(secret, value);

        var r = await wait.WaitForAsync(handle, "automationId", "Secret",
            until: "valueEquals", equals: secret, timeoutMs: 1200, pollIntervalMs: 300);
        Assert.False(r.Satisfied, "a correct password guess must never be confirmable through valueEquals");

        // The by="name" route is the sharper attack and needs its own assertion: the native ByName
        // condition matches the element's UNREDACTED name, and the value fallback then reads that same
        // raw Name -- so selector and value agree by construction and the equality holds for free,
        // without the caller ever knowing the automationId. The IsPassword floor is what stops it.
        var viaName = await wait.WaitForAsync(handle, "name", secret,
            until: "valueEquals", equals: secret, timeoutMs: 1200, pollIntervalMs: 300);
        Assert.False(viaName.Satisfied);
    }
}
