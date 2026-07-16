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

    [Fact]
    public void Deploy_writes_the_manifest_and_the_skill()
    {
        var cfg = TempConfigDir();

        var warning = new ClaudeSkillDeployer(cfg).Deploy();

        Assert.Null(warning);
        var root = Path.Combine(cfg, "skills", "flaui-mcp");
        Assert.True(File.Exists(Path.Combine(root, ".claude-plugin", "plugin.json")), "manifest deployed");
        Assert.True(File.Exists(Path.Combine(root, "skills", "driving-flaui-mcp", "SKILL.md")), "skill deployed");
    }

    // The manifest goes in .claude-plugin/ — an agy-shaped bare plugin.json at the root (what
    // AgyConfigWriter writes) is NOT a Claude Code plugin manifest.
    [Fact]
    public void The_manifest_is_claude_shaped_and_carries_the_assembly_version()
    {
        var cfg = TempConfigDir();
        new ClaudeSkillDeployer(cfg).Deploy();

        var json = File.ReadAllText(Path.Combine(cfg, "skills", "flaui-mcp", ".claude-plugin", "plugin.json"));

        Assert.Contains("\"name\": \"flaui-mcp\"", json);
        var av = typeof(ClaudeSkillDeployer).Assembly.GetName().Version!;
        Assert.Contains($"\"version\": \"{av.Major}.{av.Minor}.{av.Build}\"", json);   // 3-part semver, not 4
        Assert.False(File.Exists(Path.Combine(cfg, "skills", "flaui-mcp", "plugin.json")), "no agy-shaped root manifest");
    }

    [Fact]
    public void The_deployed_skill_is_the_embedded_seed()
    {
        var cfg = TempConfigDir();
        new ClaudeSkillDeployer(cfg).Deploy();

        var skill = File.ReadAllText(Path.Combine(cfg, "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md"));
        Assert.Contains("Driving FlaUI.Mcp", skill);
    }

    // Versioned product, not user state: a re-install must overwrite, or a user upgrading keeps a
    // drifted skill describing tools their new binary no longer has.
    [Fact]
    public void Deploy_is_idempotent_and_overwrites_a_stale_skill()
    {
        var cfg = TempConfigDir();
        var d = new ClaudeSkillDeployer(cfg);
        d.Deploy();
        var skill = Path.Combine(cfg, "skills", "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md");
        // Unique sentinel: the real seed legitimately contains "STALE" (the error token
        // REF_STALE_UNRESOLVABLE), so a "STALE"-absence assertion would false-fail. "QWERTYUIOP" cannot
        // appear in the seed (verified). We assert both directions: the stale body is gone AND the real
        // seed is back.
        File.WriteAllText(skill, "QWERTYUIOP outdated v0.14 skill body");

        var warning = d.Deploy();

        Assert.Null(warning);
        var body = File.ReadAllText(skill);
        Assert.DoesNotContain("QWERTYUIOP", body);     // the stale body is gone
        Assert.Contains("Driving FlaUI.Mcp", body);    // ...replaced by the real embedded seed
    }

    // Same policy as the agy path: the skill rides along with the registration and must never deny
    // the user a working server. It reports, it does not throw.
    [Fact]
    public void Deploy_failure_returns_a_warning_and_never_throws()
    {
        var cfg = TempConfigDir();
        Directory.CreateDirectory(Path.Combine(cfg, "skills"));
        File.WriteAllText(Path.Combine(cfg, "skills", "flaui-mcp"), "blocker");   // a file where the dir must go

        var warning = new ClaudeSkillDeployer(cfg).Deploy();

        Assert.NotNull(warning);
        Assert.Contains("driving skill not deployed", warning);
    }

    [Fact]
    public void Remove_deletes_the_skill_tree()
    {
        var cfg = TempConfigDir();
        var d = new ClaudeSkillDeployer(cfg);
        d.Deploy();

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
        d.Deploy();
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
        d.Deploy();

        d.Remove();

        Assert.True(File.Exists(Path.Combine(other, "SKILL.md")), "an unrelated skill was destroyed");
    }
}
