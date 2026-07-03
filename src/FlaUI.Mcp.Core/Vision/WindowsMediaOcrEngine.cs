using System.Collections.Generic;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>Windows.Media.Ocr implementation of IOcrEngine (§5). On-box, free, ships with Windows (language packs
/// via the OS). Decodes the PNG to a SoftwareBitmap and recognizes; returns words in bitmap px. Confirmed by
/// Spike α (docs/superpowers/spikes/2026-07-03-spikeA-winrt-ocr.md): built-in CsWinRT projection on
/// net10.0-windows10.0.19041.0 (no package); Word.BoundingRect is top-left bitmap px.</summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            throw new ToolException(ToolErrorCode.OcrUnavailable,
                "No OCR language pack is installed for the current user profile.",
                "install a Windows OCR language pack (Settings > Time & Language > Language), then retry");

        // Load the PNG bytes into a WinRT stream via DataWriter (Spike α proved this path; avoids the AsBuffer
        // extension dependency).
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await engine.RecognizeAsync(bitmap);
        var words = new List<OcrWord>();
        int lineId = 0;
        foreach (var line in result.Lines)   // one LineId per OCR text line -> TextMatcher won't join across lines
        {
            foreach (var w in line.Words)
                words.Add(new OcrWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height, lineId));
            lineId++;
        }
        return words;
    }
}
