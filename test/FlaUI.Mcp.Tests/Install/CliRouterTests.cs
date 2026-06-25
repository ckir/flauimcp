using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class CliRouterTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("print-config")]
    [InlineData("--version")]
    [InlineData("--help")]
    public void IsInstallerVerb_true_for_known_verbs(string verb)
        => Assert.True(CliRouter.IsInstallerVerb(new[] { verb }));

    [Fact]
    public void IsInstallerVerb_false_for_no_args()
        => Assert.False(CliRouter.IsInstallerVerb(Array.Empty<string>()));

    [Fact]
    public void IsInstallerVerb_false_for_unknown_arg()
        => Assert.False(CliRouter.IsInstallerVerb(new[] { "--unexpected" }));

    [Fact]
    public void Version_prints_and_returns_zero()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "--version" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(0, code);
        Assert.Contains("flaui-mcp", sb.ToString());
    }

    [Fact]
    public void PrintConfig_generic_emits_snippet()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "print-config", "--agent", "generic" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(0, code);
        Assert.Contains("mcpServers", sb.ToString());
    }
}
