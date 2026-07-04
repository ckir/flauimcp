using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

// Headless: the sentinel class name is a single shared constant; the enum/popup filters skip it.
// A live end-to-end "overlay never appears in list_windows" check is a Desktop/controller smoke.
public class OverlayFilterTests
{
    [Fact]
    public void Sentinel_class_name_is_the_shared_constant()
    {
        Assert.Equal("FlaUiMcpIntentOverlay", OverlaySentinel.ClassName);
    }

    [Fact]
    public void Enum_predicate_skips_the_sentinel_class()
    {
        Assert.True(WindowManager.ShouldSkipTopLevel(OverlaySentinel.ClassName));
        Assert.False(WindowManager.ShouldSkipTopLevel("Notepad"));
        Assert.False(WindowManager.ShouldSkipTopLevel(""));
    }
}
