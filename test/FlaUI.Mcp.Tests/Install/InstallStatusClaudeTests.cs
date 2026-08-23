using System.IO;
using System.Linq;
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
        var legacyDir = Path.Combine(claude, "skills", "flaui-mcp", "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");

        var active = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp(), ClaudePluginStatus.Active);
        Assert.Contains("deployed as plugin (flaui-mcp@flaui-mcp-marketplace)", active);

        var notRegistered = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp(), ClaudePluginStatus.NotRegistered);
        Assert.Contains("NOT deployed", notRegistered);
    }

    /// AGY-CAPSTONE round 3. The residue note used to key on `skills/driving-flaui-mcp/SKILL.md`, which
    /// asks the wrong question: `ClaudeSkillDeployer.Remove()` deletes the root RECURSIVELY with no
    /// ordering guarantee, so a partial failure can take SKILL.md and still leave
    /// `.claude-plugin/plugin.json` — residue that keeps registering a plugin named `flaui-mcp` while the
    /// note went SILENT because the one leaf it watched was gone.
    ///
    /// This is the shape that fixture writes: root present, plugin.json present, NO SKILL.md.
    [Fact]
    public void A_partially_deleted_legacy_dir_is_still_reported_even_without_its_SKILL_md()
    {
        var claude = Temp();
        var legacyRoot = Path.Combine(claude, "skills", "flaui-mcp");
        Directory.CreateDirectory(Path.Combine(legacyRoot, ".claude-plugin"));
        File.WriteAllText(Path.Combine(legacyRoot, ".claude-plugin", "plugin.json"),
            "{\n  \"name\": \"flaui-mcp\"\n}\n");
        Assert.False(File.Exists(Path.Combine(legacyRoot, "skills", "driving-flaui-mcp", "SKILL.md")),
            "fixture must NOT create SKILL.md - that is the whole point of this case");

        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp(), ClaudePluginStatus.Active);

        Assert.Contains("retired plugin manifest", text);
        Assert.Contains(legacyRoot, text);
    }

    /// ⚠⚠ A REGRESSION GATE FOR A DEFECT INTRODUCED TWICE, TWO CAPSTONE ROUNDS APART.
    ///
    /// The residue note is appended to ALL FOUR branches of the status switch, including
    /// `NotRegistered` — where no plugin is installed, so the leftover manifest would be the FIRST and
    /// only plugin Claude loads, not a second one. The wording said "a SECOND copy", that was folded,
    /// and two rounds later a rewrite said "a second flaui-mcp plugin" and reintroduced it.
    ///
    /// A comment did not prevent the second occurrence. This test does: any count word in that sentence
    /// fails it, in the one branch where a count is provably wrong.
    [Fact]
    public void Status_never_claims_a_count_for_the_legacy_residue()
    {
        var claude = Temp();
        var legacyRoot = Path.Combine(claude, "skills", "flaui-mcp");
        Directory.CreateDirectory(Path.Combine(legacyRoot, ".claude-plugin"));
        File.WriteAllText(Path.Combine(legacyRoot, ".claude-plugin", "plugin.json"), "{ \"name\": \"flaui-mcp\" }");

        // NotRegistered is the branch that makes a count claim FALSE: nothing else is installed.
        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp(), ClaudePluginStatus.NotRegistered);

        // ⚠ SCOPED TO THE NOTE'S OWN LINE, not the whole report. Asserting over all of Describe(...)
        // would fail spuriously the day someone adds "took 2 seconds" or "no additional configuration
        // required" anywhere else in it — and MEASURED, a bare substring test does trip on "seconds".
        // Word boundaries for the same reason.
        var noteLine = text.Split('\n').Single(l => l.Contains("retired plugin manifest", StringComparison.Ordinal));

        // ⚠ HONEST LIMIT: this is a BLACKLIST, not a semantic check. It cannot catch every way of
        // implying a count - "a 2nd plugin", "a pair of plugins" and the like would slip through. It is
        // sized to the recurrence that actually happened twice, and the comment above the string in
        // InstallStatus.cs carries the reasoning. Do not mistake a green here for proof of neutrality.
        foreach (var countWord in new[] { "second", "2nd", "duplicate", "another", "additional", "two", "pair", "extra" })
            Assert.DoesNotMatch($@"(?i)\b{countWord}\b", noteLine);
    }

    /// The INVERSE half, and it is why the check keys on the manifest rather than on the directory.
    /// A delete that removed every file but could not unlink the directory leaves an EMPTY dir, which
    /// loads nothing at all. Warning about that is crying wolf, and a status command that cries wolf
    /// trains operators to ignore the one message that matters.
    ///
    /// Together with the test above this pins BOTH directions: manifest present -> warn; nothing that
    /// can load -> stay silent. Neither test alone would catch a regression to `Directory.Exists`.
    [Fact]
    public void An_empty_leftover_legacy_dir_is_not_reported_as_a_collision()
    {
        var claude = Temp();
        var legacyRoot = Path.Combine(claude, "skills", "flaui-mcp");
        Directory.CreateDirectory(legacyRoot);   // exists, but holds nothing Claude can load

        var text = InstallStatus.Describe(@"C:\x.exe", Temp(), Temp(), claude, Temp(), ClaudePluginStatus.Active);

        Assert.DoesNotContain("retired plugin manifest", text);
        Assert.DoesNotContain(legacyRoot, text);
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
