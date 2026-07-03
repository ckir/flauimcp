// test/FlaUI.Mcp.Tests/Watch/EventCoalescerTests.cs
using System;
using System.Linq;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class EventCoalescerTests
{
    private static CapturedEventMeta Meta(string sub, WatchEventKind kind, string rid, DateTime t) =>
        new(sub, kind, SourceProcessId: 100, SourceRuntimeId: rid, TimestampUtc: t);

    [Fact]
    public void Same_structure_key_collapses_and_counts()
    {
        var c = new EventCoalescer(capacity: 256, debounceMs: 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        string key = "s1|structure_changed|w1";  // subscribedScope-based key
        Assert.Null(c.Offer(key, Meta("s1", WatchEventKind.StructureChanged, "r1", t0), t0));
        Assert.Null(c.Offer(key, Meta("s1", WatchEventKind.StructureChanged, "r2", t0.AddMilliseconds(30)), t0.AddMilliseconds(30)));
        Assert.Empty(c.Drain(t0.AddMilliseconds(50)));
        var ready = c.Drain(t0.AddMilliseconds(200));
        var agg = Assert.Single(ready);
        Assert.Equal(2, agg.CoalescedCount);
        Assert.Equal(WatchEventKind.StructureChanged, agg.Meta.Kind);
    }

    [Fact]
    public void Nonstructure_events_are_ready_immediately()
    {
        var c = new EventCoalescer(256, 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0);
        var ready = c.Drain(t0);   // no debounce for focus
        var agg = Assert.Single(ready);
        Assert.Equal(1, agg.CoalescedCount);
    }

    [Fact]
    public void Distinct_keys_stay_separate()
    {
        var c = new EventCoalescer(256, 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0);
        c.Offer("s1|focus_changed|r2", Meta("s1", WatchEventKind.FocusChanged, "r2", t0), t0);
        Assert.Equal(2, c.Drain(t0).Count);
    }

    [Fact]
    public void Overflow_evicts_oldest_distinct_key_and_reports_its_subscription()
    {
        var c = new EventCoalescer(capacity: 2, debounceMs: 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0));
        Assert.Null(c.Offer("s2|focus_changed|r2", Meta("s2", WatchEventKind.FocusChanged, "r2", t0.AddMilliseconds(1)), t0.AddMilliseconds(1)));
        var dropped = c.Offer("s3|focus_changed|r3", Meta("s3", WatchEventKind.FocusChanged, "r3", t0.AddMilliseconds(2)), t0.AddMilliseconds(2));
        Assert.Equal("s1", dropped);
        var keysLeft = c.Drain(t0.AddMilliseconds(3)).Select(a => a.Meta.SubscriptionId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "s2", "s3" }, keysLeft);
    }

    [Fact]
    public void Merging_into_existing_key_does_not_count_as_overflow()
    {
        var c = new EventCoalescer(capacity: 1, debounceMs: 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0));
        Assert.Null(c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0.AddMilliseconds(5)), t0.AddMilliseconds(5)));
        var agg = Assert.Single(c.Drain(t0.AddMilliseconds(6)));
        Assert.Equal(2, agg.CoalescedCount);
    }
}
