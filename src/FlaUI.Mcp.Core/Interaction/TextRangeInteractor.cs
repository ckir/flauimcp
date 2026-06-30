using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions; // TextPatternRangeEndpoint, TextUnit (verified namespace)
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>UIA TextPattern caret/selection mutation for desktop_set_caret / desktop_select_text_range.
/// No OS input — pure UIA range ops; runs on the automation thread (caller's RunOnRefForInput callback).
/// Offsets are UIA TextUnit.Character (may diverge from raw UTF-16 for non-BMP text — accepted residual).</summary>
public static class TextRangeInteractor
{
    public static void SetCaret(AutomationElement el, int offset)
    {
        var range = RequirePattern(el).DocumentRange.Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, offset);
        // collapse END onto START -> a degenerate (caret) range
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        range.Select();
    }

    public static void SelectRange(AutomationElement el, int start, int length)
    {
        var range = RequirePattern(el).DocumentRange.Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, start);
        // Collapse End onto Start (zero-width at 'start'), then extend End forward by 'length' chars.
        // DocumentRange.Clone() places End at doc_end — a direct forward move from there is a no-op;
        // collapsing first is required to get a range of exactly 'length' characters.
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
        range.Select();
    }

    private static FlaUI.Core.Patterns.ITextPattern RequirePattern(AutomationElement el)
        => el.Patterns.Text.PatternOrDefault
           ?? throw new ToolException(ToolErrorCode.PatternUnsupported,
               "Element has no text provider (TextPattern).", "target an editable text element");
}
