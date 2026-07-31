using System.Drawing;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RedactionEgressTests
{
    private static SnapshotNode Node(string name, Sensitivity s) =>
        new("e1", 0, "", ControlType.Edit, "aid", name, new Rectangle(0, 0, 10, 10),
            true, true, false, false, s, false, new[] { 1 }, new string[0], "");

    /// DEFAULT PATH: with no rules configured the render must be BYTE-IDENTICAL to today.
    /// The redacted: marker is emitted ONLY for Source == Rule — emitting it for an OS password would
    /// change the rendered line of every password field in every existing install.
    [Fact]
    public void An_os_password_node_renders_exactly_as_before()
    {
        var text = SnapshotEngine.Render(new SnapshotModel(new[] { Node("secret", Sensitivity.OsPassword) }),
                                         new SnapshotOptions());
        Assert.Contains("\"[REDACTED]\"", text);
        Assert.DoesNotContain("redacted:", text);
    }

    [Fact]
    public void A_rule_redacted_node_names_its_rule_in_the_state_list()
    {
        var s = new Sensitivity(true, RedactionSource.Rule, "card-fields");
        var text = SnapshotEngine.Render(new SnapshotModel(new[] { Node("4111 1111", s) }), new SnapshotOptions());
        Assert.Contains("\"[REDACTED]\"", text);
        Assert.Contains("redacted:rule:card-fields", text);
    }

    [Fact]
    public void A_visible_node_renders_its_real_name()
    {
        var text = SnapshotEngine.Render(new SnapshotModel(new[] { Node("Username", Sensitivity.Visible) }),
                                         new SnapshotOptions());
        Assert.Contains("\"Username\"", text);
        Assert.DoesNotContain("[REDACTED]", text);
    }
}
