using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP0 layer 2: pins the WIRE shape of desktop_wait_for. The tool projects through an
/// ANONYMOUS object (SnapshotTools.cs:70), so a field added to WaitForResult alone never reaches
/// the wire — and ToolResponse.cs:12 sets no DefaultIgnoreCondition, so nulls are EMITTED, not
/// omitted. Both facts are load-bearing for SP1 and are pinned here.</summary>
[Trait("Category", "Desktop")]
public class ToolProjectionShapeTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ToolProjectionShapeTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Wait_for_timeout_emits_all_four_keys_including_explicit_nulls()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var tools = new SnapshotTools(perception, wait);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // A selector that cannot match, with a short budget: a deterministic timeout.
        string json = await tools.DesktopWaitFor(
            window: handle.Id, by: "automationId", value: "NoSuchElement_SP0",
            until: "exists", equals: null, timeoutMs: 600, pollIntervalMs: 200);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("satisfied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ref").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("snapshotId").ValueKind);
        Assert.True(root.GetProperty("elapsedMs").GetInt32() >= 0);
    }

    [Fact]
    public async Task Wait_for_timeout_emits_the_three_diagnostic_keys_as_explicit_nulls_when_undetermined()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var tools = new SnapshotTools(perception, wait);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        string json = await tools.DesktopWaitFor(
            window: handle.Id, by: "automationId", value: "NoSuchElement_SP0",
            until: "exists", equals: null, timeoutMs: 600, pollIntervalMs: 200);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("unsatisfiedBecause").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("elementBounds").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("windowBounds").ValueKind);
    }
}
