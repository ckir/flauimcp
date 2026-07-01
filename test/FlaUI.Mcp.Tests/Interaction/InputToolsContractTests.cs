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
}
