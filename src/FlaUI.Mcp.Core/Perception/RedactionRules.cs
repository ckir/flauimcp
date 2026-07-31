using System;
using System.Text.RegularExpressions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One operator rule. All specified predicates must match (AND). Evaluated
/// cheapest-first — processName, then exact automationId, then regexes — so a false cheap predicate
/// short-circuits before any regex runs.</summary>
public sealed class RedactionRule
{
    private readonly Regex? _aidPattern, _namePattern;

    public RedactionRule(string name, string? processName, bool global,
                         string? automationId, string? automationIdPattern, string? namePattern)
    {
        Name = name; ProcessName = processName; Global = global; AutomationId = automationId;
        _aidPattern = Compile(automationIdPattern, name, nameof(automationIdPattern));
        _namePattern = Compile(namePattern, name, nameof(namePattern));
    }

    public string Name { get; }
    public string? ProcessName { get; }
    public bool Global { get; }
    public string? AutomationId { get; }

    private static Regex? Compile(string? pattern, string ruleName, string field)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try
        {
            // NonBacktracking guarantees linear time and has no catastrophic backtracking by
            // construction. It rejects backreferences/lookaround — rejecting expressive-but-unbounded
            // patterns at LOAD is the design, not a limitation to work around.
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                             TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex)
        {
            throw new RedactionConfigException(
                $"Rule '{ruleName}': {field} cannot be compiled with RegexOptions.NonBacktracking " +
                $"({ex.Message}). Backreferences, lookaround and atomic groups are not supported — " +
                "rewrite the pattern without them.");
        }
    }

    public bool Matches(string? processName, Func<string?> automationId, Func<string?> rawName)
    {
        if (!Global && !string.Equals(ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            return false;                                    // cheapest predicate, short-circuits

        if (AutomationId is not null &&
            !string.Equals(AutomationId, Safe(automationId), StringComparison.Ordinal)) return false;

        if (_aidPattern is not null && !IsMatch(_aidPattern, Safe(automationId))) return false;
        if (_namePattern is not null && !IsMatch(_namePattern, Safe(rawName))) return false;
        return true;
    }

    // An ABSENT value is not an exception: Regex.IsMatch(null, ...) throws, and many UIA elements
    // legitimately have no Name. Normalising to "" keeps one namePattern rule from redacting every
    // nameless element in its scoped process.
    private static string Safe(Func<string?> read)
    {
        try { return read() ?? string.Empty; } catch { return string.Empty; }
    }

    // A genuinely UNEXPECTED failure (regex timeout on pathological input) fails closed at THIS
    // predicate only; the rule's AND still applies.
    private static bool IsMatch(Regex re, string value)
    {
        try { return re.IsMatch(value); } catch { return true; }
    }
}

/// <summary>A malformed rule file is FATAL at startup: an operator who authored a rule file is
/// depending on a shield, and starting with an empty rule set silently voids protection they asked
/// for.</summary>
public sealed class RedactionConfigException(string message) : Exception(message);
