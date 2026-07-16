using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class AgySkillDeployTests
{
    private static (string servers, string perms, string plugins) TempPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-agy-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "settings.json"), Path.Combine(dir, "perms.json"), Path.Combine(dir, "plugins"));
    }

    [Fact]
    public void Install_deploys_agy_driving_skill_plugin()
    {
        var (servers, perms, plugins) = TempPaths();
        var w = new AgyConfigWriter(servers, perms, plugins);

        w.Install(@"C:\flaui-mcp.exe");

        var pluginJson = Path.Combine(plugins, "flaui-mcp", "plugin.json");
        var skill = Path.Combine(plugins, "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md");
        Assert.True(File.Exists(pluginJson), "plugin.json deployed");
        Assert.True(File.Exists(skill), "SKILL.md deployed");
        Assert.Contains("\"name\": \"flaui-mcp\"", File.ReadAllText(pluginJson));
        Assert.Contains("Driving FlaUI.Mcp", File.ReadAllText(skill)); // seed content marker
    }

    // The seed skill rides along with the registration; it is not the point of it. If the skill
    // cannot land, the user must still end up with a working, registered server — and must not have
    // to guess that the skill went missing.
    [Fact]
    public void Skill_deploy_failure_warns_but_still_registers_the_server()
    {
        var (servers, perms, plugins) = TempPaths();
        Directory.CreateDirectory(plugins);
        // A file squatting where the plugin root directory has to go: CreateDirectory cannot win this.
        File.WriteAllText(Path.Combine(plugins, "flaui-mcp"), "blocker");

        var r = new AgyConfigWriter(servers, perms, plugins).Install(@"C:\flaui-mcp.exe");

        Assert.Equal(AgentChange.Created, r.Change);           // registration succeeded ...
        Assert.NotNull(r.Warning);                             // ... and the shortfall is reported
        Assert.Contains("seed driving skill not deployed", r.Warning);
        Assert.Contains("flaui-mcp", File.ReadAllText(servers));
    }

    [Fact]
    public void Successful_install_reports_the_skill_directory()
    {
        var (servers, perms, plugins) = TempPaths();

        var r = new AgyConfigWriter(servers, perms, plugins).Install(@"C:\flaui-mcp.exe");

        Assert.Null(r.Warning);
        Assert.Contains(Path.Combine(plugins, "flaui-mcp"), r.Detail);
    }

    [Fact]
    public void Uninstall_removes_the_agy_plugin_folder()
    {
        var (servers, perms, plugins) = TempPaths();
        var w = new AgyConfigWriter(servers, perms, plugins);
        w.Install(@"C:\flaui-mcp.exe");
        Assert.True(Directory.Exists(Path.Combine(plugins, "flaui-mcp")));

        var r = w.Uninstall();

        Assert.False(Directory.Exists(Path.Combine(plugins, "flaui-mcp")), "plugin folder removed");
        Assert.Null(r.Warning);
    }

    // A skill dir we cannot delete (here: a file inside it held open) must not derail the uninstall —
    // the registration still has to go, or the user is left with an agent pointing at a deleted exe.
    [Fact]
    public void Uninstall_survives_an_undeletable_skill_dir_and_says_so()
    {
        var (servers, perms, plugins) = TempPaths();
        var w = new AgyConfigWriter(servers, perms, plugins);
        w.Install(@"C:\flaui-mcp.exe");
        var locked = Path.Combine(plugins, "flaui-mcp", "held-open.txt");

        using (File.Create(locked))          // still open across the Uninstall call
        {
            var r = w.Uninstall();

            Assert.Equal(AgentChange.Removed, r.Change);       // registration reverted ...
            Assert.NotNull(r.Warning);                         // ... and the leftover is reported
            Assert.Contains("left behind", r.Warning);
            Assert.DoesNotContain("flaui-mcp", File.ReadAllText(servers));
        }
    }
}
