using System;
using System.Collections.Generic;
using System.Linq;

namespace FlaUI.Mcp.Core.Vision;

public enum MatchMode { Fuzzy, Exact }

/// <summary>One matched text run: the joined matched text, its UNION bitmap rect (spanning the matched words), and
/// a [0,1] confidence (1.0 = exact normalized match; lower = fuzzier).</summary>
public readonly record struct TextMatch(string Text, double X, double Y, double W, double H, double Confidence);

/// <summary>Pure matching of a query against OCR words (Phase 9 §3). Matches a query against variable-width sliding
/// windows of consecutive words on the SAME line (LineId), so single words, multi-word phrases ("Submit Order"),
/// AND OCR-over-splits ("Subm it") all resolve. FUZZY (default) normalizes case + whitespace and allows a bounded
/// edit-distance tolerance (OCR mis-reads real UI text); EXACT matches only the normalized string. Returns ALL
/// matches, best-confidence first, so the agent picks the right occurrence (§3 Seat C).</summary>
public static class TextMatcher
{
    public static IEnumerable<TextMatch> Match(string query, IReadOnlyList<OcrWord> words, MatchMode mode)
    {
        var q = Normalize(query);
        if (q.Length == 0) return Enumerable.Empty<TextMatch>();
        int queryTokens = q.Count(c => c == ' ') + 1;
        int maxWin = queryTokens + 1; // allow ONE extra split (OCR broke a query token into two)

        var results = new List<TextMatch>();
        // Group by line, preserving input order (input order == reading order within a line — Task 9 emits words
        // in Lines->Words order; if an engine ever returns them unordered, sort each line's words by X first).
        foreach (var line in words.GroupBy(w => w.LineId))
        {
            var lw = line.OrderBy(w => w.X).ToList();
            for (int start = 0; start < lw.Count; start++)
            {
                // For this start word, try windows of 1..maxWin adjacent words; keep the BEST-confidence window
                // (one match per start index -> no duplicate explosion).
                double bestConf = 0.0;
                int bestWin = 0;
                string bestJoined = "";
                int maxThisStart = Math.Min(maxWin, lw.Count - start);
                for (int win = 1; win <= maxThisStart; win++)
                {
                    string joined = string.Join(" ", lw.GetRange(start, win).Select(w => w.Text));
                    var n = Normalize(joined);
                    if (n.Length == 0) continue;
                    double conf = mode == MatchMode.Exact ? (n == q ? 1.0 : 0.0) : FuzzyConfidence(q, n);
                    if (conf > bestConf) { bestConf = conf; bestWin = win; bestJoined = joined; }
                }
                if (bestConf > 0.0)
                {
                    var rect = Union(lw.GetRange(start, bestWin));
                    results.Add(new TextMatch(bestJoined, rect.X, rect.Y, rect.W, rect.H, bestConf));
                }
            }
        }
        return results.OrderByDescending(r => r.Confidence);
    }

    // Union bitmap rect over a window of words.
    private static (double X, double Y, double W, double H) Union(IReadOnlyList<OcrWord> ws)
    {
        double minX = ws.Min(w => w.X), minY = ws.Min(w => w.Y);
        double maxR = ws.Max(w => w.X + w.W), maxB = ws.Max(w => w.Y + w.H);
        return (minX, minY, maxR - minX, maxB - minY);
    }

    // Confidence: 1.0 exact; else 1 - editDistance/maxLen, accepted only within tolerance (at most ~1 edit per 4
    // chars, min 1). Below tolerance -> 0.0 (rejected).
    private static double FuzzyConfidence(string q, string n)
    {
        if (n == q) return 1.0;
        int d = Levenshtein(q, n);
        int maxLen = Math.Max(q.Length, n.Length);
        int tolerance = Math.Max(1, maxLen / 4);
        if (d > tolerance) return 0.0;
        return 1.0 - (double)d / maxLen;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // collapse whitespace
        return string.Join(" ", parts).ToLowerInvariant();
    }

    private static int Levenshtein(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
