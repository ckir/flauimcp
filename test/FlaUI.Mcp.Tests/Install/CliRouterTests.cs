using FlaUI.Mcp.Core.Presence;
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

    [Fact]
    public void Install_output_hints_at_the_overlay_flag()
    {
        // Discoverability: the post-install message must surface the opt-in overlay toggle.
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        try
        {
            var sb = new StringWriter();
            var code = CliRouter.Run(new[] { "install", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", sb);
            Assert.Equal(0, code);
            Assert.Contains("flaui-mcp overlay on", sb.ToString());
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Overlay_on_registers_the_generic_entry_with_the_overlay_args()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        try
        {
            var sb = new StringWriter();
            var code = CliRouter.Run(new[] { "overlay", "on", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", sb);
            Assert.Equal(0, code);
            var json = File.ReadAllText(cfg);
            Assert.Contains("--overlay", json);
            Assert.Contains("--overlay-ms=800", json);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f); }
    }

    [Fact]
    public void Overlay_off_registers_the_generic_entry_without_overlay_args()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        try
        {
            CliRouter.Run(new[] { "overlay", "on", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            var code = CliRouter.Run(new[] { "overlay", "off", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            Assert.Equal(0, code);
            var json = File.ReadAllText(cfg);
            Assert.DoesNotContain("--overlay", json);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f); }
    }

    [Fact]
    public void Overlay_on_then_autosound_on_coexist_non_destructively()
    {
        // Pinning test for the SP-A T9 fix: `overlay on` used to REPLACE the whole `args` array, so a
        // subsequent `autosound on` would drop `--overlay`. ConfigArgsMerge + ApplyMerge must preserve both.
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        try
        {
            var overlayCode = CliRouter.Run(new[] { "overlay", "on", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            var autosoundCode = CliRouter.Run(new[] { "autosound", "on", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            Assert.Equal(0, overlayCode);
            Assert.Equal(0, autosoundCode);
            var json = File.ReadAllText(cfg);
            Assert.Contains("--overlay", json);
            Assert.Contains("--overlay-ms=800", json);
            Assert.Contains("--autosound", json);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f); }
    }

    [Fact]
    public void Autosound_off_registers_the_generic_entry_without_autosound_arg()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        try
        {
            CliRouter.Run(new[] { "autosound", "on", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            var code = CliRouter.Run(new[] { "autosound", "off", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            Assert.Equal(0, code);
            var json = File.ReadAllText(cfg);
            Assert.DoesNotContain("--autosound", json);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f); }
    }

    [Fact]
    public void Autosound_bad_mode_prints_usage_and_returns_nonzero()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "autosound", "sideways" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(2, code);
        Assert.Contains("usage", sb.ToString());
    }

    [Fact]
    public void Autosound_is_a_recognized_installer_verb()
        => Assert.True(CliRouter.IsInstallerVerb(new[] { "autosound", "on" }));

    [Fact]
    public void Overlay_bad_mode_prints_usage_and_returns_nonzero()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "overlay", "sideways" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(2, code);
        Assert.Contains("usage", sb.ToString());
    }

    [Fact]
    public void Overlay_is_a_recognized_installer_verb()
        => Assert.True(CliRouter.IsInstallerVerb(new[] { "overlay", "on" }));

    [Fact]
    public void Help_documents_all_the_verbs()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "--help" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(0, code);
        var s = sb.ToString();
        Assert.Contains("USAGE:", s);
        Assert.Contains("install", s);
        Assert.Contains("overlay on|off", s);
        Assert.Contains("unlock", s);
        Assert.Contains("--version", s);
    }

    [Fact]
    public void No_args_and_dash_h_both_show_the_full_help()
    {
        var noArgs = new StringWriter();
        Assert.Equal(0, CliRouter.Run(System.Array.Empty<string>(), @"C:\x\flaui-mcp.exe", noArgs));
        Assert.Contains("VERBS:", noArgs.ToString());

        var dashH = new StringWriter();
        Assert.Equal(0, CliRouter.Run(new[] { "-h" }, @"C:\x\flaui-mcp.exe", dashH));
        Assert.Contains("VERBS:", dashH.ToString());
    }

    [Fact]
    public void Presence_is_a_recognized_installer_verb()
        => Assert.True(CliRouter.IsInstallerVerb(new[] { "presence", "on" }));

    [Fact]
    public void Presence_bad_mode_prints_usage_and_returns_nonzero()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "presence", "sideways" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(2, code);
        Assert.Contains("usage", sb.ToString());
    }

    [Fact]
    public void Presence_on_registers_flag_and_writes_live_state()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        var dataDir = Path.Combine(Path.GetTempPath(), $"flaui-data-{Guid.NewGuid():N}");
        var prev = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", dataDir);
        try
        {
            var code = CliRouter.Run(new[] { "presence", "on", "--nearby-secs", "30", "--away-secs", "200", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            Assert.Equal(0, code);
            var json = File.ReadAllText(cfg);
            Assert.Contains("--presence", json);
            Assert.Contains("--nearby-secs=30", json);
            Assert.Contains("--away-secs=200", json);
            Assert.True(PresenceConfig.TryParse(File.ReadAllText(PresenceState.StatePath()), out var c) && c.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", prev);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Presence_off_preserves_overlay_and_revokes_live()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        var dataDir = Path.Combine(Path.GetTempPath(), $"flaui-data-{Guid.NewGuid():N}");
        var prev = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", dataDir);
        try
        {
            CliRouter.Run(new[] { "overlay", "on", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            CliRouter.Run(new[] { "presence", "on", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            var code = CliRouter.Run(new[] { "presence", "off", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
            Assert.Equal(0, code);
            var json = File.ReadAllText(cfg);
            Assert.Contains("--overlay", json);          // sibling flag group preserved
            Assert.DoesNotContain("--presence", json);
            Assert.True(PresenceConfig.TryParse(File.ReadAllText(PresenceState.StatePath()), out var c) && !c.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", prev);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Presence_on_with_invalid_thresholds_refuses()
    {
        var cfg = Path.Combine(Path.GetTempPath(), $"flaui-cli-{Guid.NewGuid():N}.json");
        try
        {
            var sb = new StringWriter();
            var code = CliRouter.Run(new[] { "presence", "on", "--nearby-secs", "60", "--away-secs", "10", "--agent", "generic", "--config", cfg }, @"C:\x\flaui-mcp.exe", sb);
            Assert.Equal(2, code);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg) + "*")) File.Delete(f); }
    }
}
