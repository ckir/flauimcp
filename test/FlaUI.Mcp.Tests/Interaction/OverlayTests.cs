using System.Threading.Tasks;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class OverlayTests
{
    [Fact]
    public void TokenGate_only_current_owner_may_hide()
    {
        var g = new OverlayTokenGate();
        long t1 = g.Next();
        Assert.True(g.OwnsCurrent(t1));
        long t2 = g.Next();                 // a later action supersedes
        Assert.False(g.OwnsCurrent(t1));    // stale hide from t1 is rejected
        Assert.True(g.OwnsCurrent(t2));     // newest owner may hide
    }

    [Fact]
    public async Task NullActionOverlay_is_disabled_and_no_ops()
    {
        var o = NullActionOverlay.Instance;
        Assert.False(o.Enabled);
        await o.PreviewAsync(new OverlayRect(0, 0, 100, 100)); // completes immediately, no throw
    }

    [Fact]
    public void OverlayRect_degenerate_is_detectable()
    {
        Assert.True(new OverlayRect(0, 0, 0, 10).IsDegenerate);
        Assert.True(new OverlayRect(0, 0, 10, -1).IsDegenerate);
        Assert.False(new OverlayRect(-1920, 0, 500, 500).IsDegenerate);
    }
}
