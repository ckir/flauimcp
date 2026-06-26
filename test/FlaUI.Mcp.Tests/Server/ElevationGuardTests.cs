using FlaUI.Mcp.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

// Pure decision seam — no real elevation/desktop needed, runs in the headless CI subset.
public class ElevationGuardTests
{
    [Fact]
    public void Writes_a_stderr_warning_when_elevated()
    {
        var sw = new StringWriter();
        bool wrote = ElevationGuard.WarnIfElevated(isElevated: true, sw);
        Assert.True(wrote);
        Assert.Contains("WARNING", sw.ToString());
        Assert.Contains("elevated", sw.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Is_silent_when_not_elevated()
    {
        var sw = new StringWriter();
        bool wrote = ElevationGuard.WarnIfElevated(isElevated: false, sw);
        Assert.False(wrote);
        Assert.Equal("", sw.ToString());
    }
}
