using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class InstallStatusTests
{
    private static (string plugins, string dataDir, string claude, string state) TempPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-status-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "plugins"), Path.Combine(dir, "data"), Path.Combine(dir, "claude"), Path.Combine(dir, "state"));
    }

    [Fact]
    public void Reports_a_deployed_seed_with_its_version()
    {
        var (plugins, dataDir, claude, state) = TempPaths();
        new AgyConfigWriter(Path.Combine(dataDir, "s.json"), Path.Combine(dataDir, "p.json"), plugins)
            .Install(@"C:\flaui-mcp.exe");
        new ClaudeSkillDeployer(claude).Deploy();   // deploy both skills so nothing reads "NOT deployed"

        var s = InstallStatus.Describe(@"C:\flaui-mcp.exe", plugins, dataDir, claude, state);

        Assert.Contains("deployed", s);
        Assert.DoesNotContain("NOT deployed", s);
        Assert.Contains(Path.Combine(plugins, "flaui-mcp"), s);
    }

    // The question L6 exists to answer: "did the skill actually deploy?" It must be answerable
    // when the answer is NO -- that is the whole point.
    [Fact]
    public void Reports_a_missing_seed_plainly()
    {
        var (plugins, dataDir, claude, state) = TempPaths();

        var s = InstallStatus.Describe(@"C:\flaui-mcp.exe", plugins, dataDir, claude, state);

        Assert.Contains("NOT deployed", s);
        Assert.Contains("no record yet", s);   // no install.log either
    }

    [Fact]
    public void Surfaces_the_last_run_including_a_failure()
    {
        var (plugins, dataDir, claude, state) = TempPaths();
        Directory.CreateDirectory(dataDir);
        File.WriteAllLines(Path.Combine(dataDir, InstallStatus.LogName), new[]
        {
            "# flaui-mcp 0.14.0 — install at 2026-07-16 10:00:00",
            "[agy] Failed: something broke",
            "[claude] Created: somewhere",
        });

        var s = InstallStatus.Describe(@"C:\flaui-mcp.exe", plugins, dataDir, claude, state);

        Assert.Contains("[agy] Failed: something broke", s);
        Assert.Contains("[claude] Created: somewhere", s);
    }

    // End-to-end: a failure recorded by `install` must be readable back by `status`. This pair is
    // the only channel that survives Setup's runhidden run.
    [Fact]
    public void A_failed_install_is_readable_back_through_the_status_verb()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(cfg, "{ this is not json");
        var dataDir = Path.Combine(Path.GetTempPath(), $"flaui-data-{Guid.NewGuid():N}");
        var prev = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", dataDir);
        try
        {
            CliRouter.Run(new[] { "install", "--agent", "agy", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());

            var sb = new StringWriter();
            var code = CliRouter.Run(new[] { "status" }, @"C:\x\flaui-mcp.exe", sb);

            Assert.Equal(0, code);
            Assert.Contains("[agy] Failed", sb.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", prev);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(cfg) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void PrintConfig_stays_pure_json_so_it_can_still_be_piped()
    {
        var sb = new StringWriter();
        CliRouter.Run(new[] { "print-config", "--agent", "generic" }, @"C:\x\flaui-mcp.exe", sb);

        var json = System.Text.Json.Nodes.JsonNode.Parse(sb.ToString());   // throws if we polluted it
        Assert.NotNull(json?["mcpServers"]);
    }
}
