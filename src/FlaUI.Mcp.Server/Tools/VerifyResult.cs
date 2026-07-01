using System;
using System.Text.Json.Serialization;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>The `verify` object folded into desktop_type's result (spec §5.4). Wire contract:
/// ran/verified/mismatch are ALWAYS present so a strict client never hits a missing-key error; the
/// string keys are conditional (JsonIgnore-when-null). `reason` is an OPEN string; consumers must
/// tolerate unknown tokens. `recommendedFallbackTool` is a STABLE machine key; `remedy` is opaque
/// human/LLM prose whose wording is NOT guaranteed stable across minor versions.</summary>
public sealed record VerifyResult
{
    public const int VerifyEchoMax = 256;

    private const string RemedyProse =
        "Text was not entered correctly — the target may be a reactive/RichEdit editor that races synthetic keystrokes. Use desktop_set_value (UIA ValuePattern) for reliable text entry.";

    [JsonPropertyName("ran")] public bool Ran { get; init; }
    [JsonPropertyName("verified")] public bool Verified { get; init; }
    [JsonPropertyName("mismatch")] public bool Mismatch { get; init; }

    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
    [JsonPropertyName("expected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expected { get; init; }
    [JsonPropertyName("actual"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actual { get; init; }
    [JsonPropertyName("recommendedFallbackTool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedFallbackTool { get; init; }
    [JsonPropertyName("remedy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Remedy { get; init; }

    /// <summary>verify=false: the caller opted out. ran:false, no assertion, reason "disabled".</summary>
    public static readonly VerifyResult Disabled =
        new() { Ran = false, Verified = false, Mismatch = false, Reason = "disabled" };

    /// <summary>Map a pure <see cref="VerifyOutcome"/> to the wire object, truncating the echoes.</summary>
    public static VerifyResult From(VerifyOutcome o) => o.Status switch
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
                RecommendedFallbackTool = "desktop_set_value",
                Remedy = RemedyProse,
            },
        VerifyStatus.Skipped =>
            new() { Ran = true, Verified = false, Mismatch = false, Reason = o.Reason },
        _ => throw new ArgumentOutOfRangeException(nameof(o), o.Status, "unknown VerifyStatus"),
    };
}
