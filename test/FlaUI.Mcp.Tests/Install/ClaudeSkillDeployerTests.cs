using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeSkillDeployerTests
{
    private static string TempConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-claude-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// Writes exactly the tree Deploy() used to write, so the Remove_* tests keep a real target after
    /// Deploy() is gone. Deliberately NOT a re-implementation of Deploy(): these tests assert what
    /// Remove() DELETES, so the arrange only has to put files where Remove() looks.
    private static void SeedSkillTree(string claudeConfigDir)
    {
        // ⚠ HARDCODED, deliberately. This used to read `new ClaudeSkillDeployer(cfg).SkillRoot`, i.e. it
        // asked the code under test where to put the fixture — so breaking `PluginName` moved the fixture
        // AND the deletion together, and Remove_deletes_the_skill_tree (whose assertion hardcodes
        // "flaui-mcp") passed VACUOUSLY. MEASURED: under PluginName -> "wrong-path" that test stayed
        // GREEN; only a sibling's incidentally-hardcoded path caught the mutant, so a tidy-up of that
        // sibling would have made this whole file vacuous. A fixture must state the CONTRACT, never
        // derive it from the thing it is testing.
        var root = Path.Combine(claudeConfigDir, "skills", "flaui-mcp");
        var skillDir = Path.Combine(root, "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(skillDir);
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            "{\n  \"name\": \"flaui-mcp\",\n  \"version\": \"9.9.9\"\n}\n");
    }

    [Fact]
    public void Remove_deletes_the_skill_tree()
    {
        var cfg = TempConfigDir();
        var d = new ClaudeSkillDeployer(cfg);
        SeedSkillTree(cfg);

        var warning = d.Remove();

        Assert.Null(warning);
        Assert.False(Directory.Exists(Path.Combine(cfg, "skills", "flaui-mcp")));
    }

    [Fact]
    public void Remove_on_a_machine_that_never_had_it_is_a_silent_no_op()
        => Assert.Null(new ClaudeSkillDeployer(TempConfigDir()).Remove());

    [Fact]
    public void Remove_survives_an_undeletable_tree_and_says_so()
    {
        var cfg = TempConfigDir();
        var d = new ClaudeSkillDeployer(cfg);
        SeedSkillTree(cfg);
        var held = Path.Combine(cfg, "skills", "flaui-mcp", "held-open.txt");

        using (File.Create(held))
        {
            var warning = d.Remove();
            Assert.NotNull(warning);
            Assert.Contains("left behind", warning);
        }
    }

    // Nothing outside our own namespace may be touched.
    [Fact]
    public void Remove_leaves_other_skills_alone()
    {
        var cfg = TempConfigDir();
        var other = Path.Combine(cfg, "skills", "someone-elses-skill");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "SKILL.md"), "not ours");
        var d = new ClaudeSkillDeployer(cfg);
        SeedSkillTree(cfg);

        d.Remove();

        Assert.True(File.Exists(Path.Combine(other, "SKILL.md")), "an unrelated skill was destroyed");
    }
}
