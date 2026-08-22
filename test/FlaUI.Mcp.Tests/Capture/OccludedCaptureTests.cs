using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

[Trait("Category", "Desktop")]
public class OccludedCaptureTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public OccludedCaptureTests(TestAppFixture app) => _app = app;

    // THE SUCCESS CRITERION OF THE WHOLE FEATURE: capture a window that is behind another window and get
    // THAT window's pixels, not the occluder's.
    [Fact]
    public async Task An_occluded_window_yields_its_own_pixels_not_the_occluders()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();

        // CONTROL: capture while it is on top.
        using var control = src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000);
        Assert.NotNull(control);

        // Cover it with a topmost opaque window over the SAME rect.
        using var occluder = OccluderWindow.Show(rect);
        await Task.Delay(400);   // let DWM compose the occluder

        // ⛔⛔ THE POSITIVE CONTROL. IT WAS WRONG THREE TIMES, IN THE ONE TEST THAT PROVES THIS FEATURE
        // WORKS, AND ALL THREE WRONG VERSIONS PASSED. Read this before touching it.
        //
        // v1 (as the plan pinned it): `Assert.True(scraped.Png.Length > 0)` under a comment claiming it
        //    "asserts the defect exists". True of ANY successful capture, so if the occluder never
        //    covered the window, everything below passed trivially.
        //
        // v2: assert the scrape differs from the PrintWindow `control`. INERT. MEASURED with the
        //    occluder shrunk to 1x1 so it covers nothing -- still passed, because a PrintWindow render
        //    and a screen scrape of the same window at the same size agree on **0.000** of the sampled
        //    pixels. Different backends; that comparison can never fail.
        //
        // v3: scrape BEFORE the occluder vs scrape AFTER. Also inert, and the reason is worth keeping:
        //    this test never brings the target to the foreground -- deliberately, that is the whole
        //    point -- so on a developer machine the window is typically ALREADY behind the terminal
        //    running the tests, whose output scrolls between the two captures. MEASURED: the two scrapes
        //    differ whether or not the occluder exists.
        //
        // What actually works is to look for the OCCLUDER ITSELF. It paints one flat colour nothing else
        // on screen is likely to be, so: that colour must appear in the SCRAPE (proving it really covered
        // the window) and must NOT appear in the PrintWindow capture (which is the feature).
        var scraped = ScreenCapture.CaptureRectangle(rect, Array.Empty<Rectangle>(), 0,
            CaptureScope.Window, Array.Empty<CaptureWarning>());
        Assert.True(scraped.Png.Length > 0);
        using var scrapedBmp = (Bitmap)Image.FromStream(new System.IO.MemoryStream(scraped.Png));
        Assert.True(FractionOfOccluderColour(scrapedBmp) > 0.5,
            "the occluder's colour is not in the scrape, so it never covered the window - this test " +
            "would have passed without exercising occlusion at all");

        // THE ASSERTION. PrintWindow still returns the target's own content.
        using var occludedCapture = src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000);
        Assert.NotNull(occludedCapture);
        Assert.False(UniformCanvasDetector.IsUniform(occludedCapture!),
            "the occluded capture came back a single colour - PrintWindow did not render the target");
        Assert.True(SimilarEnough(control!, occludedCapture!),
            "the occluded capture does not match the control - it may be a photograph of the occluder");

        // THE SAME FACT FROM THE OTHER SIDE, and the one that fails loudest if PrintWindow ever starts
        // photographing the screen: the occluder's colour covered >50% of the SCRAPE above, and must be
        // absent here.
        Assert.True(FractionOfOccluderColour(occludedCapture!) < 0.01,
            "the occluder's colour is IN the PrintWindow capture - it photographed the occluder rather " +
            "than rendering the window");
    }

    // Compare a coarse grid rather than every pixel: a caret blink or a hover highlight must not fail it.
    /// <summary>Fraction of sampled pixels that are the occluder's flat colour. Sampled on the same
    /// coarse grid as SimilarEnough, and matched with a small tolerance because DWM may blend edges.</summary>
    private static double FractionOfOccluderColour(Bitmap b)
    {
        int hit = 0, total = 0;
        for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 32))
            for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 32))
            {
                total++;
                var p = b.GetPixel(x, y);
                if (p.R > 200 && p.G < 60 && p.B > 200) hit++;   // magenta
            }
        return total > 0 ? (double)hit / total : 0;
    }

    private static bool SimilarEnough(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        int same = 0, total = 0;
        for (int y = 0; y < a.Height; y += Math.Max(1, a.Height / 32))
            for (int x = 0; x < a.Width; x += Math.Max(1, a.Width / 32))
            {
                total++;
                if (a.GetPixel(x, y).ToArgb() == b.GetPixel(x, y).ToArgb()) same++;
            }
        return total > 0 && (double)same / total > 0.90;
    }

    // The RUNTIME half of the metadata contract. Task 20's sweep reads the projection's SOURCE; this
    // reads the JSON the tool actually produced. Both are needed: the sweep catches a field removed from
    // the code, this catches a shape that is wrong only once it is serialized.
    //
    // ⚠ The plan sketched this against a `TestHost.ResolveScreenshotTools` helper and warned that the
    // repo may not have one. It does not — but Task 20 created `ScreenshotToolsFactory`, which builds the
    // tool exactly as production does (and, crucially, cannot forget the fail-closed delegates). Using it
    // here rather than inventing a second construction path is the point of having it.
    [Fact]
    public async Task The_emitted_metadata_carries_all_nine_documented_fields()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());

        var call = await ScreenshotToolsFactory.For(perception).DesktopScreenshot(window: handle.Id);
        Assert.False(call.IsError);
        var json = call.Content.OfType<TextContentBlock>().First().Text;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var field in new[]
        {
            "bounds", "dpiScale", "scaleApplied", "redactions", "maskEscalations", "escalated",
            "unmaskedProcesses", "captureMethod", "captureWarnings",
        })
            Assert.True(doc.RootElement.TryGetProperty(field, out _),
                $"the emitted metadata is missing '{field}'");

        Assert.Equal("printWindow", doc.RootElement.GetProperty("captureMethod").GetString());
        // ALWAYS PRESENT, and empty is the normal case -- an absence would read as "nothing to report"
        // only by accident.
        Assert.Equal(System.Text.Json.JsonValueKind.Array,
                     doc.RootElement.GetProperty("captureWarnings").ValueKind);
    }
}
