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

    /// Startup reads the rule file ONCE and both hashes and parses those same bytes, so parsing moved off
    /// File.ReadAllText onto a byte overload. ReadAllText detected a BOM; System.Text.Json REJECTS a
    /// leading BOM outright. Without BOM-detecting decode, every BOM'd rule file — what Notepad and many
    /// editors produce by default on Windows — would become "could not parse" and refuse server startup.
    [Fact]
    public void A_rule_file_saved_with_a_utf8_bom_still_loads()
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, """{ "version": 1, "rules": [ { "name": "a", "global": true, "automationId": "x" } ] }""",
                          new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("a", Assert.Single(RedactionRuleFile.Load(p)).Name);
    }

    /// The byte overload and the path overload must agree — startup uses Parse, the check-redaction-rules
    /// CLI uses Load, and a divergence would make the CLI's verdict describe different rules than the
    /// server actually enforces.
    [Fact]
    public void Parse_over_bytes_agrees_with_Load_over_the_same_file()
    {
        const string json = """
        { "version": 1, "rules": [
          { "name": "a", "processName": "devenv", "automationId": "tokenBox" },
          { "name": "b", "processName": "chrome", "automationIdPattern": "^cc_" } ] }
        """;
        var p = Write(json);
        Assert.Equal(Array.ConvertAll(RedactionRuleFile.Load(p), r => r.Name),
                     Array.ConvertAll(RedactionRuleFile.Parse(File.ReadAllBytes(p), p), r => r.Name));
    }
}
