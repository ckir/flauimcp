using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
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

    /// <summary>ROADMAP opportunistic item 6, the desktop_get_text half. TextReadResult.TruncatedFrom
    /// was already pinned at the RECORD level (WaitContractShapeTests, ContentToolsTests) — but the tool
    /// re-projects through an ANONYMOUS object (ContentTools.cs:83), so a record field reaching the wire
    /// is a SEPARATE claim, and the one a consuming agent actually depends on. Both branches matter: the
    /// truncated read must carry the direction, and the untruncated read must still EMIT the key as an
    /// explicit null rather than dropping it (ToolResponse.cs:10 sets no DefaultIgnoreCondition), because
    /// an absent key and a null key are different things to parse against.</summary>
    [Fact]
    public async Task Get_text_surfaces_truncatedFrom_through_the_anonymous_projection()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ContentTools(perception, mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var snap = await perception.SnapshotAsync(handle,
            new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true, FullProperties = true });
        string textRef = RefLineHelper.RefFor(snap.Tree, "TextDoc");

        // TestApp's TextDoc is "line one\nline two\nline three" — a head read of 4 chars drops the tail.
        string truncated = await tools.DesktopGetText(
            window: handle.Id, @ref: textRef, maxLength: 4, fromEnd: false);
        using (var doc = JsonDocument.Parse(truncated))
        {
            Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal("tail", doc.RootElement.GetProperty("truncatedFrom").GetString());
        }

        string full = await tools.DesktopGetText(
            window: handle.Id, @ref: textRef, maxLength: 10000, fromEnd: false);
        using (var doc = JsonDocument.Parse(full))
        {
            Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("truncatedFrom").ValueKind);
        }
    }
}
