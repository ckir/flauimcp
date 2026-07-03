using System;
using System.Text.Json.Serialization;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>The `verify` object folded into desktop_type's result (spec §5.4). Wire contract:
/// ran/verified/mismatch are ALWAYS present so a strict client never hits a missing-key error; the
/// string keys are conditional (JsonIgnore-when-null). `reason` is an OPEN string; consumers must
/// tolerate unknown tokens. `recommendedFallbackTool` is a STABLE machine key — either
/// desktop_set_value or desktop_paste_text, branched on the `canSetValue` wire fact — and
/// `remedy` is opaque human/LLM prose whose wording is NOT guaranteed stable across minor versions.</summary>
public sealed record VerifyResult
{
    public const int VerifyEchoMax = 256;

    private const string RemedyProse =
        "Text was not entered correctly — the target likely races synthetic keystrokes. " +
        "If canSetValue is true, use desktop_set_value (UIA ValuePattern) for byte-exact entry. " +
        "If canSetValue is false (e.g. an Electron contenteditable with no ValuePattern), use " +
        "desktop_paste_text with your ORIGINAL full text (do NOT use the truncated 'expected' echo) " +
        "— it pastes atomically; restoring the prior clipboard is best-effort (often " +
        "clipboardRestored:\"abandoned\" in the reactive editors this path targets). If canSetValue is absent/unknown, " +
        "try desktop_set_value first and fall back to desktop_paste_text on PatternUnsupported.";

    [JsonPropertyName("ran")] public bool Ran { get; init; }
    [JsonPropertyName("verified")] public bool Verified { get; init; }
    [JsonPropertyName("mismatch")] public bool Mismatch { get; init; }

    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
    [JsonPropertyName("expected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expected { get; init; }
    [JsonPropertyName("actual"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actual { get; init; }
    [JsonPropertyName("canSetValue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanSetValue { get; init; }
    [JsonPropertyName("recommendedFallbackTool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedFallbackTool { get; init; }
    [JsonPropertyName("remedy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Remedy { get; init; }

    /// <summary>verify=false: the caller opted out. ran:false, no assertion, reason "disabled".</summary>
    public static readonly VerifyResult Disabled =
        new() { Ran = false, Verified = false, Mismatch = false, Reason = "disabled" };

    /// <summary>Map a pure <see cref="VerifyOutcome"/> to the wire object, truncating the echoes.</summary>
    public static VerifyResult From(VerifyOutcome o, bool? canSetValue = null) => o.Status switch
    {
        VerifyStatus.Match =>
            new() { Ran = true, Verified = true, Mismatch = false },
        VerifyStatus.Mismatch =>
            new()
            {
                Ran = true,
                Verified = false,
                Mismatch = true,
                Expected = TypedTextVerifier.Truncate(o.Expected ?? string.Empty, VerifyEchoMax),
                Actual = TypedTextVerifier.Truncate(o.Actual ?? string.Empty, VerifyEchoMax),
                CanSetValue = canSetValue,
                // true/null -> set_value (null is the SAFE default: a wrong set_value guess yields a
                // recoverable PatternUnsupported; defaulting to clipboard would clobber the clipboard).
                RecommendedFallbackTool = canSetValue == false ? "desktop_paste_text" : "desktop_set_value",
                Remedy = RemedyProse,
            },
        VerifyStatus.Skipped =>
            new() { Ran = true, Verified = false, Mismatch = false, Reason = o.Reason },
        _ => throw new ArgumentOutOfRangeException(nameof(o), o.Status, "unknown VerifyStatus"),
    };
}
