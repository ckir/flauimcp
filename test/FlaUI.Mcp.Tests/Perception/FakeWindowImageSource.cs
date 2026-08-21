using System;
using System.Drawing;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>A synthetic acquisition for headless tests. Every knob a test needs to stage one of the
/// seam's outcomes without a real window.</summary>
public sealed class FakeWindowImageSource : IWindowImageSource
{
    private readonly Func<Size, Bitmap?> _make;
    public int Calls { get; private set; }
    public Size LastRequestedSize { get; private set; }
    public IntPtr LastHwnd { get; private set; }

    public FakeWindowImageSource(Func<Size, Bitmap?> make) => _make = make;

    /// <summary>A bitmap of the requested size filled with one colour, with a distinguishing 4x4 marker
    /// at (1,1) so a test can prove the crop moved the origin rather than merely resizing.</summary>
    public static FakeWindowImageSource Solid(Color c) => new(size =>
    {
        var b = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using var g = Graphics.FromImage(b);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 0, 0, b.Width, b.Height);
        using var marker = new SolidBrush(Color.Magenta);
        g.FillRectangle(marker, 1, 1, 4, 4);
        return b;
    });

    /// <summary>Like <see cref="Solid"/>, but genuinely NON-UNIFORM to the detector.
    ///
    /// ⚠ THIS EXISTS BECAUSE `Solid` IS NOT. `Solid` paints a 4x4 magenta marker, and a comment
    /// elsewhere in this suite once read "Solid() paints a magenta marker, so use a genuinely flat source
    /// for this one" -- treating that marker as enough to make the bitmap varied. It is not.
    /// `UniformCanvasDetector` samples a 64x64 GRID, so on a 400x300 window its sample points step ~6px
    /// horizontally and ~5px vertically and NEVER land inside a 4x4 marker at (1,1). MEASURED: a `Solid`
    /// window emits `uniformCanvas`. The marker is there to prove the crop moved the origin, which is a
    /// different job and one it still does.
    ///
    /// The contrasting half-window block below is far larger than the grid step, so it is unmissable.</summary>
    public static FakeWindowImageSource Varied(Color c) => new(size =>
    {
        var b = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using var g = Graphics.FromImage(b);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 0, 0, b.Width, b.Height);
        using var contrast = new SolidBrush(c.GetBrightness() > 0.5f ? Color.Black : Color.White);
        g.FillRectangle(contrast, b.Width / 2, 0, b.Width - (b.Width / 2), b.Height);
        using var marker = new SolidBrush(Color.Magenta);
        g.FillRectangle(marker, 1, 1, 4, 4);
        return b;
    });

    /// <summary>Stages the timeout path.</summary>
    public static FakeWindowImageSource TimesOut() => new(_ => null);

    /// <summary>Stages the null-GDI-handle path.</summary>
    public static FakeWindowImageSource FailsWithCaptureUnavailable() => new(_ =>
        throw new FlaUI.Mcp.Core.Errors.ToolException(
            FlaUI.Mcp.Core.Errors.ToolErrorCode.CaptureUnavailable,
            "GDI resources are exhausted; the capture could not be allocated.",
            "close some windows and retry"));

    public Bitmap? Acquire(IntPtr hwnd, Size size, int timeoutMs)
    {
        Calls++;
        LastHwnd = hwnd;
        LastRequestedSize = size;
        return _make(size);
    }
}
