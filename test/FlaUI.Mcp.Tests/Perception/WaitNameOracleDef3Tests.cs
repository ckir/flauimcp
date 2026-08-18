using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// DEF-3: the redaction token was a LOCATOR ORACLE on wait_for(by:"name"). Matching the REDACTED string
/// meant wait_for(by:"name", value:"[REDACTED]") confirmed the existence of every redacted element.
/// The fix excludes a redacted element from NAME matching entirely — driven by the element's
/// classification, never by the query string, so a legitimately-named "[REDACTED]" element is unaffected.
public class WaitNameOracleDef3Tests
{
    private static SnapshotNode Node(string name, Sensitivity s) => new(
        Ref: "e1", Depth: 1, Indent: "", ControlType: ControlType.Edit,
        AutomationId: "aid1", Name: name, Bounds: new System.Drawing.Rectangle(0, 0, 10, 10),
        Enabled: true, Focusable: true, Focused: false, Selected: false,
        Sensitivity: s, IsOffscreen: false,
        RuntimeId: System.Array.Empty<int>(), Patterns: System.Array.Empty<string>(), HelpText: "");

    private static readonly Sensitivity RuleHidden = new(true, RedactionSource.Rule, "r1");

    /// HALF 1 — an OS-password element must not be confirmable by the TOKEN.
    [Fact]
    public void A_redacted_element_is_not_matched_by_the_redaction_token()
    {
        Assert.False(WaitCoordinator.Matches(Node("hunter2", Sensitivity.OsPassword), "name", "[REDACTED]"));
    }

    /// HALF 1 — nor by its REAL name. This is the actual oracle: a correct guess must not be confirmable.
    [Fact]
    public void A_redacted_element_is_not_matched_by_its_real_name()
    {
        Assert.False(WaitCoordinator.Matches(Node("hunter2", Sensitivity.OsPassword), "name", "hunter2"));
    }

    /// HALF 1 — the same must hold for RULE-driven redaction, not just the OS password flag.
    [Fact]
    public void A_rule_redacted_element_is_not_matched_by_its_real_name()
    {
        Assert.False(WaitCoordinator.Matches(Node("4111", RuleHidden), "name", "4111"));
    }

    /// HALF 2 — the other half, and the one that catches a query-banning implementation: an element
    /// GENUINELY NAMED "[REDACTED]" that is NOT redacted IS still matched. Scrubbed logs and compliance
    /// reports contain this literal; banning the query would create a permanent blind spot.
    [Fact]
    public void A_visible_element_genuinely_named_the_token_is_still_matched()
    {
        Assert.True(WaitCoordinator.Matches(Node("[REDACTED]", Sensitivity.Visible), "name", "[REDACTED]"));
    }

    /// BC-1 TARGETABILITY — a redacted element stays findable by automationId and controlType. Only NAME
    /// search is withheld. If this fails, the fix has over-redacted and made password fields untargetable.
    [Fact]
    public void A_redacted_element_is_still_matched_by_automationId_and_controlType()
    {
        var n = Node("hunter2", Sensitivity.OsPassword);
        Assert.True(WaitCoordinator.Matches(n, "automationId", "aid1"));
        Assert.True(WaitCoordinator.Matches(n, "controlType", "Edit"));
    }
}
