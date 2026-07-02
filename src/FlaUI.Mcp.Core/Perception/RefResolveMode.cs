namespace FlaUI.Mcp.Core.Perception;

/// <summary>Selects how a held ref is re-resolved from its descriptor.
/// Lenient (reads): descriptor re-walk within the ancestor scope(s), accumulate + dedup by RuntimeId,
/// surfacing AMBIGUOUS_MATCH on a duplicate and REF_STALE_UNRESOLVABLE when identity is lost (no
/// positional fallback). Strict (state-changing actions, INV-8): match ONLY the exact element whose
/// live UIA RuntimeId equals the descriptor's — never rebind by AutomationId/Name. Exactly one
/// RuntimeId match is required; 0 or a spoofed >1 yields REF_STALE/AMBIGUOUS.</summary>
public enum RefResolveMode
{
    Lenient,
    Strict
}
