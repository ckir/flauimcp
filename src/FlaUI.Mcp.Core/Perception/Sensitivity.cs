namespace FlaUI.Mcp.Core.Perception;

/// <summary>Why a value was withheld. None = not redacted.
///
/// ⚠ <c>Unreadable</c> is NOT an operator rule firing — it is the fail-CLOSED outcome when rules are
/// configured but the element's IDENTITY could not be read, so no rule could be evaluated honestly.
/// It gets its own member rather than borrowing Rule (which would put a rule name on the wire that the
/// operator never wrote) or Os (which would claim a password flag that was never set). This type exists
/// so a false positive is debuggable; a redaction that lies about its own cause defeats that.</summary>
public enum RedactionSource { None, Os, Rule, Unreadable }

/// <summary>The one redaction decision. Source/RuleName exist so a false positive is debuggable —
/// over-redaction is the dominant risk and an untraceable redaction is undebuggable.</summary>
public readonly record struct Sensitivity(bool Redact, RedactionSource Source, string? RuleName)
{
    public static readonly Sensitivity Visible = new(false, RedactionSource.None, null);
    public static readonly Sensitivity OsPassword = new(true, RedactionSource.Os, null);

    /// <summary>Rules are configured but the element's identity could not be read, so no rule could be
    /// evaluated. Withhold rather than emit — see <see cref="RedactionSource.Unreadable"/>.</summary>
    public static readonly Sensitivity UnreadableIdentity = new(true, RedactionSource.Unreadable, null);
}
