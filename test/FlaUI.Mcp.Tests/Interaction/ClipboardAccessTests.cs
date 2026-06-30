using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

[Trait("Category", "Desktop")]
public class ClipboardAccessTests
{
    [Fact]
    public async Task Set_then_get_round_trips_text()
    {
        var probe = "flaui-mcp-clip-" + System.Guid.NewGuid().ToString("N");
        await ClipboardAccess.SetTextAsync(probe);
        Assert.Equal(probe, await ClipboardAccess.GetTextAsync());
    }

    [Fact]
    public async Task Set_empty_then_get_returns_empty()
    {
        await ClipboardAccess.SetTextAsync("");
        Assert.Equal("", await ClipboardAccess.GetTextAsync());
    }
}
