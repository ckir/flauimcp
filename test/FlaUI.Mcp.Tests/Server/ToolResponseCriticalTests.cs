using System;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>SP4 capstone round 3 — the tool boundary must not launder a CRITICAL failure into a tool error.
///
/// `ToolResponse.Guard` and `GuardImage` used to catch `Exception` unfiltered, so an `OutOfMemoryException`
/// raised anywhere in a tool was returned to the agent as `{"error":"INTERNAL", "suggestedRecovery":
/// "re-check arguments and retry"}` while the server carried on in a state where it had just failed to
/// allocate. Re-checking arguments cannot help, and the real cause was hidden from every log above.
///
/// ⚠ This was the LAST frame of a three-round chase. Round 2 stopped the mask walk misclassifying criticals
/// as redaction outcomes; round 3 found they were merely being misclassified as ARGUMENT errors one frame
/// further out. A filter that stops short of the boundary accomplishes nothing.
///
/// ⚠ The `OperationCanceledException` half of the filter is DELIBERATELY unasserted here, and that is not
/// an oversight: VERIFIED at capstone round 3 that nothing in `src/` throws one — there is no
/// `CancellationToken` anywhere, and the ACTION path's `AwaitWithTimeout` throws
/// `ToolException(ActionBlockedPending)`. Asserting it would pin a behaviour no production path can reach.
/// It is in the filter so the guarantee holds by construction if cancellation is ever introduced.</summary>
public class ToolResponseCriticalTests
{
    [Fact]
    public async Task Guard_propagates_a_critical_failure_instead_of_returning_it_as_json()
        => await Assert.ThrowsAsync<OutOfMemoryException>(
            () => ToolResponse.Guard(() => throw new OutOfMemoryException("simulated")));

    [Fact]
    public async Task GuardImage_propagates_a_critical_failure_instead_of_returning_it_as_json()
        => await Assert.ThrowsAsync<OutOfMemoryException>(
            () => ToolResponse.GuardImage(() => throw new OutOfMemoryException("simulated")));

    /// <summary>The other half of the contract, and the reason the filter is narrow rather than a blanket
    /// rethrow: an ORDINARY failure must still be wrapped as a structured tool error. A fix that made
    /// everything propagate would satisfy the two facts above and destroy the boundary.</summary>
    [Fact]
    public async Task An_ordinary_failure_is_still_wrapped_as_a_structured_tool_error()
    {
        var json = await ToolResponse.Guard(() => throw new InvalidOperationException("ordinary"));

        Assert.Contains("INTERNAL", json);
        Assert.Contains("ordinary", json);
    }

    /// <summary>And a ToolException still maps to its own code, not to INTERNAL — the classification an
    /// agent branches on.</summary>
    [Fact]
    public async Task A_ToolException_still_reports_its_own_code()
    {
        var json = await ToolResponse.Guard(() => throw new ToolException(
            ToolErrorCode.RedactionUnmaskable, "cannot mask", "retry once the UI has settled"));

        Assert.Contains("RedactionUnmaskable", json);
        Assert.DoesNotContain("INTERNAL", json);
    }
}
