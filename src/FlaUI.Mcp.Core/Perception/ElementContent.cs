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

        /// <summary>True when the underlying UIA read returned NULL or threw — i.e. the value is ABSENT,
        /// not empty. Set only by <see cref="Name"/>. It exists because the watch event payload has always
        /// emitted a null name for an unnamed source, and null-vs-"" on the wire is a contract.</summary>
        public bool Absent { get; init; }
    }

    public static Read Name(AutomationElement el, SensitivityClassifier classifier, string? processName)
    {
        string raw = Safe(() => el.Name, out bool absent);
        var s = Classify(el, classifier, processName, raw); // Name already has it — don't re-read it
        return new Read(s.Redact ? RedactedToken : raw, s) { RawForIdentity = raw, Absent = absent };
    }

    public static Read Value(AutomationElement el, SensitivityClassifier classifier, string? processName)
    {
        var s = Classify(el, classifier, processName);
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
        var s = Classify(el, classifier, processName);
        if (s.Redact) return new Read(RedactedToken, s) { RawForIdentity = string.Empty };
        string raw = readText() ?? string.Empty;
        return new Read(raw, s) { RawForIdentity = raw };
    }

    /// <summary>DEFAULT PATH (spec §4.4): the rule thunks are constructed only INSIDE the HasRules branch.
    ///
    /// ⚠ They must NOT be parameters. C# evaluates arguments at the CALL SITE, so passing them in would
    /// heap-allocate two delegates plus their closures on every egress read, BEFORE this method's
    /// HasRules guard could skip them — the guard would sit after the allocation it exists to avoid.
    /// That is exactly what this code did until an AGY-CAPSTONE round caught it.
    ///
    /// One closure remains on the default path — RedactionPolicy.IsPasswordOrFailClosed takes a Func —
    /// and that is not a regression: every site this type replaced allocated the same one.
    ///
    /// <paramref name="alreadyReadName"/> lets a caller that has ALREADY read the name hand it over
    /// rather than have a rule read it a second time. Null means "read it lazily, only if a rule asks".</summary>
    private static Sensitivity Classify(AutomationElement el, SensitivityClassifier classifier,
                                        string? processName, string? alreadyReadName = null)
    {
        if (!classifier.HasRules)
            return RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault)
                ? Sensitivity.OsPassword : Sensitivity.Visible;
        return classifier.Classify(processName,
            () => Safe(() => el.Properties.AutomationId.ValueOrDefault),
            () => alreadyReadName ?? Safe(() => el.Name),
            () => el.Properties.IsPassword.ValueOrDefault);
    }

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? string.Empty; } catch { return string.Empty; }
    }

    // As Safe, but reports whether the value was ABSENT (null return or a throw) rather than empty.
    private static string Safe(Func<string?> read, out bool absent)
    {
        try { var v = read(); absent = v is null; return v ?? string.Empty; }
        catch { absent = true; return string.Empty; }
    }
}
