using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP0 layer 1: pins the RECORD shapes SP1 is about to extend. Headless, CI-gated.
/// The tool-layer projection is pinned separately in ToolProjectionShapeTests (Desktop) —
/// a record pin cannot see the anonymous projection, which is where fields actually get dropped.</summary>
public class WaitContractShapeTests
{
    [Fact]
    public void WaitForResult_carries_its_four_established_members()
    {
        var r = new WaitForResult(Satisfied: true, Ref: "e7", ElapsedMs: 42, SnapshotId: "w1:3");

        Assert.True(r.Satisfied);
        Assert.Equal("e7", r.Ref);
        Assert.Equal(42, r.ElapsedMs);
        Assert.Equal("w1:3", r.SnapshotId);
    }

    [Fact]
    public void WaitForResult_allows_null_ref_and_snapshotId_on_an_unsatisfied_result()
    {
        // A timeout returns nulls, NOT omitted members. SP1's new fields follow this same
        // convention, so it is pinned here rather than assumed.
        var r = new WaitForResult(Satisfied: false, Ref: null, ElapsedMs: 5000, SnapshotId: null);

        Assert.False(r.Satisfied);
        Assert.Null(r.Ref);
        Assert.Null(r.SnapshotId);
    }

    [Fact]
    public void TextReadResult_carries_truncatedFrom()
    {
        var t = new TextReadResult("abc", Truncated: true, IsPassword: false, TruncatedFrom: "head");

        Assert.Equal("abc", t.Text);
        Assert.True(t.Truncated);
        Assert.False(t.IsPassword);
        Assert.Equal("head", t.TruncatedFrom);
    }
}
