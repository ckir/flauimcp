// src/FlaUI.Mcp.Core/Perception/FindQuery.cs
using System;
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Flattened desktop_find query (validated/carried from the tool boundary).
/// NameMatch is "eq" (ordinal exact) or "contains" (ordinal substring); ignored when Name is null.</summary>
public sealed record FindQuery(
    string? AutomationId,
    string? Name,
    string NameMatch,
    string? ControlType,
    bool EnabledOnly);

/// <summary>One find hit. Bounds is a physical-pixel rect [x, y, w, h]. Name is "[REDACTED]"
/// for an IsPassword element (INV-5). Absent UIA strings are empty, never null, on the wire.</summary>
public sealed record FindMatch(
    string Ref,
    string AutomationId,
    string Name,
    string ControlType,
    int[] Bounds,
    bool IsOffscreen,
    bool IsEnabled,
    bool HasFocus);

/// <summary>find result: matches capped at max, in tree order; TotalMatches is the full count
/// (before the cap) so a truncated result is never silently misleading (spec 3.1).</summary>
public sealed record FindResult(
    System.Collections.Generic.IReadOnlyList<FindMatch> Matches,
    int TotalMatches,
    bool IsTruncated);

/// <summary>Pure query logic: the parts of desktop_find that need no live UIA element.
/// TryParseControlType covers the native-condition control-type constraint; MatchesPostFilter
/// covers the constraints UIA cannot express as an indexed property condition (name "contains",
/// enabledOnly).</summary>
public sealed class FindQuerySpec
{
    private readonly FindQuery _q;
    public FindQuerySpec(FindQuery q) => _q = q;

    /// <summary>Parse a UIA ControlType by name (case-insensitive). Returns false for null/blank
    /// (no constraint) or an unknown name (caller raises InvalidArguments for the unknown case -
    /// it distinguishes null from a bad string itself).</summary>
    public static bool TryParseControlType(string? controlType, out ControlType ct)
    {
        ct = default;
        return !string.IsNullOrWhiteSpace(controlType)
            && Enum.TryParse(controlType, ignoreCase: true, out ct);
    }

    /// <summary>Post-filter predicate applied AFTER the native UIA condition narrows the set:
    /// name "contains" (UIA ByName is exact-only) and enabledOnly. Name-"eq" is handled natively,
    /// but re-checking it here is harmless and keeps the predicate total. Callers pass the element's
    /// ALREADY-REDACTED name (a password field's name is "[REDACTED]"), so a password element can
    /// never satisfy a name constraint (INV-5 - no name-oracle). The element name may be NULL (UIA
    /// returns null for unnamed containers - Panes/Groups); a null name is treated as empty so no
    /// matcher throws (it simply can't satisfy a non-empty name constraint).</summary>
    public bool MatchesPostFilter(string? name, bool enabled)
    {
        if (_q.EnabledOnly && !enabled) return false;
        if (_q.Name is { } wanted)
        {
            var n = name ?? string.Empty; // null == unnamed container; keep the predicate total
            bool ok = string.Equals(_q.NameMatch, "contains", StringComparison.Ordinal)
                ? n.Contains(wanted, StringComparison.Ordinal)
                : string.Equals(n, wanted, StringComparison.Ordinal);
            if (!ok) return false;
        }
        return true;
    }
}
