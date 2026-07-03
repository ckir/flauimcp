using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>On-box OCR seam (Phase 9 §5). Input is a PNG byte[] (a ScreenCapture.CaptureRectangle result); output
/// is the recognized words with rects in BITMAP pixels (the same downscaled space CoordinateMapping expects). One
/// engine for v1 (Windows.Media.Ocr); the seam exists so TextFinder is headless-testable with a fake and so a 2nd
/// engine can be added later without touching callers. Throws ToolException(OcrUnavailable) if no OCR is available
/// (no language pack) — never crashes.</summary>
public interface IOcrEngine
{
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes);
}
