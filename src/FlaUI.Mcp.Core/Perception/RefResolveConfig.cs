namespace FlaUI.Mcp.Core.Perception;

/// <summary>Operator configuration for ref resolution, read from the environment at process start.
/// FLAUI_MCP_REF_STRICT=off is a BREAK-GLASS switch that forces Lenient resolution on state-changing
/// paths too (deliberately DISABLES INV-8) — for emergencies on apps whose UIA identity is too
/// volatile for strict. FLAUI_MCP_REF_MAXSCOPES tunes the ancestor fan-out cap (default 512). Both
/// are pure functions of their input string so they can be unit-tested without touching the
/// environment.</summary>
public static class RefResolveConfig
{
    public const int DefaultMaxScopes = 512;

    /// <summary>Strict unless the operator explicitly set "off" (case-insensitive, trimmed).</summary>
    public static bool StrictEnabled(string? env) =>
        !string.Equals(env?.Trim(), "off", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>The resolution mode state-changing paths use, given the FLAUI_MCP_REF_STRICT value.
    /// This is the single testable seam for the kill-switch wiring (QA: the off->Lenient path must be
    /// covered, not just the boolean parse).</summary>
    public static RefResolveMode WriteMode(string? env) =>
        StrictEnabled(env) ? RefResolveMode.Strict : RefResolveMode.Lenient;

    /// <summary>Positive integer override, else the default.</summary>
    public static int MaxScopes(string? env) =>
        int.TryParse(env, out var n) && n > 0 ? n : DefaultMaxScopes;
}
