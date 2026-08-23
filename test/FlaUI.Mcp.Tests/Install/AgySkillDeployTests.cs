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
    public void Uninstall_removes_the_agy_plugin_folder()
    {
        var (servers, perms, plugins) = TempPaths();
        var w = new AgyConfigWriter(servers, perms, plugins);
        AgyFixtures.SeedAgyInstall(servers, perms, plugins);
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
        AgyFixtures.SeedAgyInstall(servers, perms, plugins);
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
