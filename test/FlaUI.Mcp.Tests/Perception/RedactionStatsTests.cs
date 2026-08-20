using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// Task 10: `redacted` (the SHIPPED counter) keeps its literal OS-password meaning, because a consumer
/// already keys off it; `redactedCount` is the new total across BOTH redaction sources. An earlier draft
/// wired these tallies and tested neither.
public class RedactionStatsTests
{
    private static SnapshotNode Node(string name, Sensitivity s) => new(
        Ref: "e1", Depth: 1, Indent: "", ControlType: ControlType.Edit,
        AutomationId: "aid", Name: name, Bounds: new System.Drawing.Rectangle(0, 0, 10, 10),
        Enabled: true, Focusable: true, Focused: false, Selected: false,
        Sensitivity: s, IsOffscreen: false,
        RuntimeId: System.Array.Empty<int>(), Patterns: System.Array.Empty<string>(), HelpText: "");

    [Fact]
    public void The_os_counter_keeps_its_meaning_while_redactedCount_counts_both_sources()
    {
        var model = new SnapshotModel(new SnapshotItem[]
        {
            Node("pw",      Sensitivity.OsPassword),
            Node("card",    new Sensitivity(true, RedactionSource.Rule, "card-fields")),
            Node("Username", Sensitivity.Visible),
        });

        var stats = PerceptionManager.Tally("w1:1", model);

        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.OsPasswordCount); // OS only — a DIFFERENT number from RedactedCount below
        Assert.Equal(2, stats.RedactedCount);  // both redacted nodes
    }

    [Fact]
    public void RedactedBy_names_the_source()
    {
        Assert.Equal("os", ElementContent.RedactedBy(Sensitivity.OsPassword));
        Assert.Equal("rule:card-fields", ElementContent.RedactedBy(new Sensitivity(true, RedactionSource.Rule, "card-fields")));
        Assert.Null(ElementContent.RedactedBy(Sensitivity.Visible));
    }
}
