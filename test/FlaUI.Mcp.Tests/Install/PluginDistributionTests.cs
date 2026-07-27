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
}
