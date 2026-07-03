using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>What the clipboard held before desktop_paste_text borrowed it. Text-only layer:
/// Text is fully restorable; TextWithRichFormats restores only its plain-text projection (formatting
/// lost); NonText (image/files, no text) is not restorable at all; Empty had nothing.</summary>
public enum PriorClipboardKind { Text, TextWithRichFormats, NonText, Empty }

/// <summary>Pure classification of a clipboard from the set of currently-present format ids. Separated
/// from the Win32 enumeration in <see cref="ClipboardAccess"/> so the decision is headless-testable.</summary>
public static class ClipboardClassifier
{
    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_UNICODETEXT = 13, CF_LOCALE = 16;

    // OS auto-synthesizes these from CF_UNICODETEXT; a clipboard holding ONLY these is pure text.
    private static readonly HashSet<uint> TextSynonyms = new() { CF_UNICODETEXT, CF_TEXT, CF_OEMTEXT, CF_LOCALE };

    public static PriorClipboardKind Classify(IReadOnlyCollection<uint> presentFormats)
    {
        if (presentFormats.Count == 0) return PriorClipboardKind.Empty;
        bool hasUnicode = false, hasNonSynonym = false;
        foreach (var f in presentFormats)
        {
            if (f == CF_UNICODETEXT) hasUnicode = true;
            if (!TextSynonyms.Contains(f)) hasNonSynonym = true;
        }
        if (hasUnicode) return hasNonSynonym ? PriorClipboardKind.TextWithRichFormats : PriorClipboardKind.Text;
        return PriorClipboardKind.NonText; // formats present, none is CF_UNICODETEXT
    }
}
