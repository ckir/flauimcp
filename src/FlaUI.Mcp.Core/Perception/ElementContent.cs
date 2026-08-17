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
        var (s, _) = Classify(el, classifier, processName, raw); // Name already has it — don't re-read it
        return new Read(s.Redact ? RedactedToken : raw, s) { RawForIdentity = raw, Absent = absent };
    }

    public static Read Value(AutomationElement el, SensitivityClassifier classifier, string? processName)
    {
        var (s, readName) = Classify(el, classifier, processName);
        if (s.Redact) return new Read(RedactedToken, s) { RawForIdentity = string.Empty };
        string raw = Safe(() => el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? string.Empty);
        // Reuse the name a RULE already read rather than reading it a second time. Two reads of a live
        // element are a redaction BYPASS, not just waste: if the name mutates in between, the rule matches
        // the OLD value and this fallback puts the NEW one on the wire unredacted. Still lazy — with no
        // rules readName is null and this reads exactly once, as it always did.
        if (raw.Length == 0) raw = readName ?? Safe(() => el.Name);
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
        var (s, _) = Classify(el, classifier, processName);
        if (s.Redact) return new Read(RedactedToken, s) { RawForIdentity = string.Empty };
        string raw = readText() ?? string.Empty;
        return new Read(raw, s) { RawForIdentity = raw };
    }

    /// <summary>DEF-1: the redaction DECISION for an element whose CONTENT is never read — the pixel path.
    /// Screenshot masking needs to know whether to paint a rect black; it must never read the value it is
    /// hiding. Routing it through the same <see cref="Classify"/> as every text site is the point: the pixel
    /// path used to read <c>IsPassword</c> RAW inside a swallowing try/catch, so a provider that THREW got
    /// its text redacted and its pixels CAPTURED. Classify fails CLOSED, so a throw now masks.</summary>
    public static Sensitivity SensitivityOf(AutomationElement el, SensitivityClassifier classifier,
                                            string? processName)
        => Classify(el, classifier, processName).Sensitivity;

    /// <summary>Wire provenance for the `redactedBy` field: "os" for the OS IsPassword flag,
    /// "rule:&lt;name&gt;" for an operator rule, "unreadable" when rules are configured but the element's
    /// identity could not be read (fail-closed, no rule was evaluated), null when nothing was withheld.
    /// Kept separate from `isPassword`, which keeps its literal OS meaning for existing consumers.
    ///
    /// ⚠ "unreadable" is deliberately NOT spelled "rule:something" — inventing a rule name the operator
    /// never wrote would send them hunting for a rule that does not exist.</summary>
    public static string? RedactedBy(Sensitivity s) => !s.Redact ? null
        : s.Source switch
        {
            RedactionSource.Os => "os",
            RedactionSource.Unreadable => "unreadable",
            _ => $"rule:{s.RuleName}"
        };

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
    /// rather than have a rule read it a second time. Null means "read it lazily, only if a rule asks".
    ///
    /// ⚠ The thunks MEMOIZE. RedactionRule.Matches calls the automationId thunk up to TWICE and the name
    /// thunk once — PER RULE — and MaxRules is 64, so an unmemoized thunk costs up to ~192 COM reads on a
    /// single element. Worse than the cost: re-reading a LIVE element means rule 1 and rule 40 can see
    /// different values, and an element that mutates mid-classification can slip past every rule that
    /// would have matched it. One read per property per classification is a correctness property.
    ///
    /// Returns the name it ended up reading (null if nothing asked for it) so a caller that also needs
    /// the name can reuse it instead of opening the same two-read window again.</summary>
    private static (Sensitivity Sensitivity, string? ReadName) Classify(
        AutomationElement el, SensitivityClassifier classifier,
        string? processName, string? alreadyReadName = null)
    {
        if (!classifier.HasRules)
            return (RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault)
                ? Sensitivity.OsPassword : Sensitivity.Visible, alreadyReadName);

        // ⚠ SP3 CAPSTONE FIX (finding L2) — the two signals used to disagree about failure.
        // The IsPassword thunk below is passed UNWRAPPED on purpose: the classifier runs it through
        // RedactionPolicy.IsPasswordOrFailClosed, so a THROWING password read redacts. The two identity
        // thunks went through Safe(), which swallows a throw and yields "" — and "" matches no rule, so a
        // throwing AutomationId/Name read meant NO RULE APPLIED and the content was emitted in the clear.
        // OS passwords failed closed while operator rules failed OPEN, at the same instant, on the same
        // element. Nothing failed to compile and no test noticed.
        //
        // `identityUnreadable` is set only by a thunk that was ACTUALLY INVOKED, which matters: the thunks
        // are lazy, so a rule set that never consults AutomationId cannot be failed closed by a read that
        // never happened. An EMPTY identity is NOT a failure — most elements legitimately have no
        // AutomationId — so only a genuine throw trips this.
        bool identityUnreadable = false;
        string SafeIdentity(Func<string?> read)
        {
            try { return read() ?? string.Empty; }
            catch { identityUnreadable = true; return string.Empty; }
        }

        string? memoAid = null, memoName = alreadyReadName;
        var s = classifier.Classify(processName,
            () => memoAid ??= SafeIdentity(() => el.Properties.AutomationId.ValueOrDefault),
            () => memoName ??= SafeIdentity(() => el.Name),
            () => el.Properties.IsPassword.ValueOrDefault);

        // Only when no rule matched: a rule that DID match already redacts, and its name is better
        // provenance than "unreadable".
        if (!s.Redact && identityUnreadable) return (Sensitivity.UnreadableIdentity, memoName);
        return (s, memoName);
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
