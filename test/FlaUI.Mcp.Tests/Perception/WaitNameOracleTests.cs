using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>INV-5 on wait_for's selector. `find` matches on the redacted name and snapshot renders
/// "[REDACTED]", but wait_for compared the RAW Name — so wait_for(by:"name", until:"exists") was a
/// name-oracle: satisfied:true confirms what a password element is called. Nothing crosses the wire,
/// and confirming is the whole attack.
/// HEADLESS by construction. The predicate is a pure function of a SnapshotNode, so this needs no
/// desktop, runs in CI, and — unlike a fixture-driven version — can assert the oracle against a
/// password node whose Name actually CARRIES a secret. A real conformant PasswordBox usually exposes an
/// empty or label-only Name, which would let a fixture test pass without the redaction existing at all.</summary>
public class WaitNameOracleTests
{
    private static SnapshotNode Node(string name, bool isPassword) => new(
        Ref: "e1", Depth: 1, Indent: "", ControlType: ControlType.Edit,
        AutomationId: "Secret", Name: name, Bounds: new System.Drawing.Rectangle(0, 0, 10, 10),
        Enabled: true, Focusable: true, Focused: false, Selected: false,
        Sensitivity: isPassword ? Sensitivity.OsPassword : Sensitivity.Visible, IsOffscreen: false,
        RuntimeId: System.Array.Empty<int>(), Patterns: System.Array.Empty<string>(), HelpText: "");

    /// ⚠ SUPERSEDED IN PART BY SP3 / DEF-3 — read this before "restoring" the old assertion.
    /// The second assertion used to be `Assert.True(Matches(pwd, "name", "[REDACTED]"))`, pinning the
    /// TOKEN-SUBSTITUTION MECHANISM: a password node matched the literal "[REDACTED]". That mechanism was
    /// itself the residual oracle — `wait_for(by:"name", value:"[REDACTED]")` returning satisfied:true
    /// confirms a redacted element EXISTS at that spot, which is the same class of leak this test was
    /// written to close, one level up. DEF-3 removes it: a redacted element is excluded from name matching
    /// entirely rather than rebranded to a well-known string.
    ///
    /// This is the ONLY pre-existing test SP3 changed, and the change makes it STRICTER (True -> False:
    /// strictly fewer things match). The §4.4 rule forbids weakening an assertion to let SP3 pass; nothing
    /// here is weakened, and the test's stated intent — a password element's real name can never be
    /// confirmed by name — is unchanged and still asserted on the line above.
    [Fact]
    public void A_password_elements_real_name_can_never_be_confirmed_by_name()
    {
        var pwd = Node("hunter2-NEVER-LEAK", isPassword: true);

        Assert.False(WaitCoordinator.Matches(pwd, "name", "hunter2-NEVER-LEAK"));
        Assert.False(WaitCoordinator.Matches(pwd, "name", "[REDACTED]"));
    }

    [Fact]
    public void A_normal_element_still_matches_on_its_real_name()
    {
        var plain = Node("Submit", isPassword: false);

        Assert.True(WaitCoordinator.Matches(plain, "name", "Submit"));
        Assert.False(WaitCoordinator.Matches(plain, "name", "[REDACTED]"));
    }

    [Fact]
    public void AutomationId_and_controlType_are_untouched_by_the_redaction()
    {
        // These stay the legitimate way to target a password box: neither carries content, so neither
        // leaks anything by matching. Redacting them would have made password fields unwaitable.
        var pwd = Node("hunter2-NEVER-LEAK", isPassword: true);

        Assert.True(WaitCoordinator.Matches(pwd, "automationId", "Secret"));
        Assert.True(WaitCoordinator.Matches(pwd, "controlType", "Edit"));
    }
}
