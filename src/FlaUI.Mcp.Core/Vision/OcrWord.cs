namespace FlaUI.Mcp.Core.Vision;

/// <summary>One recognized word from the OCR engine. Rect is in BITMAP pixels (the downscaled capture space);
/// CoordinateMapping converts it to screen/window coords. LineId groups words that share an OCR text line, so
/// phrase matching (TextMatcher) never joins words across unrelated lines.</summary>
public readonly record struct OcrWord(string Text, double X, double Y, double W, double H, int LineId);
