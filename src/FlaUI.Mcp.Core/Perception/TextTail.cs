namespace FlaUI.Mcp.Core.Perception;

/// <summary>Surrogate-safe tail slicing for the desktop_get_text fromEnd read (spec §5.4). Taking "the
/// last N chars" by naive index math can split a UTF-16 surrogate pair, leaving an unpaired surrogate
/// that System.Text.Json replaces with U+FFFD (or rejects). This backs the cut forward to a code-point
/// boundary so the returned string is always valid UTF-16 before serialization.</summary>
public static class TextTail
{
    /// <summary>The last <paramref name="cap"/> chars of <paramref name="s"/>, never beginning on an
    /// unpaired low surrogate. If the naive cut would land inside a surrogate pair, drop that half-pair
    /// (start one char later) so the result is a whole number of code points from the end.</summary>
    public static string Slice(string s, int cap)
    {
        if (cap <= 0) return string.Empty;
        if (s.Length <= cap) return s;
        int start = s.Length - cap;
        // If the cut lands on the LOW half of a pair, step forward past it (yields cap-1 chars, valid).
        if (char.IsLowSurrogate(s[start])) start++;
        return s.Substring(start);
    }
}
