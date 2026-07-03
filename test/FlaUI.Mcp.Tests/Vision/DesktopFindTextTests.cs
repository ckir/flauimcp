// test/FlaUI.Mcp.Tests/Vision/DesktopFindTextTests.cs
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Vision;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

/// <summary>Phase 9 Task 10 dealbreaker guard (§10b/§6): proves the OCR->coordinate pipeline lands a REAL click
/// coordinate inside a REAL control, end to end, on the live display scale. Fixture: the TestApp's
/// "RebuildItemsButton" (Content="Rebuild Items") — a unique, multi-word visible string at a known UIA-reported
/// position. desktop_find_text OCRs the live window, we take the top match's (xPct,yPct), convert it back to a
/// physical screen point via CoordinateMath.PctToPhysical (the SAME helper desktop_click_at uses), and assert that
/// point falls inside the button's real BoundingRectangle. Runs on a live console only (Category=Desktop) — needs
/// an actual rendered/unlocked desktop session (ScreenCapture) AND a Windows OCR language pack installed
/// (Windows.Media.Ocr) for WindowsMediaOcrEngine to produce real recognitions; the controller must ensure both are
/// available when running this test.</summary>
[Trait("Category", "Desktop")]
public class DesktopFindTextTests
{
    [Fact]
    public async Task Find_text_click_coordinate_lands_inside_the_control()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        using (mgr)
        {
            var refs = new RefRegistry();
            var perception = new PerceptionManager(mgr, refs, new SnapshotCache());
            var finder = new TextFinder(new WindowsMediaOcrEngine());
            var tools = new FindTextTools(perception, finder);

            var handle = await mgr.OpenByPidAsync(app.Process.Id);
            await mgr.FocusAsync(handle); // must be foreground/unoccluded for the capture to see real pixels

            // Ground truth: the button's real UIA-reported screen rect + the window's rect (same source
            // desktop_get_bounds/desktop_click_at would read).
            var (winRect, btnRect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            {
                var btn = win.FindFirstDescendant(cf => cf.ByAutomationId("RebuildItemsButton"));
                Assert.NotNull(btn);
                return (win.BoundingRectangle, btn!.BoundingRectangle);
            });

            var json = await tools.DesktopFindText("Rebuild Items", handle.Id);
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("error", out _),
                $"desktop_find_text returned an error: {json}");

            var matches = doc.RootElement.GetProperty("matches");
            Assert.True(matches.GetArrayLength() > 0, $"expected at least one OCR match for 'Rebuild Items', got: {json}");
            var top = matches[0];
            double xPct = top.GetProperty("xPct").GetDouble();
            double yPct = top.GetProperty("yPct").GetDouble();

            // Same mapping desktop_click_at uses: window-relative fraction -> physical screen px.
            var (px, py) = CoordinateMath.PctToPhysical(winRect.X, winRect.Y, winRect.Width, winRect.Height, xPct, yPct);

            Assert.True(px >= btnRect.Left && px <= btnRect.Right && py >= btnRect.Top && py <= btnRect.Bottom,
                $"derived click ({px},{py}) does not land inside RebuildItemsButton's bounds {btnRect} " +
                $"(window {winRect}, xPct={xPct}, yPct={yPct}, match={top.GetRawText()})");
        }
    }
}
