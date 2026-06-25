using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class CliRouterTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("print-config")]
    [InlineData("--version")]
    [InlineData("--help")]
    public void IsInstallerVerb_true_for_known_verbs(string verb)
        => Assert.True(CliRouter.IsInstallerVerb(new[] { verb }));

    [Fact]
    public void IsInstallerVerb_false_for_no_args()
        => Assert.False(CliRouter.IsInstallerVerb(Array.Empty<string>()));

    [Fact]
    public void IsInstallerVerb_false_for_unknown_arg()
        => Assert.False(CliRouter.IsInstallerVerb(new[] { "--unexpected" }));

    [Fact]
    public void Version_prints_and_returns_zero()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "--version" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(0, code);
        Assert.Contains("flaui-mcp", sb.ToString());
    }

    [Fact]
    public void PrintConfig_generic_emits_snippet()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "print-config", "--agent", "generic" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(0, code);
        Assert.Contains("mcpServers", sb.ToString());
    }

    [Fact]
    public void Uninstall_sweeps_our_backup_files_next_to_the_config()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        File.WriteAllText(cfg, "{ \"mcpServers\": { \"flaui-mcp\": { \"command\": \"x\", \"args\": [] } } }");
        var bak = cfg + ".bak-20260101000000";
        File.WriteAllText(bak, "{}");
        try
        {
            var sb = new StringWriter();
            CliRouter.Run(new[] { "uninstall", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", sb);
            Assert.False(File.Exists(bak), "our .bak-<ts> file should have been swept on uninstall");
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Uninstall_with_purge_data_removes_the_data_dir()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"flaui-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "generic-mcp.json"), "{}");
        // --config redirects the agy/generic paths to a throwaway temp file so no real config is touched.
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        var prev = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", dataDir);
        try
        {
            var sb = new StringWriter();
            CliRouter.Run(new[] { "uninstall", "--agent", "generic", "--config", cfg, "--purge-data" }, @"C:\x\flaui-mcp.exe", sb);
            Assert.False(Directory.Exists(dataDir), "--purge-data should remove the data dir");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", prev);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(cfg) + "*")) File.Delete(f);
        }
    }
}
