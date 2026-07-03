// test/FlaUI.Mcp.Tests/Watch/WatchPayloadBuilderTests.cs
using System;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WatchPayloadBuilderTests
{
    private sealed class FakeReader : IEventSourceReader
    {
        public bool HasSource { get; init; } = true;
        public bool IsPassword { get; init; }
        public string? ControlTypeValue { get; init; }
        public string? NameValue { get; init; }
        public int[]? BoundsValue { get; init; }
        public string? RefValue { get; init; }
        public string? ControlType => ControlTypeValue;
        public string? Name => NameValue;
        public int[]? Bounds => BoundsValue;
        public string? MintRef() => RefValue;
    }

    private static CapturedEventMeta Meta(WatchEventKind kind) => new(
        "s1", kind, 100, "r1", new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Builds_full_payload_from_present_source()
    {
        var reader = new FakeReader { ControlTypeValue = "Edit", NameValue = "Search",
            BoundsValue = new[] { 1, 2, 3, 4 }, RefValue = "e42" };
        var p = WatchPayloadBuilder.Build(Meta(WatchEventKind.FocusChanged), "w1", coalescedCount: 2, reader);
        Assert.Equal("s1", p.SubscriptionId);
        Assert.Equal("focus_changed", p.Event);
        Assert.Equal("w1", p.Window);
        Assert.Equal("e42", p.Ref);
        Assert.Equal("Edit", p.ControlType);
        Assert.Equal("Search", p.Name);
        Assert.Equal(new[] { 1, 2, 3, 4 }, p.Bounds);
        Assert.Equal(2, p.CoalescedCount);
        Assert.Equal("2026-07-03T10:00:00.000Z", p.TimestampUtc);
    }

    [Fact]
    public void Password_source_redacts_name_INV5()
    {
        var reader = new FakeReader { IsPassword = true, ControlTypeValue = "Edit",
            NameValue = "hunter2-NEVER-LEAK", RefValue = "e1" };
        var p = WatchPayloadBuilder.Build(Meta(WatchEventKind.FocusChanged), "w1", 1, reader);
        Assert.Equal("[REDACTED]", p.Name);
        Assert.DoesNotContain("hunter2", p.Name);
    }

    [Fact]
    public void Null_source_yields_null_ref_name_bounds_controltype()
    {
        var reader = new FakeReader { HasSource = false };
        var p = WatchPayloadBuilder.Build(Meta(WatchEventKind.WindowClosed), "w1", 1, reader);
        Assert.Null(p.Ref);
        Assert.Null(p.Name);
        Assert.Null(p.Bounds);
        Assert.Null(p.ControlType);
        Assert.Equal("window_closed", p.Event);
    }
}
