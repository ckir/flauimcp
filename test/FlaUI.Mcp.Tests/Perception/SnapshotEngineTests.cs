using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class SnapshotEngineTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotEngineTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Walk_surfaces_interactive_controls_with_refs_bounds_and_patterns()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (tree, count) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            refs.BeginSnapshot(handle.Id);
            return SnapshotEngine.Walk(win, Array.Empty<FlaUI.Core.AutomationElements.AutomationElement>(),
                new SnapshotOptions(), refs, handle.Id);
        });

        Assert.True(count > 0);
        Assert.Contains("[e", tree);              // refs assigned
        Assert.Contains("Button", tree);          // OkButton surfaced
        Assert.Contains("\"OK\"", tree);          // its name
        Assert.Contains("@{", tree);              // bounding rect present on every line
        Assert.Contains("Invoke", tree);          // button advertises InvokePattern
        // interactiveOnly default prunes the unnamed StackPanel container:
        Assert.DoesNotContain("Pane \"\"", tree);
    }

    [Fact]
    public async Task FullProperties_appends_automation_ids()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (tree, _) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            refs.BeginSnapshot(handle.Id);
            return SnapshotEngine.Walk(win, Array.Empty<FlaUI.Core.AutomationElements.AutomationElement>(),
                new SnapshotOptions { FullProperties = true }, refs, handle.Id);
        });

        Assert.Contains("aid=OkButton", tree);
    }
}
