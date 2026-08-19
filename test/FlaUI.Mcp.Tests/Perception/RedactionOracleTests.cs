using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP3 Task 8 — the PIXEL egress family (spec §5, family C) and its two defects.
///
/// DEF-1: the pixel path read IsPassword RAW inside a swallowing try/catch, while all eleven TEXT sites
/// used RedactionPolicy.IsPasswordOrFailClosed. A provider whose IsPassword read THREW therefore had its
/// text redacted and its PIXELS CAPTURED — the two egress families disagreed about the same element.
///
/// DEF-2: full-desktop capture passed Array.Empty&lt;Rectangle&gt;(), so the one capture mode that
/// photographs every window at once was the only mode that masked nothing.</summary>
[Trait("Category", "Desktop")]
public class RedactionOracleTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public RedactionOracleTests(TestAppFixture app) => _app = app;

    private static PerceptionManager Perception(WindowManager mgr) =>
        new(mgr, new RefRegistry(), new SnapshotCache());

    /// <summary>⚠ HONEST LABEL: this is a REGRESSION GUARD, not a defect pin. It does NOT go red against
    /// the pre-fix code, and it cannot: DEF-1's fail-open is only observable when the IsPassword read
    /// THROWS, and a conformant WPF PasswordBox answers the property normally, so the old raw read already
    /// collected this rect. No fixture can stage a throwing UIA provider — the same limitation
    /// PasswordRedactionTests.cs:38-43 documents for the snapshot path.
    ///
    /// The fail-CLOSED primitive itself is pinned headlessly (PerceptionManagerShouldFixTests.cs:11-13).
    /// What this fact adds is that the pixel path still finds a real password field at all after being
    /// rerouted through the classifier — i.e. that the fix did not break the working case while closing
    /// the throwing one.</summary>
    [Fact]
    public async Task Window_capture_geometry_reports_a_mask_rect_for_a_password_field()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var geo = await Perception(mgr).ResolveWindowCaptureGeometryAsync(handle, null);

        Assert.False(geo.Denied);
        Assert.False(geo.Minimized);
        Assert.NotEmpty(geo.MaskRects);
    }

    /// <summary>DEF-2 — a TRUE red→green pin. It goes through the TOOL rather than the manager, which is
    /// deliberate and load-bearing twice over: the tool is the thing that passed Array.Empty, so a manager-
    /// level assertion would leave the actual defect unpinned and would still pass if someone reverted the
    /// call site; and going through the tool is what lets this fact COMPILE against the pre-fix tree, which
    /// an assertion on the new AllPasswordRectsAsync could not.
    ///
    /// Asserts the metadata's own `redactions` counter — the number the tool reports to the agent.</summary>
    [Fact]
    public async Task Full_desktop_capture_masks_a_visible_password_field()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        await mgr.OpenByPidAsync(_app.Process.Id); // ensure the TestApp window is bound and on screen

        var result = await new ScreenshotTools(Perception(mgr)).DesktopScreenshot();

        Assert.False(result.IsError);
        var meta = result.Content.OfType<TextContentBlock>().Single().Text;
        using var doc = System.Text.Json.JsonDocument.Parse(meta);
        int redactions = doc.RootElement.GetProperty("redactions").GetInt32();
        Assert.True(redactions > 0, $"full-desktop capture reported {redactions} redactions; metadata was {meta}");
    }
}
