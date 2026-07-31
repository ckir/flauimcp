using System;
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>THE closed set of egress accessors (spec §7.2). Outside this type and the pinned identity
/// readers, src/ may not read a content-bearing UIA property at all — a later task's Roslyn sweep
/// enforces it.
///
/// Every accessor returns the ALREADY-REDACTED string. The easy path (call it, return what it gave
/// you) is the SAFE path; that is the property earlier designs lacked. A type carrying {raw, Sensitivity}
/// would prove the classification was COMPUTED, not APPLIED — a developer who forgets writes
/// `return result.RawName` and ships the leak under a green gate.</summary>
public static class ElementContent
{
    public const string RedactedToken = "[REDACTED]";

    public readonly record struct Read(string Text, Sensitivity Sensitivity)
    {
        /// <summary>The RAW value, for identity paths only (BC-1: the stored descriptor keeps the raw
        /// name, because RefRegistry falls back to Name+ControlType as ref identity when AutomationId is
        /// absent). Its only legitimate callers are the descriptor / ref-resolution sites, which are few,
        /// pinned and reviewed. Building a wire payload from this is visibly wrong at the call site.</summary>
        public string RawForIdentity { get; init; } = string.Empty;
    }

    public static Read Name(AutomationElement el, SensitivityClassifier classifier, string? processName)
    {
        string raw = Safe(() => el.Name);
        var s = Classify(el, classifier, processName,
                         () => Safe(() => el.Properties.AutomationId.ValueOrDefault), () => raw);
        return new Read(s.Redact ? RedactedToken : raw, s) { RawForIdentity = raw };
    }

    public static Read Value(AutomationElement el, SensitivityClassifier classifier, string? processName)
    {
        var s = Classify(el, classifier, processName,
                         () => Safe(() => el.Properties.AutomationId.ValueOrDefault), () => Safe(() => el.Name));
        if (s.Redact) return new Read(RedactedToken, s) { RawForIdentity = string.Empty };
        string raw = Safe(() => el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? string.Empty);
        if (raw.Length == 0) raw = Safe(() => el.Name);
        return new Read(raw, s) { RawForIdentity = raw };
    }

    /// <summary>⚠ <paramref name="readText"/> is called DIRECTLY — deliberately NOT wrapped in Safe.
    /// The text-read sites throw meaningful, tested exceptions from inside that read
    /// (ToolException(PatternUnsupported) when the element has no TextPattern; UnauthorizedAccessException
    /// on an elevated window, handled by an outer catch). Swallowing them here would turn two documented
    /// errors into a silent empty string. The thunk owns its own failure policy.
    ///
    /// The thunk is NOT called at all when the value is redacted — a secret's text is never requested
    /// from the provider.</summary>
    public static Read Text(AutomationElement el, SensitivityClassifier classifier, string? processName,
                            Func<string> readText)
    {
        var s = Classify(el, classifier, processName,
                         () => Safe(() => el.Properties.AutomationId.ValueOrDefault), () => Safe(() => el.Name));
        if (s.Redact) return new Read(RedactedToken, s) { RawForIdentity = string.Empty };
        string raw = readText() ?? string.Empty;
        return new Read(raw, s) { RawForIdentity = raw };
    }

    // DEFAULT PATH: with no rules we never construct the thunks — a closure over `el` heap-allocates a
    // delegate AND a closure on every egress call, before Classify even runs (spec §4.4).
    private static Sensitivity Classify(AutomationElement el, SensitivityClassifier classifier,
                                        string? processName, Func<string?> aid, Func<string?> name)
    {
        if (!classifier.HasRules)
            return RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault)
                ? Sensitivity.OsPassword : Sensitivity.Visible;
        return classifier.Classify(processName, aid, name, () => el.Properties.IsPassword.ValueOrDefault);
    }

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? string.Empty; } catch { return string.Empty; }
    }
}
