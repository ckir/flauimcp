using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureScopeTests
{
    // The scrape seam runs the desktopCanvasUniform detector for FullDesktop and for NOTHING else.
    // OcrRegion and a fallback scrape must both come back false, and they are different callers, so
    // this is pinned per-value rather than as "not FullDesktop".
    [Theory]
    [InlineData(CaptureScope.FullDesktop, true)]
    [InlineData(CaptureScope.Window, false)]
    [InlineData(CaptureScope.Element, false)]
    [InlineData(CaptureScope.OcrRegion, false)]
    public void Only_full_desktop_runs_the_desktop_detector(CaptureScope scope, bool expected)
        => Assert.Equal(expected, scope.RunsDesktopUniformDetector());

    // The two-stage uniform detector needs a full-window bitmap distinct from the crop, which only the
    // printWindow backend produces. Every scrape path -- including a fallback -- must be false.
    [Theory]
    [InlineData(CaptureScope.Window, true)]
    [InlineData(CaptureScope.Element, true)]
    [InlineData(CaptureScope.FullDesktop, false)]
    [InlineData(CaptureScope.OcrRegion, false)]
    public void Only_window_and_element_acquire_per_window(CaptureScope scope, bool expected)
        => Assert.Equal(expected, scope.AcquiresPerWindow());

    [Fact]
    public void OcrRegion_exists_because_FindTextTools_is_a_third_caller()
        => Assert.Equal(4, System.Enum.GetValues<CaptureScope>().Length);
}
