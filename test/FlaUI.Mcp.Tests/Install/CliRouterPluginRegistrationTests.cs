using System;
using System.Collections.Generic;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

// e2e through CliRouter.Run, faking BOTH agents present via env seams + redirected paths. Mirrors the
// SAVE-AND-RESTORE env idiom of CliRouterClaudeSkillTests (no [Collection]: xunit.runner.json sets
// parallelizeTestCollections:false, so classes run sequentially; we restore each var so we never
// clobber a real value a dev may have set).
public class CliRouterPluginRegistrationTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "FLAUI_MCP_DATA_DIR", "FLAUI_MCP_STATE_DIR", "FLAUI_MCP_CLAUDE_CONFIG_DIR", "FLAUI_MCP_AGY_PLUGINS_DIR",
        "FLAUI_MCP_STAGING_DIR", "CLAUDE_CONFIG_DIR", "FLAUI_MCP_FAKE_CLAUDE_PRESENT", "FLAUI_MCP_FAKE_AGY_PRESENT",
        "FLAUI_MCP_FAKE_AGY_FAIL",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "flaui-cr-" + Path.GetRandomFileName());
    private readonly Dictionary<string, string?> _saved = new();

    public CliRouterPluginRegistrationTests()
    {
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);

        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_STATE_DIR", Path.Combine(_root, "state"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR", Path.Combine(_root, "claude"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_AGY_PLUGINS_DIR", Path.Combine(_root, "agy-plugins"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_STAGING_DIR", Path.Combine(_root, "plugin"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);   // our override must be what wins
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_PRESENT", "1");
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_AGY_PRESENT", "1");
    }

    public void Dispose()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, _saved[v]);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Install_generates_staging_artifacts_and_writes_no_agent_config_file()
    {
        var exe = Path.Combine(_root, "flaui-mcp.exe");
        File.WriteAllText(exe, "");
        var sw = new StringWriter();

        // CRITICAL isolation: pass --config to a DUMMY path. AgyServers/AgyPerms/GenericPath have NO env
        // override — only --config overrides them — so WITHOUT this the migration sweep
        // (AgyConfigWriter.Uninstall) runs against the dev's REAL ~/.gemini settings.
        var isolatedConfig = Path.Combine(_root, "agy-mcp_config.json");
        var code = CliRouter.Run(new[] { "install", "--agent", "all", "--config", isolatedConfig }, exe, sw);

        Assert.Equal(0, code);
        var staging = Path.Combine(_root, "plugin");
        Assert.True(File.Exists(Path.Combine(staging, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(staging, ".claude-plugin", "marketplace.json")));
        Assert.True(File.Exists(Path.Combine(staging, "skills", "driving-flaui-mcp", "SKILL.md")));

        // NO hand-written AGY config: the rework registers agy/claude via their CLIs, not a config file.
        // The GENERIC writer is deliberately left unchanged (it has no CLI to register with — its written
        // mcpServers snippet IS the mechanism), and --config collapses GenericPath onto this same path, so
        // an mcpServers block legitimately lands here. What MUST be gone is the retired agy hand-written
        // config — its `permissions` block and the `mcp(flaui-mcp/*)` allow token, which no other writer emits.
        if (File.Exists(isolatedConfig))
        {
            var text = File.ReadAllText(isolatedConfig);
            Assert.DoesNotContain("permissions", text);
            Assert.DoesNotContain("mcp(flaui-mcp/*)", text);
        }
    }

    // ALL Run() calls pass --config to isolate AgyServers/AgyPerms (no env override for them).
    private string Cfg => Path.Combine(_root, "agy-mcp_config.json");

    [Fact]
    public void Uninstall_deletes_staging_on_success_and_writes_no_warning()
    {
        var exe = Path.Combine(_root, "flaui-mcp.exe"); File.WriteAllText(exe, "");
        CliRouter.Run(new[] { "install", "--agent", "all", "--config", Cfg }, exe, new StringWriter());
        var staging = Path.Combine(_root, "plugin");
        Assert.True(Directory.Exists(staging));

        var code = CliRouter.Run(new[] { "uninstall", "--agent", "all", "--config", Cfg }, exe, new StringWriter());

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(staging)); // deleted on successful full deregister
        Assert.False(File.Exists(Path.Combine(_root, "state", "uninstall-warnings.log")));
    }

    [Fact]
    public void Uninstall_leaves_staging_and_warns_when_deregister_fails()
    {
        var exe = Path.Combine(_root, "flaui-mcp.exe"); File.WriteAllText(exe, "");
        CliRouter.Run(new[] { "install", "--agent", "all", "--config", Cfg }, exe, new StringWriter());
        var staging = Path.Combine(_root, "plugin");

        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_AGY_FAIL", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "all", "--config", Cfg }, exe, new StringWriter());
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_AGY_FAIL", null); }

        Assert.True(Directory.Exists(staging)); // left in place — still referenced
        var log = File.ReadAllText(Path.Combine(_root, "state", "uninstall-warnings.log"));
        Assert.Contains(@".gemini", log); // warning names agy's managed dir
        Assert.Contains("agy plugin uninstall", log); // instructs CLI-deregister before manual delete
    }

    [Fact]
    public void Targeted_agy_uninstall_does_not_delete_shared_staging_that_claude_live_mounts()
    {
        var exe = Path.Combine(_root, "flaui-mcp.exe"); File.WriteAllText(exe, "");
        CliRouter.Run(new[] { "install", "--agent", "all", "--config", Cfg }, exe, new StringWriter());
        var staging = Path.Combine(_root, "plugin");
        Assert.True(Directory.Exists(staging));

        CliRouter.Run(new[] { "uninstall", "--agent", "agy", "--config", Cfg }, exe, new StringWriter());

        // Claude (not targeted) still live-mounts staging — a targeted agy uninstall must not delete it. (R3.)
        Assert.True(Directory.Exists(staging));
    }
}
