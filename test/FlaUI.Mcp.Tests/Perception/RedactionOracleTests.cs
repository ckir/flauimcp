using System.Linq;
using FlaUI.Core.AutomationElements;
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

        var result = await ScreenshotToolsFactory.For(Perception(mgr)).DesktopScreenshot();

        Assert.False(result.IsError);
        var meta = result.Content.OfType<TextContentBlock>().Single().Text;
        using var doc = System.Text.Json.JsonDocument.Parse(meta);
        int redactions = doc.RootElement.GetProperty("redactions").GetInt32();
        Assert.True(redactions > 0, $"full-desktop capture reported {redactions} redactions; metadata was {meta}");
    }

    /// <summary>SP4/A5. `window.title` is now the OWNING WINDOW's caption via Win32 GetWindowText, with the
    /// window ROOT's UIA Name as a fallback. This pins the empty-caption path's FIRST step: WindowTitle
    /// must ANSWER rather than throw when the caption cannot be read, because the fallback below it runs
    /// only if this returns empty.
    ///
    /// ⚠ HONEST LIMIT, recorded rather than implied: this exercises neither the UIA fallback nor its catch.
    /// Both need a live window with an empty caption whose provider throws on demand, which no fixture can
    /// stage — the AB-1 limitation. Ledgered as AB-6 in docs/coverage-debt.md.</summary>
    [Fact]
    public async Task An_unreadable_window_caption_answers_null_rather_than_throwing()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        await mgr.OpenByPidAsync(_app.Process.Id); // bind the fixture, as the sibling facts do

        Assert.Null(mgr.WindowTitle(System.IntPtr.Zero));
    }

    /// <summary>SP4/A5 — the load-bearing half of this fact is the NotEqual. `window.title` used to be the
    /// focused ELEMENT's Name, so under the pre-fix code focusing OkButton made title == "OK". It is now
    /// the owning window's caption.
    ///
    /// The literals mirror FlaUI.Mcp.TestApp.MainWindow: Title="FlaUI.Mcp TestApp" (MainWindow.xaml:4) and
    /// the OK button (MainWindow.xaml:34-35). TestApp is an exe launched by the fixture, not referenced as
    /// a library, so they are duplicated here intentionally - the same convention PasswordRedactionTests
    /// uses for the secret.
    ///
    /// The third assertion is the other half of the design: the element's name did not become
    /// unreachable, it moved to `descriptor`, which is rendered through SnapshotEngine.Render and is
    /// therefore CLASSIFIED. Removing the raw Name removed a leak, not a capability.</summary>
    [Fact]
    public async Task Focused_element_reports_the_WINDOW_title_not_the_element_name()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.AsWindow().Focus();
            win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!.Focus();
            return true;
        });

        var focused = await Perception(mgr).GetFocusedElementAsync();

        Assert.Equal("FlaUI.Mcp TestApp", focused.Title);
        Assert.NotEqual("OK", focused.Title);          // the pre-fix value
        Assert.Contains("OK", focused.DescriptorLine); // still reachable, through the classified path
    }

    /// <summary>SP4/A1 — the no-escalation case is the one that runs on every ordinary capture, so it is
    /// the one worth pinning on a real desktop: a healthy WPF window reports its bounds, nothing escalates,
    /// and both diagnostics are PRESENT and empty rather than absent. A field that appears only on failure
    /// teaches a consumer to ignore its absence.
    ///
    /// ⚠ The `unmaskedProcesses` assertion was added AFTER the panel signed this plan off, when AB-9 was
    /// resolved. It is a PRESENCE check and nothing more: this capture is WINDOW-scoped, and that field is
    /// empty by construction on that path — a named window that cannot be resolved throws rather than
    /// degrading, so only the full-desktop path can ever populate it. The populated case needs a live
    /// unbindable window, which no fixture can stage; it is the AB-1 limitation again and is ledgered
    /// beside AB-9.</summary>
    [Fact]
    public async Task An_ordinary_window_capture_reports_no_mask_escalations()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var result = await ScreenshotToolsFactory.For(Perception(mgr)).DesktopScreenshot(window: handle.Id);

        Assert.False(result.IsError);
        var meta = result.Content.OfType<TextContentBlock>().Single().Text;
        using var doc = System.Text.Json.JsonDocument.Parse(meta);
        Assert.Equal(0, doc.RootElement.GetProperty("maskEscalations").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("escalated").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("unmaskedProcesses").GetArrayLength());
    }
}
