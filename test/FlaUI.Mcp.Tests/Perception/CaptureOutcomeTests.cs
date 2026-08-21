using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureOutcomeTests
{
    [Fact]
    public void A_completed_outcome_carries_the_result()
    {
        var r = new CaptureResult(System.Array.Empty<byte>(), 1, 2, 3, 4, 1.0, 0,
                                  "printWindow", System.Array.Empty<CaptureWarning>());
        var o = CaptureOutcome.Completed(r);
        Assert.Equal(CaptureOutcomeKind.Completed, o.Kind);
        Assert.Same(r, o.Result);
    }

    // The signals carry NO payload, deliberately. An earlier design had "resized" carry the observed W2
    // "for the next attempt" -- but retrying means a fresh UIA walk, which discovers the new geometry
    // itself and has no input for a rectangle the previous attempt measured. A contract requiring data
    // its only consumer discards is a contract that will drift.
    [Fact]
    public void The_resized_signal_carries_no_payload()
    {
        Assert.Equal(CaptureOutcomeKind.Resized, CaptureOutcome.Resized.Kind);
        Assert.Null(CaptureOutcome.Resized.Result);
    }

    [Fact]
    public void The_timed_out_signal_exists_and_carries_no_payload()
    {
        Assert.Equal(CaptureOutcomeKind.TimedOut, CaptureOutcome.TimedOut.Kind);
        Assert.Null(CaptureOutcome.TimedOut.Result);
    }

    // Without a TimedOut case the risk-2 scrape fallback is UNREACHABLE: the caller owns the fallback and
    // can only act on what the seam tells it.
    //
    // TargetTransient exists for the same structural reason: a degenerate W2 is RETRYABLE, and without a
    // case for it the seam could only throw -- making the identical condition terminal at W2 while it is
    // recoverable at W1.
    [Fact]
    public void There_are_exactly_four_cases()
        => Assert.Equal(4, System.Enum.GetValues<CaptureOutcomeKind>().Length);

    // Carries no RESULT, but does carry the reason its terminal message needs -- a payload with exactly
    // one consumer, unlike the W2 rectangle the Resized signal was correctly denied.
    [Fact]
    public void The_transient_signal_carries_a_reason_but_no_result()
    {
        var t = CaptureOutcome.Transient("the window reported no renderable area");
        Assert.Equal(CaptureOutcomeKind.TargetTransient, t.Kind);
        Assert.Null(t.Result);
        Assert.Equal("the window reported no renderable area", t.TransientReason);
    }
}
