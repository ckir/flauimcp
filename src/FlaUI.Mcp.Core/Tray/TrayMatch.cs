// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Core.Tray;

public enum MatchKind { None, Unique, Ambiguous }

/// <summary>Outcome of matching a caller's text against a list. Index is valid only for Unique;
/// Candidates lists every hit (empty for None, one for Unique, all for Ambiguous).</summary>
public readonly record struct MatchResult(MatchKind Kind, int Index, IReadOnlyList<int> Candidates);

/// <summary>Pure matching rules (SPEC §1.3, §2 step 7). Callers validate that the query is not
/// blank BEFORE calling (an empty substring would match everything).</summary>
public static class TrayMatch
{
    /// <summary>`icon` rule: case-insensitive substring over the tooltips.</summary>
    public static MatchResult IconBySubstring(IReadOnlyList<string> tooltips, string query)
        => FromHits(Hits(tooltips, t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));

    /// <summary>`menuItem` rule: exact (case-insensitive) equality first; only when nothing is equal,
    /// case-insensitive substring.</summary>
    public static MatchResult ItemByText(IReadOnlyList<string> cleanedTexts, string query)
    {
        var exact = Hits(cleanedTexts, t => string.Equals(t, query, StringComparison.OrdinalIgnoreCase));
        if (exact.Count > 0) return FromHits(exact);
        return FromHits(Hits(cleanedTexts, t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<int> Hits(IReadOnlyList<string> items, Func<string, bool> pred)
    {
        var hits = new List<int>();
        for (int i = 0; i < items.Count; i++) if (pred(items[i] ?? string.Empty)) hits.Add(i);
        return hits;
    }

    private static MatchResult FromHits(List<int> hits) => hits.Count switch
    {
        0 => new MatchResult(MatchKind.None, -1, hits),
        1 => new MatchResult(MatchKind.Unique, hits[0], hits),
        _ => new MatchResult(MatchKind.Ambiguous, -1, hits),
    };
}
