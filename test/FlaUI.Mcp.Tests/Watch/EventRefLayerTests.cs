// test/FlaUI.Mcp.Tests/Watch/EventRefLayerTests.cs
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Core.Definitions;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class EventRefLayerTests
{
    private static ElementDescriptor Desc() =>
        new(new[] { 1, 2 }, ControlType.Edit, "aid", "name", null, System.Array.Empty<int>());

    [Fact]
    public void Event_ref_is_resolvable_by_lookup_like_a_normal_ref()
    {
        var r = new RefRegistry();
        var @ref = r.RegisterEventRef("w1", Desc(), cached: null);
        Assert.StartsWith("e", @ref);
        var d = r.LookupDescriptor("w1", @ref);
        Assert.Equal("aid", d.AutomationId);
    }

    [Fact]
    public void Event_ref_layer_is_bounded_oldest_evicted_past_cap()
    {
        var r = new RefRegistry();
        var first = r.RegisterEventRef("w1", Desc(), null);
        for (int i = 0; i < RefRegistry.EventRefCap; i++) r.RegisterEventRef("w1", Desc(), null);
        Assert.Throws<ToolException>(() => r.LookupDescriptor("w1", first));
    }

    [Fact]
    public void EvictWindow_also_clears_event_refs()
    {
        var r = new RefRegistry();
        var @ref = r.RegisterEventRef("w1", Desc(), null);
        r.EvictWindow("w1");
        Assert.Throws<ToolException>(() => r.LookupDescriptor("w1", @ref));
    }

    [Fact]
    public void Event_ref_survives_BeginSnapshot_of_same_window()
    {
        var r = new RefRegistry();
        var @ref = r.RegisterEventRef("w1", Desc(), null);
        r.BeginSnapshot("w1");                       // wipes the DURABLE layer only
        var d = r.LookupDescriptor("w1", @ref);      // event ref still resolvable
        Assert.Equal("aid", d.AutomationId);
    }
}
