using System.IO;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class PluginDistributionTests
{
    private static Assembly ServerAsm => typeof(PluginArtifactWriter).Assembly;

    public static TheoryData<string> RequiredResources() => new()
    {
        "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md",
        "FlaUI.Mcp.Server.seed.flaui-learn.SKILL.md",
        "FlaUI.Mcp.Server.seed.flaui-curate.SKILL.md",
        "FlaUI.Mcp.Server.seed.hooks.hooks.json",
        "FlaUI.Mcp.Server.seed.scripts.flaui-curate-nudge.sh",
    };

    [Theory]
    [MemberData(nameof(RequiredResources))]
    public void Every_shipped_plugin_artifact_is_embedded(string logicalName)
    {
        using var s = ServerAsm.GetManifestResourceStream(logicalName);
        Assert.True(s is not null,
            $"embedded resource '{logicalName}' missing. Available:\n" +
            string.Join("\n", ServerAsm.GetManifestResourceNames()));
    }

    private static string Stage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-stage-" + Path.GetRandomFileName());
        new PluginArtifactWriter(dir).Generate(@"C:\fake\flaui-mcp.exe", "9.9.9");
        return dir;
    }

    [Theory]
    [InlineData("hooks/hooks.json")]
    [InlineData("scripts/flaui-curate-nudge.sh")]
    [InlineData("skills/driving-flaui-mcp/SKILL.md")]
    [InlineData("skills/flaui-learn/SKILL.md")]
    [InlineData("skills/flaui-curate/SKILL.md")]
    public void Generate_stages_every_plugin_artifact(string relative)
    {
        var staged = Path.Combine(Stage(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(staged), $"Generate() did not stage {relative}");
    }

    [Fact]
    public void Staged_shell_scripts_contain_no_CRLF()
    {
        foreach (var sh in Directory.GetFiles(Path.Combine(Stage(), "scripts"), "*.sh"))
            Assert.DoesNotContain("\r", File.ReadAllText(sh));
    }

    [Theory]
    [InlineData("FlaUI.Mcp.Server.seed.scripts.flaui-curate-nudge.sh", "scripts/flaui-curate-nudge.sh")]
    [InlineData("FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md", "skills/driving-flaui-mcp/SKILL.md")]
    public void Staged_artifacts_are_byte_identical_to_their_embedded_source(string resource, string relative)
    {
        var staged = Path.Combine(Stage(), relative.Replace('/', Path.DirectorySeparatorChar));
        using var s = ServerAsm.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(ms.ToArray(), File.ReadAllBytes(staged));
    }

    [Fact]
    public void Staged_hooks_json_wires_SessionStart_to_the_installed_exe()
    {
        var json = File.ReadAllText(Path.Combine(Stage(), "hooks", "hooks.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        var command = sessionStart[0].GetProperty("hooks")[0].GetProperty("command").GetString();

        Assert.Contains(@"C:\fake\flaui-mcp.exe", command);
        Assert.Contains("activation-payload", command);
    }

    [Fact]
    public void Staged_hooks_json_preserves_the_existing_Stop_hook()
    {
        var json = File.ReadAllText(Path.Combine(Stage(), "hooks", "hooks.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("hooks").TryGetProperty("Stop", out _),
            "generation dropped the curate-nudge Stop hook");
    }

    /// Guards the promise in WriteHooksAndScripts' comment — that hooks added to the repo tree ship
    /// without touching that method. Exercised directly because the embedded hooks.json has no
    /// SessionStart today, so no end-to-end path can reach this case yet.
    [Fact]
    public void Merging_the_activation_hook_preserves_a_SessionStart_the_base_file_already_defines()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(
            """{"hooks":{"SessionStart":[{"matcher":"startup","hooks":[{"type":"command","command":"pre-existing"}]}]}}""")!;

        PluginArtifactWriter.MergeActivationHook(root, @"C:\fake\flaui-mcp.exe");

        var entries = root["hooks"]!["SessionStart"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Contains("pre-existing", entries[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Contains("activation-payload", entries[1]!["hooks"]![0]!["command"]!.GetValue<string>());
    }
}
