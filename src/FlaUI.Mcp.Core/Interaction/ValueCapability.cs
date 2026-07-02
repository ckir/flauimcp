using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Can <c>desktop_set_value</c> write this element via UIA ValuePattern?
/// <c>true</c> = ValuePattern supported AND not read-only; <c>false</c> = no ValuePattern OR
/// read-only; <c>null</c> = couldn't determine (a UIA read threw). Advisory only — feeds the
/// desktop_type verify-mismatch remedy so a no-ValuePattern target (e.g. an Electron
/// contenteditable) is routed to the clipboard path instead of a dead-end set_value.</summary>
public static class ValueCapability
{
    public static bool? CanSetValue(AutomationElement el)
    {
        // WHOLE predicate in the try: `!...IsReadOnly` dereferences the pattern and makes its own
        // cross-process COM read that can throw independently of IsSupported. C# && short-circuits,
        // so `.Pattern` is never touched when IsSupported is false (no throw on unsupported).
        try { return el.Patterns.Value.IsSupported && !el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault; }
        catch { return null; }
    }
}
