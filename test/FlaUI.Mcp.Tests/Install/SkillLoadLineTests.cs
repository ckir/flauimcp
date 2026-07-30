using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FlaUI.Mcp.Server.Install;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

public class SkillLoadLineTests
{
    private const string BuildInput = ".claude/skills/driving-flaui-mcp/SKILL.md";
    private const string RepoTwin   = "plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md";

    private static string Read(string rel) => File.ReadAllText(RepoPaths.At(rel.Split('/')));

    public static TheoryData<string> BothCopies() => new() { BuildInput, RepoTwin };

    /// Every bare tool name the server actually exposes, from live [McpServerTool] reflection.
    private static HashSet<string> LiveToolNames()
        => typeof(WindowTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
            .Select(ToolName)
            .ToHashSet(StringComparer.Ordinal);

    // DesktopListWindows -> desktop_list_windows
    private static string ToolName(MethodInfo m)
        => Regex.Replace(m.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    /// Anchored on `mcp__` so PROSE mentioning the `select:` filter is not parsed as a tool list.
    /// Without that anchor, documenting the mechanism in the skill breaks the test that reads it.
    private static IEnumerable<string> SelectNames(string markdown)
        => Regex.Matches(markdown, @"select:(mcp__[^""\r\n]+)")
                .SelectMany(mt => mt.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim());

    /// The G3 defect: the shipped line listed ONLY bare-prefixed names, so under plugin registration it
    /// matched nothing. Two CONSECUTIVE bare-prefixed names is that broken shape — in the fixed line
    /// every bare name is followed by its plugin-prefixed twin.
    ///
    /// Do NOT "simplify" this to Assert.DoesNotContain("select:mcp__flaui-mcp__", ...). That assertion is
    /// unsatisfiable against a correct load line: the bare form leads each pair, so the fixed line starts
    /// with exactly that substring. It would only pass if the pairs were reordered plugin-first — making
    /// the test green for a cosmetic reason unrelated to the defect, and red again on any future reorder.
    [Theory]
    [MemberData(nameof(BothCopies))]
    public void No_unpaired_run_of_bare_prefixed_names_remains(string rel)
        => Assert.False(
            Regex.IsMatch(Read(rel), @"mcp__flaui-mcp__desktop_\w+\s*,\s*mcp__flaui-mcp__"),
            $"{rel}: two consecutive bare-prefixed names — that is the broken single-prefix form (G3)");

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Every_named_tool_is_derivable_from_live_reflection(string rel)
    {
        var live = LiveToolNames();
        var bad = SelectNames(Read(rel)).Where(n =>
        {
            var bare = n.StartsWith("mcp__plugin_flaui-mcp_flaui-mcp__") ? n["mcp__plugin_flaui-mcp_flaui-mcp__".Length..]
                     : n.StartsWith("mcp__flaui-mcp__")                  ? n["mcp__flaui-mcp__".Length..]
                     : null;
            return bare is null || !live.Contains(bare);
        }).ToList();

        Assert.True(bad.Count == 0,
            $"{rel}: load-line names not derivable from a live [McpServerTool]:\n" + string.Join("\n", bad));
    }

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Both_prefix_forms_are_present_for_every_named_tool(string rel)
    {
        var names = SelectNames(Read(rel)).ToHashSet(StringComparer.Ordinal);
        var missing = names
            .Where(n => n.StartsWith("mcp__flaui-mcp__"))
            .Select(n => "mcp__plugin_flaui-mcp_flaui-mcp__" + n["mcp__flaui-mcp__".Length..])
            .Where(expected => !names.Contains(expected))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{rel}: plugin-prefixed twin missing for:\n" + string.Join("\n", missing));
    }

    /// Positive EXISTENCE check, deliberately alongside the implication test above.
    /// `Both_prefix_forms_are_present_for_every_named_tool` asks "does every bare name have a plugin
    /// twin?" — vacuously TRUE if every bare name is deleted, which would break DIRECT (non-plugin)
    /// registration while all three load-line tests stayed green. This pins both families explicitly.
    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Both_prefix_families_are_present_for_all_five_tools(string rel)
    {
        var names = SelectNames(Read(rel)).ToHashSet(StringComparer.Ordinal);
        var absent = new[] { "list_windows", "open_window", "snapshot", "get_text", "input_status" }
            .SelectMany(t => new[]
            {
                "mcp__flaui-mcp__desktop_" + t,
                "mcp__plugin_flaui-mcp_flaui-mcp__desktop_" + t,
            })
            .Where(n => !names.Contains(n))
            .ToList();

        Assert.True(absent.Count == 0, $"{rel}: load line is missing:\n" + string.Join("\n", absent));
    }

    [Fact]
    public void The_two_tracked_copies_are_byte_identical()
        => Assert.Equal(
            File.ReadAllBytes(RepoPaths.At(BuildInput.Split('/'))),
            File.ReadAllBytes(RepoPaths.At(RepoTwin.Split('/'))));

    /// <summary>EVERY twinned skill, not just driving-flaui-mcp. This project ships three skills in two
    /// tracked copies each (`.claude/skills/` and `plugins/flaui-mcp/skills/`), but only the driving pair was
    /// pinned — the other two matched by discipline alone, so editing one copy and forgetting the other
    /// produced no failure anywhere. Found while adding a commit step to flaui-learn: the fix itself would
    /// have been unprotected against exactly the drift it had to avoid.
    ///
    /// Enumerated from the DIRECTORY rather than a hard-coded list, so a fourth skill inherits the pin for
    /// free instead of silently opting out of it.</summary>
    [Fact]
    public void Every_twinned_skill_has_byte_identical_copies()
    {
        var claudeSkills = new DirectoryInfo(RepoPaths.At(".claude", "skills"));
        var pluginSkills = new DirectoryInfo(RepoPaths.At("plugins", "flaui-mcp", "skills"));

        var drifted = new List<string>();
        foreach (var dir in claudeSkills.GetDirectories())
        {
            var a = Path.Combine(dir.FullName, "SKILL.md");
            var b = Path.Combine(pluginSkills.FullName, dir.Name, "SKILL.md");
            if (!File.Exists(a) || !File.Exists(b)) continue; // not a twinned skill
            if (!File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)))
                drifted.Add(dir.Name);
        }

        Assert.True(drifted.Count == 0,
            "these skills' two tracked copies have DRIFTED — edit both or neither: " + string.Join(", ", drifted));
    }

    private static string Frontmatter(string rel)
    {
        var text = Read(rel);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        Assert.True(end > 0, $"{rel}: no closing frontmatter fence");
        return text[..end];
    }

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Frontmatter_does_not_embed_the_over_indexing_process_token(string rel)
        => Assert.DoesNotContain("Get-Process", Frontmatter(rel), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Frontmatter_is_decision_point_shaped_not_mechanism_shaped(string rel)
    {
        var fm = Frontmatter(rel);
        Assert.DoesNotContain("Use when driving or dogfooding", fm);
        // Four trigger surfaces the description must name: perception, liveness, background console,
        // actuation. "screen" (not "on screen") — the description reads "on the Windows screen", and a
        // bigram check would force the prose to contort to satisfy the test rather than the reverse.
        foreach (var cue in new[] { "screen", "running", "terminal", "click" })
            Assert.Contains(cue, fm, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Frontmatter_description_stays_within_budget(string rel)
    {
        var desc = Frontmatter(rel).Split('\n').First(l => l.StartsWith("description:", StringComparison.Ordinal));
        Assert.True(desc.Length <= 600, $"{rel}: description is {desc.Length} chars (budget 600)");
    }
}
