using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Pure env-parsing — no live UIA, runs headless.
public class RefResolveConfigTests
{
    [Theory]
    [InlineData(null, true)]     // default: strict ON
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("off", false)]   // break-glass
    [InlineData("OFF", false)]   // case-insensitive, trimmed
    public void StrictEnabled_maps_env(string? env, bool expected) =>
        Assert.Equal(expected, RefResolveConfig.StrictEnabled(env));

    [Theory]
    [InlineData(null, 512)]       // default
    [InlineData("1000", 1000)]    // valid override
    [InlineData("garbage", 512)]  // unparseable -> default
    [InlineData("0", 512)]        // non-positive -> default
    [InlineData("-5", 512)]       // negative -> default
    public void MaxScopes_parses_or_defaults(string? env, int expected) =>
        Assert.Equal(expected, RefResolveConfig.MaxScopes(env));

    // The kill-switch WIRING seam (QA seat): env -> the mode state-changing paths actually use.
    [Theory]
    [InlineData(null, RefResolveMode.Strict)]   // default: INV-8 on
    [InlineData("on", RefResolveMode.Strict)]
    [InlineData("off", RefResolveMode.Lenient)] // break-glass -> lenient
    public void WriteMode_honours_the_kill_switch(string? env, RefResolveMode expected) =>
        Assert.Equal(expected, RefResolveConfig.WriteMode(env));
}
