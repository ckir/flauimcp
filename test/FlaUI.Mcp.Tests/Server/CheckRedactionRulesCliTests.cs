using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>Pins the `check-redaction-rules` dry-run verb's exit codes (spec §5.5) and its
/// always-print-the-banner / always-print-the-results behavior. Every test writes its own temp rule
/// file and its own temp instances directory — never the real <see cref="ServerStateFile.DefaultDirectory"/>.</summary>
public class CheckRedactionRulesCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-checkredact-" + Guid.NewGuid().ToString("N"));

    public CheckRedactionRulesCliTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Banner1 = "NOTE: these results reflect the RULE FILE on disk. A running server enforces the rules it loaded at";
    private const string Banner2 = "boot; restart it to apply changes. Exit code 0 means the running server is already enforcing this file.";

    private string ValidRuleFile(string ruleName = "ok")
    {
        var path = Path.Combine(_dir, "rules.json");
        File.WriteAllText(path,
            $"{{\"version\":1,\"rules\":[{{\"name\":\"{ruleName}\",\"processName\":\"notepad\",\"namePattern\":\"secret\"}}]}}");
        return path;
    }

    private string InvalidRuleFile()
    {
        var path = Path.Combine(_dir, "bad-rules.json");
        File.WriteAllText(path, "{\"version\":1,\"rules\":[{\"name\":\"bad\",\"namePattern\":\"(unclosed\"}]}");
        return path;
    }

    private static string Sha256Hex(string path)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(File.ReadAllBytes(path));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string NewInstancesDir()
    {
        var d = Path.Combine(_dir, "instances-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// Writes an instance state file by hand, matching the DTO's JSON shape byte-for-byte
    /// (the DTO itself is internal, so tests cannot construct it directly).
    private static void WriteInstance(string dir, int stateVersion, int pid, DateTime startUtc,
        string? rulesPath, string? rulesSha)
    {
        var path = Path.Combine(dir, $"{pid}-{Guid.NewGuid():N}.json");
        var json = "{\"stateVersion\":" + stateVersion.ToString(CultureInfo.InvariantCulture) +
                   ",\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) +
                   ",\"processStartTimeUtc\":\"" + startUtc.ToString("O", CultureInfo.InvariantCulture) + "\"" +
                   ",\"redactionRulesPath\":" + (rulesPath is null ? "null" : $"\"{Escape(rulesPath)}\"") +
                   ",\"redactionRulesSha256\":" + (rulesSha is null ? "null" : $"\"{rulesSha}\"") +
                   "}";
        File.WriteAllText(path, json);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\");

    private static (int Pid, DateTime StartUtc) LiveSelf()
    {
        var p = Process.GetCurrentProcess();
        return (Environment.ProcessId, p.StartTime.ToUniversalTime());
    }

    [Fact]
    public void Valid_file_no_instances_exits_5()
    {
        var rules = ValidRuleFile();
        var instances = NewInstancesDir();
        var outp = new StringWriter();

        var code = CheckRedactionRulesCommand.Run(new[] { "check-redaction-rules", rules }, outp, instances);

        Assert.Equal(5, code);
    }

    [Fact]
    public void Invalid_file_exits_1_and_names_the_offending_rule()
    {
        var rules = InvalidRuleFile();
        var instances = NewInstancesDir();
        var outp = new StringWriter();

        var code = CheckRedactionRulesCommand.Run(new[] { "check-redaction-rules", rules }, outp, instances);

        Assert.Equal(1, code);
        Assert.Contains("bad", outp.ToString());
    }

    [Fact]
    public void Valid_file_live_instance_different_hash_exits_3_and_still_prints_results()
    {
        var ruleName = "distinct-rule-name";
        var rules = ValidRuleFile(ruleName);
        var instances = NewInstancesDir();
        var (pid, startUtc) = LiveSelf();
        WriteInstance(instances, ServerStateFile.CurrentStateVersion, pid, startUtc, rules, "deadbeef-not-the-real-hash");
        var outp = new StringWriter();

        var code = CheckRedactionRulesCommand.Run(new[] { "check-redaction-rules", rules }, outp, instances);

        Assert.Equal(3, code);
        Assert.Contains(ruleName, outp.ToString());
    }

    [Fact]
    public void Version_skew_instance_exits_4_and_mentions_upgrading()
    {
        var rules = ValidRuleFile();
        var instances = NewInstancesDir();
        WriteInstance(instances, 999, 424242, DateTime.UtcNow, null, null);
        var outp = new StringWriter();

        var code = CheckRedactionRulesCommand.Run(new[] { "check-redaction-rules", rules }, outp, instances);

        Assert.Equal(4, code);
        Assert.Contains("upgrad", outp.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valid_file_live_instance_matching_hash_exits_0()
    {
        var rules = ValidRuleFile();
        var instances = NewInstancesDir();
        var (pid, startUtc) = LiveSelf();
        var sha = Sha256Hex(rules);
        WriteInstance(instances, ServerStateFile.CurrentStateVersion, pid, startUtc, rules, sha);
        var outp = new StringWriter();

        var code = CheckRedactionRulesCommand.Run(new[] { "check-redaction-rules", rules }, outp, instances);

        Assert.Equal(0, code);
    }

    public static IEnumerable<object[]> AllExitPathCases()
    {
        yield return new object[] { "no-instances" };
        yield return new object[] { "invalid-file" };
        yield return new object[] { "hash-mismatch" };
        yield return new object[] { "version-skew" };
        yield return new object[] { "hash-match" };
    }

    [Theory]
    [MemberData(nameof(AllExitPathCases))]
    public void Every_exit_path_emits_the_banner(string scenario)
    {
        var instances = NewInstancesDir();
        string rules;
        switch (scenario)
        {
            case "no-instances":
                rules = ValidRuleFile();
                break;
            case "invalid-file":
                rules = InvalidRuleFile();
                break;
            case "hash-mismatch":
            {
                rules = ValidRuleFile();
                var (pid, startUtc) = LiveSelf();
                WriteInstance(instances, ServerStateFile.CurrentStateVersion, pid, startUtc, rules, "not-the-real-hash");
                break;
            }
            case "version-skew":
                rules = ValidRuleFile();
                WriteInstance(instances, 999, 424242, DateTime.UtcNow, null, null);
                break;
            case "hash-match":
            {
                rules = ValidRuleFile();
                var (pid, startUtc) = LiveSelf();
                WriteInstance(instances, ServerStateFile.CurrentStateVersion, pid, startUtc, rules, Sha256Hex(rules));
                break;
            }
            default:
                throw new InvalidOperationException(scenario);
        }

        var outp = new StringWriter();
        CheckRedactionRulesCommand.Run(new[] { "check-redaction-rules", rules }, outp, instances);

        var text = outp.ToString();
        Assert.Contains(Banner1, text);
        Assert.Contains(Banner2, text);
    }
}
