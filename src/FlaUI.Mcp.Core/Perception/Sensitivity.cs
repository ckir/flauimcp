namespace FlaUI.Mcp.Core.Perception;

/// <summary>Why a value was withheld. None = not redacted.</summary>
public enum RedactionSource { None, Os, Rule }

/// <summary>The one redaction decision. Source/RuleName exist so a false positive is debuggable —
/// over-redaction is the dominant risk and an untraceable redaction is undebuggable.</summary>
public readonly record struct Sensitivity(bool Redact, RedactionSource Source, string? RuleName)
{
    public static readonly Sensitivity Visible = new(false, RedactionSource.None, null);
    public static readonly Sensitivity OsPassword = new(true, RedactionSource.Os, null);
}
