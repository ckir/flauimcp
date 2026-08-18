using System;
using System.Collections.Generic;
using System.Linq;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>The ONE place a redaction decision is made. Immutable after construction and safe for
/// concurrent use — it is consulted from the query STA, the action STA and the watch pump's own thread.
///
/// HasRules exists for the DEFAULT PATH: with no rules configured, callers must not even construct
/// the Func thunks, because a closure over the element heap-allocates a delegate and a closure on
/// every egress call. Deferring a COM read while allocating two objects is not a zero-cost default.</summary>
public sealed class SensitivityClassifier
{
    private readonly RedactionRule[] _rules;

    private SensitivityClassifier(RedactionRule[] rules) => _rules = rules;

    public static SensitivityClassifier OsOnly { get; } = new(Array.Empty<RedactionRule>());

    public static SensitivityClassifier ForRules(IEnumerable<RedactionRule> rules) =>
        new(rules.ToArray());

    public bool HasRules => _rules.Length > 0;

    /// <summary>Could ANY configured rule apply to this process at all? Answers the process predicate
    /// ALONE — no identity is read and no element is touched.
    ///
    /// ⚠ Exists for callers that classify EAGERLY. <see cref="RedactionRule.Matches"/> short-circuits on
    /// the process check before it ever invokes an identity thunk, so a LAZY caller is naturally immune to
    /// a rule that cannot apply here: the thunk never runs, so a read that would have thrown never does.
    /// An eager caller has already done the read by then, and gating its fail-closed on
    /// <see cref="HasRules"/> would withhold content in a completely unrelated process merely because the
    /// operator configured a rule for some OTHER app. Over-redaction is the dominant risk this type exists
    /// to keep debuggable — so eager callers gate on this, never on HasRules.</summary>
    public bool CouldMatchProcess(string? processName) =>
        CannotEvaluateFor(processName) ||
        _rules.Any(r => r.Global ||
                        string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>TRUE when the process could not be identified AND at least one rule is process-scoped, so
    /// whether that rule applies is UNKNOWABLE rather than answerable.
    ///
    /// ⚠ CAPSTONE ROUND 5 (finding Q-j2). This is a genuine leak, not a nicety.
    /// <see cref="RedactionRule.Matches"/> asks `!Global &amp;&amp; !string.Equals(ProcessName, processName)`,
    /// and `string.Equals("SecretApp", null)` is FALSE — so a null process name made every process-scoped
    /// rule silently NOT match and the content went out in the clear. Unknown was being treated as "does
    /// not apply", which is the fail-OPEN reading of an unanswered question.
    ///
    /// Global-only rule sets are unaffected: a global rule applies regardless of process, so nothing is
    /// unknowable and no fail-closed is needed. That is why this asks for a process-SCOPED rule, not just
    /// for any rule — otherwise a machine-wide rule set would start withholding on every unnamed
    /// process for no security gain.</summary>
    public bool CannotEvaluateFor(string? processName) =>
        string.IsNullOrWhiteSpace(processName) && _rules.Any(r => !r.Global);

    public Sensitivity Classify(string? processName, Func<string?> automationId, Func<string?> rawName,
                                Func<bool> readIsPassword)
    {
        // OS first, always on, fail-closed on a throwing read.
        if (RedactionPolicy.IsPasswordOrFailClosed(readIsPassword)) return Sensitivity.OsPassword;

        foreach (var rule in _rules)
            if (rule.Matches(processName, automationId, rawName))
                return new Sensitivity(true, RedactionSource.Rule, rule.Name);

        return Sensitivity.Visible;
    }
}
