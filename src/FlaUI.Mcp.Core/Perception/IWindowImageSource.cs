using System;
using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Acquire a bitmap of one window's own content. The ONLY part of this feature that touches
/// Win32, which is what keeps every other part headless-testable.</summary>
public interface IWindowImageSource
{
    /// <summary>Render the window into a new bitmap of exactly <paramref name="size"/>.
    ///
    /// Returns NULL when the call did not complete within <paramref name="timeoutMs"/>. The caller turns
    /// that into CaptureOutcome.TimedOut and falls back to the scrape.
    ///
    /// THROWS ToolException(CaptureUnavailable) when GDI hands back a null or zero handle. GDI does not
    /// throw -- CreateCompatibleDC, CreateCompatibleBitmap and SelectObject return null/zero on failure --
    /// so the scrape path's COMException/ExternalException catch never fires here and nothing else would
    /// take its place. Given ROADMAP item 13 (108 bare catches) the realistic outcome of skipping the
    /// null checks is an NRE surfacing as something unhelpful, or a garbage bitmap returned as a capture.
    ///
    /// The returned bitmap is the CALLER's to dispose.</summary>
    Bitmap? Acquire(IntPtr hwnd, Size size, int timeoutMs);
}
