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

    [Fact]
    public void EvictWindow_removes_all_refs_for_that_window()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        var e1 = r.Register("w1", Desc("a"), cached: null); // "e1"
        r.EvictWindow("w1");
        var ex = Assert.Throws<ToolException>(() => r.Lookup("w1", e1));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
    }

    [Fact]
    public void EvictWindow_leaves_other_windows_intact()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        r.BeginSnapshot("w2");
        var w1Ref = r.Register("w1", Desc("a"), cached: null);
        var w2Ref = r.Register("w2", Desc("b"), cached: null);
        r.EvictWindow("w1");
        Assert.Throws<ToolException>(() => r.Lookup("w1", w1Ref)); // w1 gone
        Assert.Equal("b", r.Lookup("w2", w2Ref).Descriptor.AutomationId); // w2 survives
    }

    [Fact]
    public void EvictWindow_is_idempotent_for_unknown_and_repeated_ids()
    {
        var r = new RefRegistry();
        r.EvictWindow("never-registered"); // no throw
        r.BeginSnapshot("w1");
        r.Register("w1", Desc("a"), cached: null);
        r.EvictWindow("w1");
        r.EvictWindow("w1"); // double-evict: still no throw
    }

    [Fact]
    public void After_EvictWindow_a_fresh_snapshot_of_that_window_starts_clean()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        r.Register("w1", Desc("a"), cached: null); // e1
        r.Register("w1", Desc("b"), cached: null); // e2
        r.EvictWindow("w1");
        // Counter dropped with the window: a fresh snapshot restarts refs from e1 (windowId "w1"
        // is never reused by WindowManager, so no live stale ref can alias this new e1).
        r.BeginSnapshot("w1");
        Assert.Equal("e1", r.Register("w1", Desc("c"), cached: null));
    }
}
