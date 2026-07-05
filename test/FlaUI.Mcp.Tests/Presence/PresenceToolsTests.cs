using System.Text.Json;
using FlaUI.Mcp.Core.Presence;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class PresenceToolsTests
{
    [Fact]
    public void Disabled_returns_enabled_false_activity_null()
    {
        var json = PresenceTools.Reply(new PresenceConfig(false, 60, 300), idleMs: 0);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("activity").ValueKind);
    }

    [Fact]
    public void Enabled_returns_the_coarse_enum_never_raw_ms()
    {
        var json = PresenceTools.Reply(new PresenceConfig(true, 60, 300), idleMs: 120_000);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("nearby", doc.RootElement.GetProperty("activity").GetString());
        Assert.False(doc.RootElement.TryGetProperty("idleMs", out _));   // NEVER raw ms
    }

    [Fact]
    public void Shape_is_invariant_same_keys_both_states()
    {
        var on = JsonDocument.Parse(PresenceTools.Reply(new PresenceConfig(true, 60, 300), 0)).RootElement;
        var off = JsonDocument.Parse(PresenceTools.Reply(new PresenceConfig(false, 60, 300), 0)).RootElement;
        Assert.True(on.TryGetProperty("enabled", out _) && on.TryGetProperty("activity", out _));
        Assert.True(off.TryGetProperty("enabled", out _) && off.TryGetProperty("activity", out _));
    }
}
