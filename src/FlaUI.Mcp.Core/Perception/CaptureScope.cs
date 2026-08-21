namespace FlaUI.Mcp.Core.Perception;

/// <summary>Which caller a capture seam is serving. Neither seam can infer this from its other
/// arguments, and both must branch on it, so it crosses the boundary explicitly (spec §1, data flow).
///
/// ⚠ CaptureWindow must NOT infer window-vs-element from `E == W1`: an element that exactly covers its
/// window would take the resize branch meant for the other scope.
///
/// ⚠ OcrRegion is a REAL third caller, not a placeholder. FindTextTools.cs:62 and :110 back
/// desktop_find_text and desktop_wait_for_text, reach the mask walk through
/// ResolveTextCaptureGeometryAsync, and would otherwise inherit every default this feature adds. The
/// spec's risk 6 said CaptureRectangle had "one other caller" until this was measured.</summary>
public enum CaptureScope
{
    /// <summary>desktop_screenshot with a window handle and no ref. Acquires via PrintWindow.</summary>
    Window,
    /// <summary>desktop_screenshot with a window handle AND a ref. Acquires via PrintWindow, then crops.</summary>
    Element,
    /// <summary>desktop_screenshot with no window. Scrapes the virtual screen; the ONLY scope that runs
    /// the desktopCanvasUniform detector.</summary>
    FullDesktop,
    /// <summary>desktop_find_text / desktop_wait_for_text. Scrapes a window or a sub-region of one.
    /// Runs NO detector: it is neither full-desktop nor a PrintWindow capture, so it has no second
    /// operand for the two-stage comparison and no whole-desktop claim to make.</summary>
    OcrRegion,
}

public static class CaptureScopeExtensions
{
    /// <summary>TRUE only for FullDesktop. A fallback scrape and the OCR path both get NO detector:
    /// the two-stage comparison has no second operand on a scrape, and neither is a whole desktop.</summary>
    public static bool RunsDesktopUniformDetector(this CaptureScope scope) => scope == CaptureScope.FullDesktop;

    /// <summary>TRUE for the two scopes that acquire through PrintWindow. Note this describes the scope's
    /// INTENDED backend, not the backend a given attempt actually used -- a Window-scope capture that fell
    /// back to the scrape still answers true here, which is why the uniform detectors are additionally
    /// gated on the method that produced the image.</summary>
    public static bool AcquiresPerWindow(this CaptureScope scope)
        => scope is CaptureScope.Window or CaptureScope.Element;
}
