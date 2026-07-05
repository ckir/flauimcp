using System.Text.Json;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ForegroundGateWiringTests
{
    [Fact]
    public void TargetNotForeground_serializes_as_a_result_not_an_error()
    {
        var r = ForegroundGate.Evaluate(100, 200, "w1",
            resolveProcess: _ => "chrome", ownerHwnd: _ => 0)!;
        var json = ToolResponse.Ok(new { targetNotForeground = r });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("targetNotForeground", out var tnf));
        Assert.False(doc.RootElement.TryGetProperty("error", out _));      // NOT the error channel
        Assert.Equal("w1", tnf.GetProperty("targetWindow").GetString());
        Assert.Equal("call-wait-for-foreground", tnf.GetProperty("recommendedAction").GetString());
        var cf = tnf.GetProperty("currentForeground");
        Assert.Equal("chrome", cf.GetProperty("process").GetString());
    }
}
