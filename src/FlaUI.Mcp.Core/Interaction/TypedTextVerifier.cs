using System;

namespace FlaUI.Mcp.Core.Interaction;

public enum VerifyStatus { Match, Mismatch, Skipped }

/// <summary>Outcome of a desktop_type read-back check.
/// Reason is a skip-token on Skipped (null otherwise); Expected/Actual are the normalized
/// typed/read-back strings on Mismatch only (null otherwise).</summary>
public readonly record struct VerifyOutcome(
    VerifyStatus Status,
    string? Reason,
    string? Expected,
    string? Actual);

/// <summary>Pure (no UIA / no SendInput / no sleep) verifier for desktop_type read-back. Mirrors
/// <see cref="UnicodeKeyTyper"/>: the correctness logic is headless-testable on the RDP-only box
/// where synthetic input cannot run. The tool layer feeds it the raw before/after TextPattern reads.</summary>
public static class TypedTextVerifier
{
    /// <summary>Decide Match/Mismatch/Skipped for a read-back.
    /// before == null -> couldn't read (no TextPattern / read failed) -> Skipped("no-textpattern").
    /// before != ""   -> field had content (checked RAW, pre-normalize) -> Skipped("field-not-empty");
    ///                   a lone "\n" is deliberately NOT empty (caret ambiguous).
    /// else compare Normalize(after) vs Normalize(typed), Ordinal -> Match | Mismatch.</summary>
    public static VerifyOutcome Check(string? before, string? after, string typed)
    {
        if (before is null)
            return new VerifyOutcome(VerifyStatus.Skipped, "no-textpattern", null, null);
        if (before.Length != 0)
            return new VerifyOutcome(VerifyStatus.Skipped, "field-not-empty", null, null);

        string exp = Normalize(typed);
        string act = Normalize(after);
        return string.Equals(act, exp, StringComparison.Ordinal)
            ? new VerifyOutcome(VerifyStatus.Match, null, null, null)
            : new VerifyOutcome(VerifyStatus.Mismatch, null, exp, act);
    }

    /// <summary>CRLF and lone-CR -> LF, then strip ALL trailing '\n' (not just one). No other
    /// trimming: leading/interior/trailing NON-newline whitespace is significant. Stripping every
    /// trailing newline avoids a false mismatch when the caller types a trailing '\n' and the editor
    /// also appends its own structural newline. Symmetric — applied to both typed and read-back.</summary>
    internal static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        string lf = s.Replace("\r\n", "\n").Replace('\r', '\n');
        int end = lf.Length;
        while (end > 0 && lf[end - 1] == '\n') end--;
        return lf.Substring(0, end);
    }

    /// <summary>Cap an echoed string so the RESULT length is at most <paramref name="max"/> INCLUDING
    /// the ellipsis: when the source exceeds max, emit the first (max-1) chars + '…' (never max+1).
    /// Bounds context-window blowout on the echoed expected/actual. Public because the Server-layer
    /// mapper (a different assembly) calls it.</summary>
    public static string Truncate(string s, int max)
    {
        if (max <= 0) return string.Empty;
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }
}
