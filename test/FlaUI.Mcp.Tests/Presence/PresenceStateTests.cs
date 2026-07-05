using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class PresenceStateTests
{
    [Fact]
    public void Round_trips_enabled_and_thresholds()
    {
        var line = PresenceConfig.Format(new PresenceConfig(true, 60, 300));
        Assert.True(PresenceConfig.TryParse(line, out var c));
        Assert.True(c.Enabled);
        Assert.Equal(60, c.NearbySecs);
        Assert.Equal(300, c.AwaySecs);
    }

    [Fact]
    public void Off_line_parses_as_disabled()
    {
        Assert.True(PresenceConfig.TryParse("enabled=0;nearbySecs=60;awaySecs=300", out var c));
        Assert.False(c.Enabled);
    }

    [Fact]
    public void Garbage_fails_to_parse()
        => Assert.False(PresenceConfig.TryParse("not-a-line", out _));
}
