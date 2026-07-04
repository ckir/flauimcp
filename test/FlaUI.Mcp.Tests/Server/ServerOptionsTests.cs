using FlaUI.Mcp.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class ServerOptionsTests
{
    [Fact]
    public void Defaults_overlay_off_and_500ms()
    {
        var o = ServerOptions.FromArgs(System.Array.Empty<string>());
        Assert.False(o.Overlay);
        Assert.Equal(500, o.OverlayMs);
    }

    [Fact]
    public void Overlay_flag_enables_it()
    {
        var o = ServerOptions.FromArgs(new[] { "--overlay" });
        Assert.True(o.Overlay);
        Assert.Equal(500, o.OverlayMs);
    }

    [Fact]
    public void OverlayMs_parses_and_zero_disables()
    {
        Assert.Equal(120, ServerOptions.FromArgs(new[] { "--overlay", "--overlay-ms=120" }).OverlayMs);
        Assert.Equal(0, ServerOptions.FromArgs(new[] { "--overlay", "--overlay-ms=0" }).OverlayMs);
    }

    [Fact]
    public void Negative_or_garbage_overlay_ms_clamps_to_zero()
    {
        Assert.Equal(0, ServerOptions.FromArgs(new[] { "--overlay-ms=-100" }).OverlayMs);
        Assert.Equal(0, ServerOptions.FromArgs(new[] { "--overlay-ms=notanumber" }).OverlayMs);
    }
}
