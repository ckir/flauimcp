using System;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;
using Xunit.Abstractions;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP4/A1, OPEN #1 — MEASUREMENT, not a behaviour pin. Excluded from the Desktop gate by its
/// Measurement trait; run it deliberately.
///
/// THE QUESTION. A1 refuses a whole capture when a redact-worthy element reports no usable bounds and no
/// ancestor does either. The plan's OPEN #1 asks whether that makes a BENIGN element dying mid-scan refuse
/// an ordinary capture — a tooltip or menu closing during a UI transition is commonplace.
///
/// The chain the concern rests on is: a dead element makes SensitivityOf fail closed to Redact=true (it
/// already did before SP4), its BoundingRectangle throws, and then <c>GetParent</c> on that dead element
/// ALSO throws — so no ancestor resolves and A1 refuses.
///
/// ⚠ THE LAST LINK WAS ASSERTED BY THE PEER AND NEVER MEASURED, and the operator chose to measure before
/// deciding. If GetParent instead ANSWERS from a cached parent, the escalation succeeds, a benign
/// container is masked, and the concern largely evaporates. This test measures the three reads
/// AncestorRectSource and the mask walk actually make against a genuinely stale element.
///
/// It asserts almost nothing on purpose — a measurement that fails the suite on an OS behaviour change is
/// a liability. It records what happened; read the output.</summary>
// ⚠ BOTH traits, and the Desktop one is not optional. Measurement alone leaves this test INSIDE the
// headless filter (`Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect`), which is what the
// GitHub CI runner executes — and this test launches a GUI app, so it would go red on a machine with no
// desktop. MEASURED before fixing: with Measurement alone it matched and RAN under the headless filter.
// Both existing measurement classes carry both traits for the same reason.
[Trait("Category", "Desktop")]
[Trait("Category", "Measurement")]
public class StaleElementParentMeasurementTests
{
    private readonly ITestOutputHelper _out;
    public StaleElementParentMeasurementTests(ITestOutputHelper output) => _out = output;

    private static string Probe(string label, Func<string> read)
    {
        try { return $"{label}: ANSWERED -> {read()}"; }
        catch (Exception ex) { return $"{label}: THREW -> {ex.GetType().FullName}: {ex.Message}"; }
    }

    [Fact]
    public async Task What_a_stale_element_does_when_asked_for_bounds_identity_and_parent()
    {
        // Our OWN fixture instance: this test KILLS the app to staleness, so it must not touch the shared one.
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);

        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        AutomationElement? victim = null;
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            victim = win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"));
            return true;
        });

        Assert.NotNull(victim);
        _out.WriteLine("--- BEFORE the process dies (sanity: these must all ANSWER) ---");
        _out.WriteLine(Probe("BoundingRectangle", () => victim!.BoundingRectangle.ToString()));
        _out.WriteLine(Probe("RuntimeId", () => string.Join(",", victim!.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>())));
        _out.WriteLine(Probe("GetParent(raw view)", () =>
            victim!.Automation.TreeWalkerFactory.GetRawViewWalker().GetParent(victim!)?.ToString() ?? "<null>"));

        // Make it genuinely stale: kill the owning process and let UIA notice.
        app.Process.Kill(entireProcessTree: true);
        app.Process.WaitForExit(5000);
        await Task.Delay(750);

        _out.WriteLine("");
        _out.WriteLine("--- AFTER the process is killed (this is the measurement) ---");
        _out.WriteLine(Probe("BoundingRectangle", () => victim!.BoundingRectangle.ToString()));
        _out.WriteLine(Probe("RuntimeId", () => string.Join(",", victim!.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>())));
        _out.WriteLine(Probe("GetParent(raw view)", () =>
            victim!.Automation.TreeWalkerFactory.GetRawViewWalker().GetParent(victim!)?.ToString() ?? "<null>"));
        _out.WriteLine("");
        _out.WriteLine("READING IT: if GetParent THREW, AncestorRectSource answers null at every level and");
        _out.WriteLine("A1 REFUSES the capture — OPEN #1's concern stands. If it ANSWERED, escalation finds a");
        _out.WriteLine("container, the element is over-masked instead, and the concern largely evaporates.");
    }

    /// <summary>THE CASE OPEN #1 ACTUALLY DESCRIBES, and the one the process-kill measurement above does
    /// NOT settle. A tooltip, menu or list item disappearing during an ordinary UI transition kills the
    /// ELEMENT while its APP STAYS ALIVE — a completely different situation for UIA than a dead process,
    /// because the provider is still there to answer.
    ///
    /// The fixture gives this for free: ClearItemsButton_Click is `ItemList.Items.Clear()`
    /// (MainWindow.xaml.cs:67), so grabbing a ListBoxItem and then invoking that button removes it from a
    /// living window.
    ///
    /// ⚠ Invoked through the UIA InvokePattern, NOT synthetic input, so this needs no input lease — it is
    /// a pattern call into the app, not a SendInput event.
    ///
    /// This is the measurement that should drive the OPEN #1 decision, because it is the frequent case.
    /// If GetParent ANSWERS here, A1 escalates to the list and over-masks rather than refusing, and the
    /// refusal is confined to the rare whole-process-death case.</summary>
    [Fact]
    public async Task What_a_removed_element_does_while_its_app_is_still_alive()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);

        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        AutomationElement? victim = null;
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            victim = win.FindFirstDescendant(cf => cf.ByAutomationId("ItemA"));
            return true;
        });
        Assert.NotNull(victim);

        _out.WriteLine("--- BEFORE removal, app ALIVE (sanity: these must all ANSWER) ---");
        _out.WriteLine(Probe("BoundingRectangle", () => victim!.BoundingRectangle.ToString()));
        _out.WriteLine(Probe("RuntimeId", () => string.Join(",", victim!.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>())));
        _out.WriteLine(Probe("GetParent(raw view)", () =>
            victim!.Automation.TreeWalkerFactory.GetRawViewWalker().GetParent(victim!)?.ToString() ?? "<null>"));

        // Remove the item, leaving the window and its provider very much alive.
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.FindFirstDescendant(cf => cf.ByAutomationId("ClearItemsButton"))!.Patterns.Invoke.Pattern.Invoke();
            return true;
        });
        await Task.Delay(750);

        _out.WriteLine("");
        _out.WriteLine("--- AFTER removal, app STILL ALIVE (this is the measurement that matters) ---");
        _out.WriteLine(Probe("BoundingRectangle", () => victim!.BoundingRectangle.ToString()));
        _out.WriteLine(Probe("RuntimeId", () => string.Join(",", victim!.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>())));
        _out.WriteLine(Probe("GetParent(raw view)", () =>
            victim!.Automation.TreeWalkerFactory.GetRawViewWalker().GetParent(victim!)?.ToString() ?? "<null>"));

        // Confirm the app really is still alive, so a THREW result above cannot be blamed on a dead process.
        app.Process.Refresh();
        _out.WriteLine("");
        _out.WriteLine($"app still alive? HasExited={app.Process.HasExited}");
        _out.WriteLine(Probe("window root still readable", () =>
            mgr.WindowTitle(app.Process.MainWindowHandle) ?? "<null>"));
    }

    /// <summary>SP4 capstone round 1, finding 2 — MEASUREMENT of what `window.title` reports when the
    /// FOCUSED element lives in a popup (a WPF ContextMenu is its own top-level HWND).
    ///
    /// The peer's claim: `ResolveFocusedWindowAsync` resolves the owning window with
    /// `GetAncestor(hwnd, GA_ROOT)`, which stops at the first TOP-LEVEL window — and a popup IS one — so
    /// the "owning window" resolves to the POPUP, whose Win32 caption is typically empty. The agent would
    /// then get an empty `window.title` and lose the application context. The proposed fix is
    /// `GA_ROOTOWNER`, which follows OWNER links too.
    ///
    /// ⚠ `GA_ROOT` is PRE-EXISTING — introduced at `d818435`, an ancestor of SP4's branch point, so this
    /// is not an SP4 regression in the handle. But A5 CHANGED WHAT IS READ from that handle: before SP4
    /// `title` was the focused ELEMENT's Name (a menu item would have reported "Alpha"), and now it is the
    /// window's caption. So SP4 can have changed the SYMPTOM even though it did not change the resolution.
    /// That is exactly what this measures, and it is why the answer decides whether anything is owed here.
    ///
    /// ⚠ Needs an INPUT LEASE: the context menu is opened by a real right-click, as PopupGraftingTests
    /// does. Measurement only — it asserts nothing about the title, because the right answer is a policy
    /// question, not an OS fact.</summary>
    [Fact]
    public async Task What_window_title_reports_when_focus_is_inside_a_popup()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        await mgr.FocusAsync(handle);
        var baseline = await perception.GetFocusedElementAsync();
        _out.WriteLine($"BASELINE (focus in the main window): title=\"{baseline.Title}\"");

        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!.RightClick();
            return true;
        });
        await Task.Delay(600); // let the menu open as a desktop-level popup

        // Focus a menu item directly; a WPF MenuItem takes UIA focus once the menu is up.
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var item = desktop.FindFirstDescendant(cf => cf.ByAutomationId("MenuAlpha"));
            _out.WriteLine($"MenuAlpha found on the desktop? {item is not null}");
            item?.Focus();
            return true;
        });
        await Task.Delay(300);

        _out.WriteLine("");
        _out.WriteLine("--- WITH FOCUS INSIDE THE CONTEXT MENU (the measurement) ---");
        try
        {
            var f = await perception.GetFocusedElementAsync();
            _out.WriteLine($"GetFocusedElementAsync(): ANSWERED -> title=\"{f.Title}\"  descriptor=\"{f.DescriptorLine}\"");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"GetFocusedElementAsync(): THREW -> {ex.GetType().FullName}: {ex.Message}");
        }
        _out.WriteLine("");
        _out.WriteLine("READING IT: an EMPTY title means the peer is right that GA_ROOT stops at the popup,");
        _out.WriteLine("and that A5 turned a useful value (the menu item's name) into an empty one. A title");
        _out.WriteLine("naming the app means GA_ROOT already resolves to the owner here and nothing is owed.");
    }
}
