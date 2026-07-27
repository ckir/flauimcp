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
        Assert.Contains("wired", text);
    }
}
