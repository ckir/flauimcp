using System;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
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
}
