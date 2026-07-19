using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class InstallStatusClaudeTests
{
    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-status-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Reports_the_claude_skill_as_not_deployed_when_the_plugin_is_not_registered()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp(), ClaudePluginStatus.NotRegistered);
        Assert.Contains("Driving skill (Claude Code):", text);
        Assert.Contains("NOT deployed", text);
        Assert.Contains("flaui-mcp install --agent claude", text);
    }

    // This is the regression the false-negative bug reproduced: post-installer-rework, `install`
    // registers Claude Code as a PLUGIN and never copies a skill dir, so the primary signal MUST be
    // the plugin registration, not a probe of the retired skills dir (absent here, on purpose).
    [Fact]
    public void Reports_the_claude_skill_deployed_via_plugin_when_the_plugin_is_active()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp(), ClaudePluginStatus.Active);

        // Scope to the Claude section only -- the agy seed section legitimately says "NOT deployed"
        // here too (nothing was deployed to the unused agy Temp() dir); that is not what this test covers.
        var claudeSection = text.Substring(text.IndexOf("Driving skill (Claude Code):", StringComparison.Ordinal));
        Assert.Contains("deployed as plugin (flaui-mcp@flaui-mcp-marketplace)", claudeSection);
        Assert.DoesNotContain("NOT deployed", claudeSection);
    }

    // Even with a leftover copy from the old skill-directory model still on disk, plugin registration
    // stays the PRIMARY signal -- a leftover is at most a mentioned aside, never the reason status says
    // deployed/not-deployed. This is the exact shape of the original false-negative bug: a correctly
    // plugin-installed machine must not read "NOT deployed" just because the retired dir is gone.
    [Fact]
    public void A_leftover_legacy_skill_dir_does_not_override_the_plugin_signal()
    {
        var claude = Temp();
        new ClaudeSkillDeployer(claude).Deploy();

        var active = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp(), ClaudePluginStatus.Active);
        Assert.Contains("deployed as plugin (flaui-mcp@flaui-mcp-marketplace)", active);

        var notRegistered = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp(), ClaudePluginStatus.NotRegistered);
        Assert.Contains("NOT deployed", notRegistered);
    }

    [Fact]
    public void Reports_when_the_claude_cli_is_not_on_path()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp(), ClaudePluginStatus.CliNotFound);
        Assert.Contains("claude CLI not found on PATH", text);
        Assert.Contains("can't check plugin registration", text);
    }

    // R5's channel: "we disabled your marketplace copy" is otherwise invisible, and Setup ran hidden.
    [Fact]
    public void Reports_a_marketplace_copy_we_disabled()
    {
        var state = Temp();
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\proj") });

        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), state, ClaudePluginStatus.NotRegistered);

        Assert.Contains("flaui-mcp@flaui-mcp", text);
        Assert.Contains(@"C:\proj", text);
        Assert.Contains("re-enabled if you uninstall", text);
    }

    [Fact]
    public void Says_nothing_about_collisions_when_there_are_none()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp(), ClaudePluginStatus.NotRegistered);
        Assert.DoesNotContain("flaui-mcp@flaui-mcp", text);
    }

    [Fact]
    public void Still_reports_the_agy_seed()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp(), ClaudePluginStatus.NotRegistered);
        Assert.Contains("Seed driving skill (agy):", text);
    }
}
