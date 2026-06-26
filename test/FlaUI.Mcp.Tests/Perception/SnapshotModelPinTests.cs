using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class SnapshotModelPinTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotModelPinTests(TestAppFixture app) => _app = app;

    private static string Legacy(SnapshotNode n)
    {
        var state = new System.Collections.Generic.List<string>();
        if (n.Enabled) state.Add("enabled");
        if (n.Focusable) state.Add("focusable");
        if (n.Focused) state.Add("focused");
        string shown = n.IsPassword ? "[REDACTED]" : n.Name;
        var sb = new System.Text.StringBuilder();
        sb.Append(n.Indent).Append('[').Append(n.Ref).Append("] ").Append(n.ControlType).Append(' ')
          .Append('"').Append(shown).Append('"')
          .Append(" @{").Append(n.Bounds.X).Append(',').Append(n.Bounds.Y).Append(',')
          .Append(n.Bounds.Width).Append(',').Append(n.Bounds.Height).Append('}')
          .Append(" {").Append(string.Join(", ", state)).Append('}');
        if (n.Patterns.Count > 0) sb.Append(" [").Append(string.Join(",", n.Patterns)).Append(']');
        return sb.ToString();
    }

    [Fact]
    public async Task FormatNode_matches_legacy_format_per_node_from_one_walk()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (renderLines, legacyLines, count, nodeCount) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var opts = new SnapshotOptions();
            var refs = new RefRegistry(); refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(win, System.Array.Empty<AutomationElement>(), opts, refs, handle.Id);
            var rendered = SnapshotEngine.Render(model, opts).Split('\n').Where(l => l.Length > 0 && l.Contains("[e")).ToArray();
            var legacy = model.Nodes.Select(Legacy).ToArray();
            return (rendered, legacy, model.NodeCount, model.Nodes.Count());
        });

        Assert.Equal(legacyLines.Length, renderLines.Length);
        Assert.Equal(count, nodeCount);
    }
}
