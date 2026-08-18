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

    public static VerifyRead FromElement(AutomationElement el, SensitivityClassifier classifier,
        string? processName, bool readCapability = false)
    {
        // The redaction decision short-circuits BEFORE the capability probe and before any text read —
        // ElementContent.Text never calls the thunk for a redacted element, so the ORDER of the two live
        // reads (capability, then text) is preserved exactly as it was under the old IsPassword branch.
        bool? canSet = null;
        bool readFailed = false;
        var read = ElementContent.Text(el, classifier, processName, () =>
        {
            canSet = readCapability ? ValueCapability.CanSetValue(el) : null; // reuse this live element
            try
            {
                var tp = el.Patterns.Text.PatternOrDefault;
                if (tp is null) { readFailed = true; return string.Empty; }
                return tp.DocumentRange.GetText(MaxReadChars);
            }
            catch { readFailed = true; return string.Empty; } // a failed read must never fail the type
        });
        if (read.Sensitivity.Redact) return new VerifyRead(null, true, null); // redacted -> never echo
        return readFailed ? new VerifyRead(null, false, canSet)
                          : new VerifyRead(read.Text, false, canSet);
    }
}
