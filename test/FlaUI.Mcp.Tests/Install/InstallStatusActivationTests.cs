using System;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

/// Pins the activation hook's health into `status`, so "why is the activation hint not appearing?"
/// is answerable without reading hook source.
///
/// The class saves and restores FLAUI_MCP_STAGING_DIR — the repo's established idiom (see
/// CliRouterClaudeSkillTests) — because InstallStatus derives the staging dir from that variable, and
/// two other test classes set it process-wide. Assembly-wide parallelization is disabled, so this only
/// has to survive sequential leakage, but pinning it makes the "not staged" case deterministic rather
/// than dependent on whatever ran before.
public class InstallStatusActivationTests : IDisposable
{
    private readonly string? _savedStagingDir = Environment.GetEnvironmentVariable("FLAUI_MCP_STAGING_DIR");
    private readonly string _emptyStaging = Temp();

    public InstallStatusActivationTests()
        => Environment.SetEnvironmentVariable("FLAUI_MCP_STAGING_DIR", _emptyStaging);

    public void Dispose()
        => Environment.SetEnvironmentVariable("FLAUI_MCP_STAGING_DIR", _savedStagingDir);

    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "flaui-status-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Reports_the_activation_hook_as_absent_when_the_plugin_is_not_staged()
    {
        var text = InstallStatus.Describe(@"C:\fake\flaui-mcp.exe", Temp(), Temp(), Temp(), Temp(),
                                          ClaudePluginStatus.NotRegistered);
        Assert.Contains("Activation hook:", text);
        Assert.Contains("not staged", text);
    }

    [Fact]
    public void Reports_the_activation_hook_as_wired_when_the_staged_hooks_file_names_the_verb()
    {
        var staging = Temp();
        new PluginArtifactWriter(staging).Generate(@"C:\fake\flaui-mcp.exe", "9.9.9");

        var text = InstallStatus.DescribeActivationHook(staging);

        // StartsWith, NOT Contains("wired") — the negative message is "staged but NOT wired", which
        // CONTAINS "wired". A Contains assertion here is satisfied by the failure string, so it cannot
        // tell wired from not-wired: it would stay green if this method never reported success again.
        Assert.StartsWith("wired", text, StringComparison.Ordinal);
    }

    /// The success string must keep saying that "wired" is a claim about the STAGED FILE, not about the
    /// running client. Measured 2026-07-27: Claude Code registers plugin hooks only at client startup,
    /// so this method reports "wired" for a hook that is inert in every already-running session — and
    /// the bare wording sent the maintainer hunting a product defect that did not exist.
    ///
    /// Asserted separately from the StartsWith check above BECAUSE that check cannot see the clause:
    /// delete the caveat and every other test in this file stays green. Two vacuous assertions have
    /// already shipped in this feature; this is the third place the same hole would have opened.
    [Fact]
    public void The_wired_message_states_that_the_hook_loads_only_after_a_full_client_restart()
    {
        var staging = Temp();
        new PluginArtifactWriter(staging).Generate(@"C:\fake\flaui-mcp.exe", "9.9.9");

        var text = InstallStatus.DescribeActivationHook(staging);

        Assert.Contains("restart", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full Claude Code restart", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_not_wired_when_hooks_json_has_no_SessionStart_entry()
    {
        var staging = Temp();
        Directory.CreateDirectory(Path.Combine(staging, "hooks"));
        File.WriteAllText(Path.Combine(staging, "hooks", "hooks.json"),
            """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"bash nudge.sh"}]}]}}""");

        Assert.Contains("NOT wired", InstallStatus.DescribeActivationHook(staging));
    }

    /// The false green the parsing fix exists to prevent: the verb string present in the file, but in
    /// an unrelated hook rather than a SessionStart entry. A substring check reports this as "wired".
    [Fact]
    public void Does_not_report_wired_when_the_verb_appears_only_in_an_unrelated_hook()
    {
        var staging = Temp();
        Directory.CreateDirectory(Path.Combine(staging, "hooks"));
        File.WriteAllText(Path.Combine(staging, "hooks", "hooks.json"),
            """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo activation-payload"}]}]}}""");

        Assert.Contains("NOT wired", InstallStatus.DescribeActivationHook(staging));
    }

    /// `status` is what a user runs when the install is ALREADY broken, so it must never be the thing
    /// that throws. Each of these is valid JSON of the wrong SHAPE — the cases where JsonNode's string
    /// indexer and GetValue<string>() raise InvalidOperationException rather than returning null.
    [Theory]
    [InlineData("""{"hooks":{"SessionStart":42}}""")]                                   // SessionStart a scalar
    [InlineData("""{"hooks":{"SessionStart":[{"hooks":42}]}}""")]                       // inner hooks not an array
    [InlineData("""{"hooks":{"SessionStart":[{"hooks":[{"command":42}]}]}}""")]         // command not a string
    [InlineData("""{"hooks":{"SessionStart":[{"hooks":[{"type":"command"}]}]}}""")]     // command absent
    public void Degrades_to_a_message_instead_of_throwing_on_valid_json_of_the_wrong_shape(string json)
    {
        var staging = Temp();
        Directory.CreateDirectory(Path.Combine(staging, "hooks"));
        File.WriteAllText(Path.Combine(staging, "hooks", "hooks.json"), json);

        Assert.Contains("NOT wired", InstallStatus.DescribeActivationHook(staging));
    }

    /// A structurally broken file and a sound one that merely lacks the hook need DIFFERENT actions
    /// from the operator. Reporting "NOT wired" for both would hide the corruption behind a
    /// routine-looking message, at the exact moment the operator is trying to find it.
    [Theory]
    [InlineData("""{"hooks":42}""")]   // hooks present but not an object
    [InlineData("""[]""")]             // root is an array, not an object
    [InlineData("""{}""")]             // no hooks key at all
    public void Distinguishes_a_structurally_malformed_file_from_a_merely_unwired_one(string json)
    {
        var staging = Temp();
        Directory.CreateDirectory(Path.Combine(staging, "hooks"));
        File.WriteAllText(Path.Combine(staging, "hooks", "hooks.json"), json);

        var text = InstallStatus.DescribeActivationHook(staging);
        Assert.Contains("MALFORMED", text);
        Assert.DoesNotContain("NOT wired", text);
    }

    /// `status` runs when something is already wrong, so an OS-level read failure — an antivirus lock,
    /// a permission problem — must degrade to a message. Only JsonException was caught before, so an
    /// IOException propagated and took the whole command down.
    [Fact]
    public void Degrades_to_a_message_instead_of_throwing_when_the_file_cannot_be_read()
    {
        var staging = Temp();
        Directory.CreateDirectory(Path.Combine(staging, "hooks"));
        var path = Path.Combine(staging, "hooks", "hooks.json");
        File.WriteAllText(path, "{}");

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Contains("UNREADABLE", InstallStatus.DescribeActivationHook(staging));
    }

    [Fact]
    public void Reports_wired_when_SessionStart_is_a_single_entry_object_rather_than_an_array()
    {
        var staging = Temp();
        Directory.CreateDirectory(Path.Combine(staging, "hooks"));
        File.WriteAllText(Path.Combine(staging, "hooks", "hooks.json"),
            """{"hooks":{"SessionStart":{"matcher":"startup","hooks":[{"type":"command","command":"x activation-payload"}]}}}""");

        Assert.StartsWith("wired", InstallStatus.DescribeActivationHook(staging), StringComparison.Ordinal);
    }
}
