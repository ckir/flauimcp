using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class TextFinderTests
{
    private sealed class FakeEngine : IOcrEngine
    {
        private readonly IReadOnlyList<OcrWord> _words;
        public FakeEngine(params OcrWord[] words) => _words = words;
        public Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] png) => Task.FromResult(_words);
    }

    [Fact]
    public async Task Find_maps_each_match_to_screen_and_window_coords()
    {
        // OCR reports "Submit" at bitmap rect (40,40,20,10) on line 0. scale 1, capture origin (0,0), window 100x100.
        var finder = new TextFinder(new FakeEngine(new OcrWord("Submit", 40, 40, 20, 10, 0)));
        var matches = await finder.FindAsync(
            query: "Submit", pngBytes: System.Array.Empty<byte>(), mode: MatchMode.Fuzzy, all: true,
            scaleApplied: 1.0, captureX: 0, captureY: 0, winLeft: 0, winTop: 0, winWidth: 100, winHeight: 100);
        var m = Assert.Single(matches);
        Assert.Equal("Submit", m.Text);
        Assert.Equal(50, m.CenterX);      // 40 + 20/2
        Assert.Equal(45, m.CenterY);      // 40 + 10/2
        Assert.Equal(0.5, m.XPct, 5);
        Assert.Equal(0.45, m.YPct, 5);
        Assert.Equal(new[] { 40, 40, 20, 10 }, new[] { m.BoundsX, m.BoundsY, m.BoundsW, m.BoundsH });
    }

    [Fact]
    public async Task All_false_returns_only_the_best_match()
    {
        // Two candidates on DIFFERENT lines (so they don't merge into one phrase window).
        var finder = new TextFinder(new FakeEngine(
            new OcrWord("Submit", 0, 0, 10, 10, 0), new OcrWord("5ubmit", 0, 40, 10, 10, 1)));
        var one = await finder.FindAsync("Submit", System.Array.Empty<byte>(), MatchMode.Fuzzy, all: false,
            1.0, 0, 0, 0, 0, 100, 100);
        Assert.Single(one);
        Assert.Equal("Submit", one[0].Text); // exact beats fuzzy
    }
}
