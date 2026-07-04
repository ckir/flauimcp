using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Shared targeting gate for the ref-or-selector tools (Phase 10 #2). ONE copy of the
/// exactly-one-of security gate + the selector param description, so the two tool classes can't drift.</summary>
internal static class SelectorGating
{
    /// <summary>Reject unless EXACTLY ONE of {ref, selector} is supplied (both or neither → InvalidArguments),
    /// before any UIA/resolution. desktop_key uses its own at-most-one variant (neither = foreground).</summary>
    public static void RequireExactlyOne(string? @ref, Selector? selector)
    {
        bool hasRef = !string.IsNullOrEmpty(@ref), hasSel = selector is not null;
        if (hasRef == hasSel)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "provide exactly one of ref or selector.",
                hasRef ? "drop one — ref and selector are mutually exclusive" : "pass a ref (from a snapshot) or a selector {automationId|name|controlType}");
    }

    /// <summary>MCP param description for the shared `selector` param. Note resolvedElement is returned
    /// ONLY on the selector path (a review nit: the old text implied it was always present).</summary>
    public const string SelectorDesc = "Stable target {automationId?,name?,nameMatch?,controlType?,scope?,ignoreCase?} resolved at action time. Exactly one of ref | selector. Returns resolvedElement when selector is used.";
}
