using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Acquire the pixels for a screen rectangle. The scrape path's counterpart to
/// IWindowImageSource, and the seam that makes CaptureRectangle's WARNING-EMISSION logic
/// headless-testable -- without it the only way to reach that logic is to grab a real screen.
///
/// ⚠ Unlike IWindowImageSource this returns a NON-nullable bitmap: a scrape has no timeout to express,
/// so there is no "did not complete in time" case to signal. Failure throws, exactly as the direct call
/// does today.
///
/// The returned bitmap is the CALLER's to dispose.</summary>
public interface IScreenImageSource
{
    Bitmap Acquire(Rectangle absolute);
}
