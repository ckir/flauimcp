// test/FlaUI.Mcp.Tests/Watch/WatchWireTests.cs
using System.Text.Json;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WatchWireTests
{
    [Theory]
    [InlineData("window_opened", WatchEventKind.WindowOpened)]
    [InlineData("window_closed", WatchEventKind.WindowClosed)]
    [InlineData("focus_changed", WatchEventKind.FocusChanged)]
    [InlineData("structure_changed", WatchEventKind.StructureChanged)]
    public void TryParse_roundtrips_wire_tokens(string token, WatchEventKind expected)
    {
        Assert.True(WatchEventKinds.TryParse(token, out var k));
        Assert.Equal(expected, k);
        Assert.Equal(token, WatchEventKinds.ToWire(k));
    }

    [Fact]
    public void TryParse_rejects_unknown_token()
    {
        Assert.False(WatchEventKinds.TryParse("property_changed", out _));
        Assert.False(WatchEventKinds.TryParse("", out _));
    }

    [Fact]
    public void Payload_serializes_required_keys_and_omits_null_optionals()
    {
        var p = new DesktopEventPayload(
            SubscriptionId: "s1", Event: "window_closed", Window: "w1",
            Ref: null, ControlType: null, Name: null, Bounds: null,
            CoalescedCount: 1, TimestampUtc: "2026-07-03T10:00:00.000Z");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(p));
        var root = doc.RootElement;
        Assert.Equal("s1", root.GetProperty("subscriptionId").GetString());
        Assert.Equal("window_closed", root.GetProperty("event").GetString());
        Assert.Equal("w1", root.GetProperty("window").GetString());
        Assert.Equal(1, root.GetProperty("coalescedCount").GetInt32());
        Assert.Equal("2026-07-03T10:00:00.000Z", root.GetProperty("timestampUtc").GetString());
        Assert.False(root.TryGetProperty("ref", out _));
        Assert.False(root.TryGetProperty("controlType", out _));
        Assert.False(root.TryGetProperty("name", out _));
        Assert.False(root.TryGetProperty("bounds", out _));
    }

    [Fact]
    public void Payload_emits_ref_and_bounds_when_present_under_wire_keys()
    {
        var p = new DesktopEventPayload("s1", "focus_changed", "w1",
            Ref: "e42", ControlType: "Edit", Name: "Search", Bounds: new[] { 1, 2, 3, 4 },
            CoalescedCount: 3, TimestampUtc: "2026-07-03T10:00:00.000Z");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(p));
        var root = doc.RootElement;
        Assert.Equal("e42", root.GetProperty("ref").GetString());
        Assert.Equal("Edit", root.GetProperty("controlType").GetString());
        Assert.Equal("Search", root.GetProperty("name").GetString());
        Assert.Equal(4, root.GetProperty("bounds").GetArrayLength());
        Assert.Equal(3, root.GetProperty("coalescedCount").GetInt32());
    }
}
