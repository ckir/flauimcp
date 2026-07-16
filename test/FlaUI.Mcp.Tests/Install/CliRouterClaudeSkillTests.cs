using System;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

// These drive CliRouter.Run end to end, so they MUST redirect every path away from the real
// profile. `81dedd7` had to fix tests that wrote into the real ~/.flaui-mcp precisely because a
// path lacked an override.
//
// Env vars are process-global. Two things make this safe, and both are pre-existing repo facts —
// do not "improve" on them:
//   - xunit.runner.json sets parallelizeTestCollections:false, so test classes run sequentially.
//     No [Collection] attribute is needed; adding one would invent a convention this repo lacks.
//   - We SAVE AND RESTORE each variable rather than nulling it (the pattern at CliRouterTests.cs:55,71).
//     Nulling would clobber a real CLAUDE_CONFIG_DIR on the machine of anyone who actually sets it.
public class CliRouterClaudeSkillTests : IDisposable
{
    private static readonly string[] Vars =
        { "FLAUI_MCP_DATA_DIR", "FLAUI_MCP_STATE_DIR", "FLAUI_MCP_CLAUDE_CONFIG_DIR", "FLAUI_MCP_AGY_PLUGINS_DIR", "CLAUDE_CONFIG_DIR" };

    private readonly string _root;
    private readonly Dictionary<string, string?> _saved = new();

    public CliRouterClaudeSkillTests()
    {
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);

        _root = Path.Combine(Path.GetTempPath(), "flaui-router-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_STATE_DIR", Path.Combine(_root, "state"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR", Path.Combine(_root, "claude"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_AGY_PLUGINS_DIR", Path.Combine(_root, "agy"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);   // our override must be what wins
    }

    public void Dispose()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, _saved[v]);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ClaudeSkill => Path.Combine(_root, "claude", "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md");

    [Fact]
    public void Install_deploys_the_claude_skill()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        Assert.True(File.Exists(ClaudeSkill), "the driving skill was not deployed for Claude Code");
    }

    [Fact]
    public void Uninstall_removes_the_claude_skill()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);

        CliRouter.Run(new[] { "uninstall", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);

        Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills", "flaui-mcp")));
    }

    // The payload is unconditional in the sense of "no contributor gate" — it must NOT mean "write
    // regardless of host". --agent all is the installer's default (installer/flaui-mcp.iss:38), so an
    // unguarded write would create an orphaned ~/.claude/skills/flaui-mcp on EVERY agy-only machine.
    [Fact]
    public void Install_deploys_nothing_for_claude_when_the_claude_cli_is_absent()
    {
        var outp = new StringWriter();
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
            Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills", "flaui-mcp")),
                "deployed a skill for a client we could not register");
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    // The NotFound gate belongs to INSTALL only. Removing the skill dir is pure file I/O and needs
    // no CLI — gating it would strand a skill on disk telling the agent to call tools that are gone.
    [Fact]
    public void Uninstall_still_removes_the_skill_when_the_claude_cli_is_absent()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        Assert.True(File.Exists(ClaudeSkill));

        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
            Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills", "flaui-mcp")),
                "a skill for deleted tools was left on disk because the CLI happened to be off PATH");
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    // The permanent-loss path: `claude` off PATH for a minute must not cost the user their plugin.
    [Fact]
    public void Uninstall_with_the_cli_absent_keeps_the_restore_marker_and_warns()
    {
        var state = Path.Combine(_root, "state");
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var outp = new StringWriter();

        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);

            Assert.True(File.Exists(CollisionMarker.PathIn(state)), "the marker was consumed without restoring anything");
            var warnings = File.ReadAllText(UninstallWarnings.PathIn(state));
            Assert.Contains("still disabled", warnings);
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    // R6, end to end: an uninstall warning must reach the file the Inno uninstaller reads, and must
    // survive the purge that runs in the same breath.
    [Fact]
    public void Uninstall_warnings_are_parked_where_they_survive_the_purge()
    {
        var state = Path.Combine(_root, "state");
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var outp = new StringWriter();

        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "all", "--purge-data", "--config", Path.Combine(_root, "c.json") },
                @"C:\x\flaui-mcp.exe", outp);

            Assert.False(Directory.Exists(Path.Combine(_root, "data")), "the purge did not happen");
            Assert.True(File.Exists(UninstallWarnings.PathIn(state)), "the purge destroyed the warnings it was supposed to outlive");
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING", null); }
    }

    [Fact]
    public void Agy_only_install_does_not_touch_the_claude_config_dir()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "agy", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        Assert.False(Directory.Exists(Path.Combine(_root, "claude", "skills")));
    }

    // R4, as a router-level test: --purge-data is read independently of --agent (CliRouter.cs:18),
    // so an agy-targeted purge wipes the SHARED data dir. The Claude marker must survive it, or a
    // later `uninstall --agent claude` concludes "the user disabled it" and strands them forever.
    [Fact]
    public void An_agy_purge_does_not_destroy_the_claude_restore_marker()
    {
        var state = Path.Combine(_root, "state");
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var outp = new StringWriter();

        CliRouter.Run(new[] { "uninstall", "--agent", "agy", "--purge-data", "--config", Path.Combine(_root, "c.json") },
            @"C:\x\flaui-mcp.exe", outp);

        Assert.True(File.Exists(CollisionMarker.PathIn(state)), "an agy purge destroyed the Claude restore marker");
    }

    [Fact]
    public void Purge_still_removes_the_data_dir()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "install", "--agent", "generic", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
        CliRouter.Run(new[] { "uninstall", "--agent", "all", "--purge-data", "--config", Path.Combine(_root, "c.json") },
            @"C:\x\flaui-mcp.exe", outp);

        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void CLAUDE_CONFIG_DIR_is_honored_when_our_own_override_is_absent()
    {
        Environment.SetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR", null);
        var claudeHome = Path.Combine(_root, "claude-home");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeHome);
        try
        {
            var outp = new StringWriter();
            CliRouter.Run(new[] { "install", "--agent", "claude", "--config", Path.Combine(_root, "c.json") }, @"C:\x\flaui-mcp.exe", outp);
            Assert.True(File.Exists(Path.Combine(claudeHome, "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md")));
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null); }
    }
}
