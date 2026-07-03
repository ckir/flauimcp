namespace FlaUI.Mcp.Core.Perception;

/// <summary>Capture geometry for OCR text-finding (Phase 9 §6 Task 10): the capture rect (whole window, or a
/// region sub-rect of it) PLUS the full window physical rect (needed separately so a region's fractions still
/// map against the WHOLE window, not just the cropped capture — CoordinateMapping's winLeft/winTop/winWidth/
/// winHeight parameters). Denied/Minimized mirror CaptureGeometry so the tool can convert them the same way
/// ScreenshotTools does (TargetDenied / ElementNotActionable).</summary>
public sealed record TextCaptureGeometry(
    bool Denied, string? DeniedProcess, bool Minimized,
    System.Drawing.Rectangle CaptureBounds, System.Collections.Generic.IReadOnlyList<System.Drawing.Rectangle> PasswordRects,
    int WindowLeft, int WindowTop, int WindowWidth, int WindowHeight);
