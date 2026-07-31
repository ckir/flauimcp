using System;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RedactionRuleLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp3rules").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string json)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    private static RedactionConfigException Rejects(string path) =>
        Assert.Throws<RedactionConfigException>(() => RedactionRuleFile.Load(path));

    [Fact]
    public void A_valid_file_loads_its_rules_in_order()
    {
        var p = Write("""
        { "version": 1, "rules": [
          { "name": "a", "processName": "devenv", "automationId": "tokenBox" },
          { "name": "b", "processName": "chrome", "automationIdPattern": "^cc_" } ] }
        """);
        var rules = RedactionRuleFile.Load(p);
        Assert.Equal(new[] { "a", "b" }, Array.ConvertAll(rules, r => r.Name));
    }

    [Fact] public void A_missing_file_is_rejected() =>
        Assert.Contains("not found", Rejects(Path.Combine(_dir, "nope.json")).Message, StringComparison.OrdinalIgnoreCase);

    [Fact] public void Malformed_json_is_rejected() =>
        Assert.Contains("parse", Rejects(Write("{ not json")).Message, StringComparison.OrdinalIgnoreCase);

    [Fact] public void An_unknown_version_is_rejected() =>
        Assert.Contains("version", Rejects(Write("""{ "version": 2, "rules": [] }""")).Message, StringComparison.OrdinalIgnoreCase);

    [Fact] public void A_duplicate_rule_name_is_rejected_and_named() =>
        Assert.Contains("dup", Rejects(Write("""
        { "version": 1, "rules": [
          { "name": "dup", "processName": "a", "automationId": "x" },
          { "name": "dup", "processName": "b", "automationId": "y" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact] public void A_rule_with_no_element_predicate_is_rejected_and_named() =>
        Assert.Contains("bare", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "bare", "processName": "a" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact] public void Global_together_with_processName_is_rejected_and_named() =>
        Assert.Contains("both", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "both", "global": true, "processName": "a", "automationId": "x" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact] public void A_rule_that_is_neither_global_nor_process_scoped_is_rejected_and_named() =>
        Assert.Contains("floating", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "floating", "automationId": "x" } ] }
        """)).Message, StringComparison.Ordinal);

    /// NonBacktracking cannot compile a backreference. Rejecting at LOAD is the whole point:
    /// a per-match timeout is charged per rule PER NODE, so one pathological pattern would burn
    /// 100ms x N nodes on every snapshot.
    [Fact] public void A_pattern_NonBacktracking_cannot_compile_is_rejected_and_named() =>
        Assert.Contains("backref", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "backref", "global": true, "namePattern": "(a)\\1" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact]
    public void More_than_MaxRules_is_rejected()
    {
        var items = string.Join(",", Enumerable.Range(0, RedactionRuleFile.MaxRules + 1)
            .Select(i => $$"""{ "name": "r{{i}}", "global": true, "automationId": "x" }"""));
        Assert.Contains("64", Rejects(Write($$"""{ "version": 1, "rules": [ {{items}} ] }""")).Message, StringComparison.Ordinal);
    }
}
