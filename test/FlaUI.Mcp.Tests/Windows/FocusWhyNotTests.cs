using System.Text.Json;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

public class FocusWhyNotTests
{
    [Fact]
    public void Gained_reply_has_no_whynot()
    {
        var json = WindowTools.FocusReply(new FocusResult(true, 100, 100), "w1",
            _ => "notepad", _ => 0, _ => null);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("foregroundGained").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("currentForeground", out _));
    }

    [Fact]
    public void Not_gained_reply_carries_leaksafe_whynot()
    {
        var json = WindowTools.FocusReply(new FocusResult(false, 100, 200), "w1",
            h => h == 200 ? "chrome" : "notepad", _ => 0, _ => null);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("foregroundGained").GetBoolean());
        var cf = doc.RootElement.GetProperty("currentForeground");
        Assert.Equal("chrome", cf.GetProperty("process").GetString());
        Assert.Equal("call-wait-for-foreground", doc.RootElement.GetProperty("recommendedAction").GetString());
    }
}
