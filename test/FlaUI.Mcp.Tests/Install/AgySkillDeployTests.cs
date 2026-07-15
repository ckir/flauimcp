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

    [Fact]
    public void Uninstall_removes_the_agy_plugin_folder()
    {
        var (servers, perms, plugins) = TempPaths();
        var w = new AgyConfigWriter(servers, perms, plugins);
        w.Install(@"C:\flaui-mcp.exe");
        Assert.True(Directory.Exists(Path.Combine(plugins, "flaui-mcp")));

        w.Uninstall();

        Assert.False(Directory.Exists(Path.Combine(plugins, "flaui-mcp")), "plugin folder removed");
    }
}
