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
}
