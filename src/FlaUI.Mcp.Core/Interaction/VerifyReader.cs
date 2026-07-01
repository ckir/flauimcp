using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Result of a defensive TextPattern read for desktop_type verification.
/// Redacted == true              -> password/sensitive field; the tool must NOT assert or echo.
/// Redacted == false, Text == null -> unreadable (no TextPattern / the read threw).
/// Redacted == false, Text != null -> the element's current text (may be "").</summary>
public readonly record struct VerifyRead(string? Text, bool Redacted);

/// <summary>Thin UIA leaf: reads an element's text for verification, mirroring GetTextAsync's
/// password short-circuit but returning a null Text instead of THROWING on an unreadable element
/// (a failed read must never fail the type). Touches live UIA -> not headless-unit-tested; exercised
/// only in the documented live smoke (spec §6).</summary>
public static class VerifyReader
{
    private const int MaxReadChars = 200000; // mirror GetTextAsync's clamp ceiling; typed <= 4096 so never truncates the compare

    public static VerifyRead FromElement(AutomationElement el)
    {
        bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
        if (isPwd) return new VerifyRead(null, true);
        try
        {
            var tp = el.Patterns.Text.PatternOrDefault;
            if (tp is null) return new VerifyRead(null, false);
            return new VerifyRead(tp.DocumentRange.GetText(MaxReadChars), false);
        }
        catch { return new VerifyRead(null, false); }
    }
}
