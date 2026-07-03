using System.Linq;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Headless (NOT Category=Desktop): pure reflection over the tool signature, no SendInput.
public class InputToolsContractTests
{
    [Fact]
    public void DesktopType_defaults_the_inter_key_delay_to_15ms()
    {
        var p = typeof(InputTools).GetMethod(nameof(InputTools.DesktopType))!
            .GetParameters().Single(x => x.Name == "interKeyDelayMs");
        Assert.Equal(15, p.DefaultValue);
    }

    [Fact]
    public void DesktopType_defaults_verify_to_true()
    {
        var p = typeof(InputTools).GetMethod(nameof(InputTools.DesktopType))!
            .GetParameters().Single(x => x.Name == "verify");
        Assert.Equal(true, p.DefaultValue);
    }

    [Fact]
    public void DesktopPasteText_is_declared_Destructive_with_expected_params()
    {
        var m = typeof(InputTools).GetMethod("DesktopPasteText");
        Assert.NotNull(m);
        var names = m!.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("forceOverwriteClipboard", names);
        Assert.Contains("verify", names);
    }
}
