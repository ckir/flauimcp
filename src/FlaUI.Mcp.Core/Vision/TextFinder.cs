using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>One located text run with coordinates in BOTH physical screen px (bounds/center, pairs with
/// desktop_get_bounds) AND window fractions (xPct/yPct, pairs with desktop_click_at) — Phase 9 §6.</summary>
public readonly record struct TextFindMatch(
    string Text, double Confidence,
    int BoundsX, int BoundsY, int BoundsW, int BoundsH,   // physical screen px
    int CenterX, int CenterY, double XPct, double YPct);

/// <summary>Orchestrates OCR → match → coordinate mapping (Phase 9 §3/§6). Pure given an IOcrEngine and the
/// capture/window geometry: the caller (FindTextTools) does the actual screen capture off the STA and passes the
/// PNG + the CaptureResult scale/origin + the full window rect. Returns matches best-confidence first; all:false
/// keeps only the top match.</summary>
public sealed class TextFinder
{
    private readonly IOcrEngine _ocr;
    public TextFinder(IOcrEngine ocr) => _ocr = ocr;

    public async Task<IReadOnlyList<TextFindMatch>> FindAsync(
        string query, byte[] pngBytes, MatchMode mode, bool all,
        double scaleApplied, int captureX, int captureY,
        int winLeft, int winTop, int winWidth, int winHeight)
    {
        var words = await _ocr.RecognizeAsync(pngBytes);
        var hits = TextMatcher.Match(query, words, mode); // already ordered best-first; each carries a union rect
        var mapped = hits.Select(h =>
        {
            // h.X/Y/W/H is the matched run's UNION rect in bitmap px (may span multiple words for a phrase).
            var center = CoordinateMapping.BitmapRectCenterToWindowPct(
                h.X, h.Y, h.W, h.H, scaleApplied, captureX, captureY, winLeft, winTop, winWidth, winHeight);
            // Bounds top-left in screen px (undo scale + origin); size undoes just the scale.
            var tl = CoordinateMapping.BitmapToWindowPct(
                h.X, h.Y, scaleApplied, captureX, captureY, winLeft, winTop, winWidth, winHeight);
            int bw = (int)System.Math.Round(h.W / scaleApplied);
            int bh = (int)System.Math.Round(h.H / scaleApplied);
            return new TextFindMatch(h.Text, h.Confidence,
                tl.ScreenX, tl.ScreenY, bw, bh, center.ScreenX, center.ScreenY, center.XPct, center.YPct);
        });
        var list = mapped.ToList();
        return all ? list : list.Take(1).ToList();
    }
}
