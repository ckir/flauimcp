using System.Drawing;
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class ScrapeWarningEmissionTests
{
    private sealed class FakeScreen : IScreenImageSource
    {
        private readonly Color _fill;
        private readonly bool _speck;
        public FakeScreen(Color fill, bool speck = false) { _fill = fill; _speck = speck; }
        public Bitmap Acquire(Rectangle absolute)
        {
            var b = new Bitmap(absolute.Width, absolute.Height);
            using (var g = Graphics.FromImage(b))
            using (var brush = new SolidBrush(_fill))
                g.FillRectangle(brush, 0, 0, absolute.Width, absolute.Height);
            // One contrasting block, big enough that the detector's 64x64 grid cannot step over it.
            if (_speck)
                using (var g = Graphics.FromImage(b))
                using (var brush = new SolidBrush(Color.White))
                    g.FillRectangle(brush, 0, 0, absolute.Width / 2, absolute.Height / 2);
            return b;
        }
    }

    private static readonly Rectangle Area = new(0, 0, 400, 300);
    private static string[] Codes(CaptureResult r) => r.CaptureWarnings.Select(w => w.Code).ToArray();

    private static CaptureResult Run(CaptureScope scope, bool uniform, params CaptureWarning[] soFar)
        => ScreenCapture.CaptureRectangle(Area, System.Array.Empty<Rectangle>(), 0, scope, soFar,
                                          new FakeScreen(Color.Black, speck: !uniform));

    // The whole point of the seam: a uniform FULL DESKTOP raises desktopCanvasUniform.
    [Fact]
    public void A_uniform_full_desktop_scrape_emits_desktopCanvasUniform()
        => Assert.Equal(new[] { "desktopCanvasUniform" }, Codes(Run(CaptureScope.FullDesktop, uniform: true)));

    [Fact]
    public void A_normal_full_desktop_scrape_emits_nothing()
        => Assert.Empty(Codes(Run(CaptureScope.FullDesktop, uniform: false)));

    // A window-scope FALLBACK scrape gets the first-stage check -- the round-4 fold.
    [Fact]
    public void A_uniform_window_scope_scrape_emits_uniformCanvas()
        => Assert.Equal(new[] { "uniformCanvas" }, Codes(Run(CaptureScope.Window, uniform: true)));

    // ⚠ THE DELIBERATE SILENCE. Element scope must NOT claim the whole window rendered as one colour.
    // Pinned so the gap reads as a decision rather than an omission someone later "fixes".
    [Fact]
    public void An_element_scope_uniform_scrape_stays_silent()
        => Assert.Empty(Codes(Run(CaptureScope.Element, uniform: true)));

    // OCR runs no detector at all: neither a whole desktop nor a PrintWindow capture.
    [Fact]
    public void A_uniform_ocr_scrape_stays_silent()
        => Assert.Empty(Codes(Run(CaptureScope.OcrRegion, uniform: true)));

    // warningsSoFar is the caller's decision (a scrapeFallback* code) and must be PRESERVED, not replaced.
    [Fact]
    public void Warnings_from_the_caller_are_appended_to_never_replaced()
    {
        var prior = CaptureWarnings.For(CaptureWarnings.ScrapeFallbackTargetUnresponsive);
        var codes = Codes(Run(CaptureScope.Window, uniform: true, prior));
        Assert.Equal(new[] { "scrapeFallbackTargetUnresponsive", "uniformCanvas" }, codes);
    }

    [Fact]
    public void The_scrape_always_reports_screenScrape_as_its_method()
        => Assert.Equal("screenScrape", Run(CaptureScope.FullDesktop, uniform: false).CaptureMethod);
}
