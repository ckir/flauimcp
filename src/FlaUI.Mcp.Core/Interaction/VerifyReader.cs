using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Result of a defensive TextPattern read for desktop_type verification.
/// Redacted == true              -> password/sensitive field; the tool must NOT assert or echo.
/// Redacted == false, Text == null -> unreadable (no TextPattern / the read threw).
/// Redacted == false, Text != null -> the element's current text (may be "").
/// CanSetValue is populated only when FromElement is called with readCapability:true (the after-read);
/// it is the set_value writability fact (see ValueCapability).</summary>
public readonly record struct VerifyRead(string? Text, bool Redacted, bool? CanSetValue);

/// <summary>Thin UIA leaf: reads an element's text for verification, mirroring GetTextAsync's
/// password short-circuit but returning a null Text instead of THROWING on an unreadable element
/// (a failed read must never fail the type). Touches live UIA -> not headless-unit-tested; exercised
/// only in the documented live smoke (spec §6).</summary>
public static class VerifyReader
{
    private const int MaxReadChars = 200000; // mirror GetTextAsync's clamp ceiling; typed <= 4096 so never truncates the compare

    public static VerifyRead FromElement(AutomationElement el, bool readCapability = false)
    {
        bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
        if (isPwd) return new VerifyRead(null, true, null); // redacted short-circuits before the remedy branch
        bool? canSet = readCapability ? ValueCapability.CanSetValue(el) : null; // reuse this live element
        try
        {
            var tp = el.Patterns.Text.PatternOrDefault;
            if (tp is null) return new VerifyRead(null, false, canSet);
            return new VerifyRead(tp.DocumentRange.GetText(MaxReadChars), false, canSet);
        }
        catch { return new VerifyRead(null, false, canSet); }
    }
}
