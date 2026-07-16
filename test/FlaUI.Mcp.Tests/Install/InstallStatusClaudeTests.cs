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
    public void Reports_the_claude_skill_as_not_deployed_when_it_is_absent()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp());
        Assert.Contains("Driving skill (Claude Code):", text);
        Assert.Contains("NOT deployed", text);
    }

    [Fact]
    public void Reports_the_claude_skill_and_its_version_once_deployed()
    {
        var claude = Temp();
        new ClaudeSkillDeployer(claude).Deploy();

        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp());

        var av = typeof(InstallStatus).Assembly.GetName().Version!;
        Assert.Contains("deployed", text);
        Assert.Contains($"v{av.Major}.{av.Minor}.{av.Build}", text);
    }

    // R5's channel: "we disabled your marketplace copy" is otherwise invisible, and Setup ran hidden.
    [Fact]
    public void Reports_a_marketplace_copy_we_disabled()
    {
        var state = Temp();
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\proj") });

        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), state);

        Assert.Contains("flaui-mcp@flaui-mcp", text);
        Assert.Contains(@"C:\proj", text);
        Assert.Contains("re-enabled if you uninstall", text);
    }

    [Fact]
    public void Says_nothing_about_collisions_when_there_are_none()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp());
        Assert.DoesNotContain("flaui-mcp@flaui-mcp", text);
    }

    [Fact]
    public void Still_reports_the_agy_seed()
    {
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), Temp(), Temp());
        Assert.Contains("Seed driving skill (agy):", text);
    }
}
