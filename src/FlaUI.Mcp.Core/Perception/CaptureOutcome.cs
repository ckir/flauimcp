namespace FlaUI.Mcp.Core.Perception;

public enum CaptureOutcomeKind
{
    /// <summary>An image was produced. Result is non-null.</summary>
    Completed,
    /// <summary>W1.Size != W2.Size. No image. The CALLER decides what happens next, and the answer
    /// differs by scope.</summary>
    Resized,
    /// <summary>PrintWindow did not return within the bound. No image. The caller falls back to the
    /// scrape with scrapeFallbackTargetUnresponsive.</summary>
    TimedOut,
    /// <summary>W2 had zero or negative extents — the window has no renderable area RIGHT NOW. No image.
    /// RETRYABLE, and that is the whole reason this case exists separately from a refusal.
    ///
    /// ⚠ It exists because the design already treats the IDENTICAL condition at W1 as a retryable
    /// transient: "a window caught mid-open or mid-animation can report a degenerate rect for a frame —
    /// exactly the transient the retry loop exists to absorb". Throwing here while returning a soft
    /// signal there would make the same physical condition terminal or recoverable purely according to
    /// which of two reads a few milliseconds apart happened to see it, and would defeat the retry loop
    /// for precisely the animating window it was built for.
    /// *(AGY-AFTER panel over this plan, round 3, Axiom Breaker.)*
    ///
    /// Surfaces to the AGENT as ElementNotActionable once retries exhaust — the same code the W1 case
    /// surfaces as. The agent-facing code is not the internal signal.</summary>
    TargetTransient,
}

/// <summary>What CaptureWindow returns. It WRAPS a CaptureResult rather than being one, because the seam
/// has four outcomes and a CaptureResult can express only the first.
///
/// ⚠ Not an exception: this is ordinary, expected control flow on a path the design does not refuse, and
/// throwing here would collide with the ToolException conversions surrounding this code.
/// ⚠ Not a null or sentinel CaptureResult: the caller must distinguish "resized" from "timed out" from
/// any other empty outcome, and a sentinel collapses them.
/// ⚠ The two EMPTY signals -- Resized and TimedOut -- carry NO payload. Retrying means a fresh UIA walk, which discovers the new geometry
/// itself; nothing consumes a rectangle the failed attempt observed.</summary>
public sealed record CaptureOutcome(CaptureOutcomeKind Kind, CaptureResult? Result,
                                    string? TransientReason = null)
{
    public static CaptureOutcome Completed(CaptureResult result) => new(CaptureOutcomeKind.Completed, result);
    public static readonly CaptureOutcome Resized = new(CaptureOutcomeKind.Resized, null);
    public static readonly CaptureOutcome TimedOut = new(CaptureOutcomeKind.TimedOut, null);

    /// <summary>A retryable transient, carrying the sentence the caller uses IF the budget runs out.
    ///
    /// ⚠ This payload does NOT violate the no-payload rule the Resized signal obeys. That rule killed a
    /// payload NOBODY CONSUMED -- "a contract requiring data its only consumer discards". This one has
    /// exactly one consumer and is consumed on every terminal path: without it the coordinator would
    /// report "no renderable area" for an empty element crop, which is a false diagnosis.</summary>
    public static CaptureOutcome Transient(string reason)
        => new(CaptureOutcomeKind.TargetTransient, null, reason);
}
