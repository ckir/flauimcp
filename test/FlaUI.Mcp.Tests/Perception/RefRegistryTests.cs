using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RefRegistryTests
{
    private static ElementDescriptor Desc(string aid) =>
        new(Array.Empty<int>(), ControlType.Button, aid, aid, null, Array.Empty<int>());

    [Fact]
    public void Register_assigns_sequential_refs_then_BeginSnapshot_keeps_the_counter_climbing()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        Assert.Equal("e1", r.Register("w1", Desc("a"), cached: null));
        Assert.Equal("e2", r.Register("w1", Desc("b"), cached: null));
        // New snapshot of w1 clears its refs but the counter does NOT reset, so
        // a stale held ref ("e1") can never silently alias a new element.
        r.BeginSnapshot("w1");
        Assert.Equal("e3", r.Register("w1", Desc("c"), cached: null));
    }

    [Fact]
    public void Refs_are_namespaced_per_window_handle()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        r.BeginSnapshot("w2");
        Assert.Equal("e1", r.Register("w1", Desc("a"), cached: null));
        Assert.Equal("e1", r.Register("w2", Desc("a"), cached: null)); // independent counter
    }

    [Fact]
    public void A_ref_from_a_superseded_snapshot_is_REF_NOT_FOUND()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        var stale = r.Register("w1", Desc("a"), cached: null); // "e1"
        r.BeginSnapshot("w1"); // supersedes — clears w1's map
        var ex = Assert.Throws<ToolException>(() => r.Lookup("w1", stale));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
        Assert.False(string.IsNullOrEmpty(ex.SuggestedRecovery));
    }

    [Fact]
    public void Lookup_of_unknown_window_is_REF_NOT_FOUND()
    {
        var r = new RefRegistry();
        var ex = Assert.Throws<ToolException>(() => r.Lookup("w9", "e1"));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
    }

    [Fact]
    public void BeginSnapshot_returns_a_window_scoped_snapshot_id()
    {
        var r = new RefRegistry();
        var id1 = r.BeginSnapshot("w1");
        var id2 = r.BeginSnapshot("w1");
        Assert.StartsWith("w1:", id1);
        Assert.NotEqual(id1, id2);
    }
}
