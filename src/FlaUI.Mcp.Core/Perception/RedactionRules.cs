using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

internal sealed record RuleDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("processName")] string? ProcessName,
    [property: JsonPropertyName("global")] bool Global,
    [property: JsonPropertyName("automationId")] string? AutomationId,
    [property: JsonPropertyName("automationIdPattern")] string? AutomationIdPattern,
    [property: JsonPropertyName("namePattern")] string? NamePattern);

internal sealed record RuleFileDto(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rules")] List<RuleDto>? Rules);

public static class RedactionRuleFile
{
    public const int MaxRules = 64;
    private const int SupportedVersion = 1;

    public static RedactionRule[] Load(string path)
    {
        if (!File.Exists(path))
            throw new RedactionConfigException($"Redaction rule file not found: '{path}'.");

        RuleFileDto? dto;
        try { dto = JsonSerializer.Deserialize<RuleFileDto>(File.ReadAllText(path)); }
        catch (Exception ex) { throw new RedactionConfigException($"Could not parse '{path}': {ex.Message}"); }

        if (dto is null) throw new RedactionConfigException($"Could not parse '{path}': empty document.");
        if (dto.Version != SupportedVersion)
            throw new RedactionConfigException(
                $"Unsupported redaction rule file version {dto.Version} (this build supports {SupportedVersion}).");

        var rules = dto.Rules ?? new List<RuleDto>();
        if (rules.Count > MaxRules)
            throw new RedactionConfigException(
                $"{rules.Count} rules exceeds the maximum of {MaxRules}. More than {MaxRules} rules is a policy " +
                "problem, not a configuration problem.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var built = new List<RedactionRule>(rules.Count);
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            var label = string.IsNullOrWhiteSpace(r.Name) ? $"at index {i}" : $"'{r.Name}'";
            if (string.IsNullOrWhiteSpace(r.Name))
                throw new RedactionConfigException($"Rule {label}: 'name' is required and must be non-empty.");
            if (!seen.Add(r.Name!))
                throw new RedactionConfigException($"Rule '{r.Name}': duplicate rule name.");
            if (r.Global && !string.IsNullOrEmpty(r.ProcessName))
                throw new RedactionConfigException(
                    $"Rule '{r.Name}': 'global' and 'processName' are mutually exclusive.");
            if (!r.Global && string.IsNullOrEmpty(r.ProcessName))
                throw new RedactionConfigException(
                    $"Rule '{r.Name}': needs 'processName', or 'global': true to opt into a machine-wide rule.");
            if (string.IsNullOrEmpty(r.AutomationId) && string.IsNullOrEmpty(r.AutomationIdPattern)
                && string.IsNullOrEmpty(r.NamePattern))
                throw new RedactionConfigException(
                    $"Rule '{r.Name}': needs at least one of automationId / automationIdPattern / namePattern. " +
                    "A rule with no element predicate would redact an entire process.");

            built.Add(new RedactionRule(r.Name!, r.ProcessName, r.Global,
                                        r.AutomationId, r.AutomationIdPattern, r.NamePattern));
        }
        return built.ToArray();
    }
}
