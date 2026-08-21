using System.Linq;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureResultShapeTests
{
    // CaptureResult is a POSITIONAL record constructed positionally. A field added mid-list silently
    // rebinds arguments wherever the types happen to line up -- int X, int Y, int W, int H are four
    // interchangeable ints. This pins the ORDER, so an insertion fails a test instead of shipping.
    [Fact]
    public void The_positional_order_is_append_only()
    {
        var ctor = typeof(CaptureResult).GetConstructors().Single();
        var names = ctor.GetParameters().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "Png", "X", "Y", "W", "H", "ScaleApplied", "Redactions",
            "CaptureMethod", "CaptureWarnings",
        }, names);
    }

    [Fact]
    public void CaptureWarnings_is_never_null_when_constructed_empty()
    {
        var r = new CaptureResult(System.Array.Empty<byte>(), 0, 0, 1, 1, 1.0, 0,
                                  "printWindow", System.Array.Empty<CaptureWarning>());
        Assert.NotNull(r.CaptureWarnings);
        Assert.Empty(r.CaptureWarnings);
    }
}
