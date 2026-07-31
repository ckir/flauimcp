using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class PasswordRedactionTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PasswordRedactionTests(TestAppFixture app) => _app = app;

    // Mirrors FlaUI.Mcp.TestApp.MainWindow.SecretValue. TestApp is an exe launched by the fixture,
    // not referenced as a library, so the literal is duplicated here intentionally.
    private const string Secret = "hunter2-NEVER-LEAK";

    [Fact]
    public async Task Password_field_value_is_redacted_in_the_snapshot()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // fullProperties so AutomationId (Secret) and HelpText are emitted too — proves none of them
        // carry the typed password.
        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });

        Assert.DoesNotContain(Secret, snap.Tree);  // the typed password never reaches agent context
        Assert.Contains("[REDACTED]", snap.Tree);  // the IsPassword field is masked in the output
    }
}

/// <summary>HEADLESS companion to PasswordRedactionTests (same reasoning as WaitNameOracleTests.cs:11-14):
/// a real conformant PasswordBox exposes an empty Name, so a fixture-driven version would pass even if the
/// redaction did not exist. These construct a node/descriptor whose Name actually CARRIES a secret. Split
/// into its own class (rather than added to PasswordRedactionTests above) because that class is
/// IClassFixture&lt;TestAppFixture&gt; — sharing it would launch the real TestApp process even when only
/// these headless facts are selected by a Category!=Desktop filter.</summary>
public class PasswordRedactionTestsHeadless
{
    private const string Secret = "hunter2-NEVER-LEAK";

    private static SnapshotNode PasswordNode(string name) => new(
        Ref: "e1", Depth: 1, Indent: "", ControlType: ControlType.Edit,
        AutomationId: "", Name: name, Bounds: new System.Drawing.Rectangle(0, 0, 10, 10),
        Enabled: true, Focusable: true, Focused: false, Selected: false,
        Sensitivity: Sensitivity.OsPassword, IsOffscreen: false,
        RuntimeId: System.Array.Empty<int>(), Patterns: System.Array.Empty<string>(), HelpText: "");

    /// <summary>The rendered tree is the largest wire surface. Asserting only the secret's ABSENCE
    /// would pass against an empty render, so both halves are asserted.</summary>
    [Fact]
    public void A_password_names_secret_never_reaches_the_rendered_snapshot()
    {
        var model = new SnapshotModel(new SnapshotItem[] { PasswordNode(Secret) });

        var rendered = SnapshotEngine.Render(model, new SnapshotOptions { InteractiveOnly = false });

        Assert.DoesNotContain(Secret, rendered);
        Assert.Contains("[REDACTED]", rendered);
    }

    /// <summary>The descriptor deliberately stores the RAW name because it is the resolver's only
    /// lookup key when AutomationId is absent (PerceptionManager.cs:580, RefRegistry.cs:181-182).
    /// This pins the OTHER half of that bargain: the raw name must never be echoed in the failure
    /// diagnostic. If someone ever adds Name to RefRegistry.Key, this fails.</summary>
    [Fact]
    public void A_password_names_secret_never_reaches_a_ref_resolution_failure_message()
    {
        var refs = new RefRegistry();
        var descriptor = new ElementDescriptor(
            System.Array.Empty<int>(), ControlType.Edit, "", Secret, null,
            System.Array.Empty<int>(), false);

        var ex = Assert.Throws<ToolException>(() =>
            refs.ResolveDescriptor(descriptor, System.Array.Empty<AutomationElement>(), "e1"));

        Assert.DoesNotContain(Secret, ex.Message);
        Assert.DoesNotContain(Secret, ex.SuggestedRecovery ?? "");
    }
}
