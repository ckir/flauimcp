using System;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class PathsForAgentSweepTests : IDisposable
{
    // Every path the uninstall touches MUST be redirected away from the real profile: `--agent claude`
    // and `all` run the claude branch (deployer.Remove on CLAUDE_CONFIG_DIR, remedy.Restore on STATE_DIR).
    // FLAUI_MCP_FAKE_CLAUDE_MISSING=1 also avoids invoking the real `claude` CLI (absent in CI). The sweep
    // under test runs after the agent loop regardless, so faking claude does not affect what it asserts.
    private static readonly string[] Vars =
        { "FLAUI_MCP_DATA_DIR", "FLAUI_MCP_STATE_DIR", "FLAUI_MCP_CLAUDE_CONFIG_DIR",
          "FLAUI_MCP_AGY_PLUGINS_DIR", "CLAUDE_CONFIG_DIR", "FLAUI_MCP_FAKE_CLAUDE_MISSING" };

    private readonly string _dir;
    private readonly string _cfg;
    private readonly Dictionary<string, string?> _saved = new();

    public PathsForAgentSweepTests()
    {
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);

        _dir = Path.Combine(Path.GetTempPath(), "flaui-sweep-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _cfg = Path.Combine(_dir, "c.json");
        File.WriteAllText(_cfg, "{ \"mcpServers\": {} }");
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", Path.Combine(_dir, "data"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_STATE_DIR", Path.Combine(_dir, "state"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR", Path.Combine(_dir, "claude"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_AGY_PLUGINS_DIR", Path.Combine(_dir, "agy"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
    }

    public void Dispose()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, _saved[v]);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string SeedBak()
    {
        var bak = _cfg + ".bak-20260101000000";
        File.WriteAllText(bak, "{}");
        return bak;
    }

    // Assert Run SUCCEEDED before trusting the file's presence/absence. Without this, an internal
    // abort BEFORE the sweep phase would also leave the file on disk, faking a "not swept" pass.
    private void Uninstall(string agent)
    {
        var code = CliRouter.Run(new[] { "uninstall", "--agent", agent, "--config", _cfg }, @"C:\x\flaui-mcp.exe", new StringWriter());
        Assert.Equal(0, code);
    }

    [Fact]
    public void Uninstall_agent_claude_sweeps_no_backups()
    {
        var bak = SeedBak();
        Uninstall("claude");
        Assert.True(File.Exists(bak), "claude has no local config file, so its uninstall must not sweep the redirected (agy/generic) backups");
    }

    [Fact]
    public void Uninstall_agent_all_still_sweeps_backups()
    {
        var bak = SeedBak();
        Uninstall("all");
        Assert.False(File.Exists(bak), "a full uninstall must still sweep our own backups");
    }

    [Fact]
    public void Uninstall_agent_agy_sweeps_backups()
    {
        var bak = SeedBak();
        Uninstall("agy");
        Assert.False(File.Exists(bak), "agy owns AgyServers/AgyPerms, which --config redirected here, so it must sweep");
    }

    [Fact]
    public void Uninstall_agent_AGY_uppercase_still_sweeps()   // pins the case-insensitive match
    {
        var bak = SeedBak();
        Uninstall("AGY");
        Assert.False(File.Exists(bak), "the agent match is OrdinalIgnoreCase, so uppercase AGY must sweep like agy");
    }
}
