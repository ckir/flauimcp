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
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // fullProperties so AutomationId (Secret) and HelpText are emitted too — proves none of them
        // carry the typed password.
        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });

        Assert.DoesNotContain(Secret, snap.Tree);  // the typed password never reaches agent context
        Assert.Contains("[REDACTED]", snap.Tree);  // the IsPassword field is masked in the output
    }
}
