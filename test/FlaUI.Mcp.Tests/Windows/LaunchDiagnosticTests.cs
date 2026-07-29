using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

/// <summary>Closes backlog slug <c>launch-starves-on-ambient-single-instance</c>.
///
/// <para>desktop_launch_app refuses a window owned by a PRE-EXISTING pid, which is correct and was
/// deliberately left alone — attaching to a process we did not start would be a far worse bug, and that
/// hypothesis was measured and refuted during the A1a work. The defect was purely the DIAGNOSTIC: a
/// single-instance app hands the launch to its ambient process and exits, the loop starves, and the
/// caller was told to "increase timeoutMs" when no timeout could ever help.</para>
///
/// <para>HEADLESS, and that is the whole design of this test. The backlog entry argued against an
/// end-to-end launch: every available trigger depends on a THIRD PARTY's behaviour (VS Code's IPC
/// hand-off, Win11 Notepad's tabbing), so such a test would pin someone else's implementation detail and
/// would go green when Microsoft changed a launcher rather than when we fixed the wording. The condition
/// that selects the message is entirely ours, so that decision is what gets tested.</para></summary>
public class LaunchDiagnosticTests
{
    private const string Path = @"C:\Program Files\Contoso\Contoso.exe";
    private const string ProcName = "Contoso";

    [Fact]
    public void An_ambient_instance_is_named_as_the_cause_and_never_blamed_on_the_timeout()
    {
        var ex = WindowManager.LaunchTimeoutFor(Path, ProcName, timeoutMs: 5000,
            preExistingProcessNames: new[] { "explorer", "contoso", "svchost" });

        Assert.Equal(ToolErrorCode.LaunchTimeout, ex.Code);
        Assert.Contains(ProcName, ex.Message);
        Assert.Contains("ALREADY running", ex.Message);

        // The message must not assert more than the code can prove. A window may never have appeared
        // at all -- an immediate post-start failure is indistinguishable from a hand-off from in here --
        // so the hand-off is offered as the LIKELY cause with the alternative named, not stated as fact.
        Assert.Contains("LIKELY", ex.Message);
        Assert.Contains("looks identical", ex.Message);
        Assert.Contains("verify the arguments", ex.SuggestedRecovery);

        // THE LOAD-BEARING ASSERTION. Both halves of the old message were wrong for this case, and the
        // remedy was the actively harmful half: waiting longer can never change which pids are
        // eligible. If someone collapses the two branches back into one generic message, every other
        // assertion here could still pass — this one cannot.
        Assert.DoesNotContain("increase timeoutMs", ex.SuggestedRecovery);

        // And it must point at something that actually works.
        Assert.Contains("desktop_open_window", ex.SuggestedRecovery);
        Assert.Contains("--user-data-dir", ex.SuggestedRecovery);
    }

    [Fact]
    public void With_no_ambient_instance_the_generic_timeout_message_is_unchanged()
    {
        var ex = WindowManager.LaunchTimeoutFor(Path, ProcName, timeoutMs: 5000,
            preExistingProcessNames: new[] { "explorer", "svchost", "ContosoHelper" });

        Assert.Equal(ToolErrorCode.LaunchTimeout, ex.Code);
        Assert.Contains("no titled window", ex.Message);
        Assert.Contains("increase timeoutMs", ex.SuggestedRecovery);
    }

    /// <summary>The MATCH now happens inside the tested function rather than at the call site, which is
    /// what makes these two cases assertable at all. Previously the caller passed a ready-made bool, so a
    /// later edit could hardcode it to false and every test here would have stayed green while the
    /// feature was dead in production. The residual untested wiring is one argument — that the caller
    /// snapshots the names BEFORE Process.Start — which no unit test can reach without launching a
    /// third-party app, the very thing this design avoids.</summary>
    [Theory]
    [InlineData(new[] { "contoso" }, true)]              // case-insensitive, as the filter is
    [InlineData(new[] { "Contoso" }, true)]
    [InlineData(new[] { "ContosoHelper" }, false)]       // near-miss name must NOT trigger it
    [InlineData(new[] { "unknown" }, false)]             // SafeProcessNameOf's fallback must not match
    [InlineData(new string[0], false)]                   // empty snapshot
    public void The_ambient_verdict_is_decided_inside_the_tested_function(string[] names, bool ambient)
    {
        var ex = WindowManager.LaunchTimeoutFor(Path, ProcName, timeoutMs: 5000, preExistingProcessNames: names);
        Assert.Equal(ambient, ex.Message.Contains("ALREADY running"));
        Assert.Equal(!ambient, ex.SuggestedRecovery!.Contains("increase timeoutMs"));
    }

    /// <summary>The diagnostic must use the SAME notion of "same app" as the eligibility filter it
    /// explains. If the two ever diverge, the message could confidently blame an ambient instance the
    /// filter would never have rejected — blaming the wrong cause, which is the failure this whole
    /// change set has been removing.</summary>
    [Theory]
    [InlineData("Contoso", "contoso", true)]   // Windows process names are case-insensitive in practice
    [InlineData("Contoso", "Contoso", true)]
    [InlineData("Contoso", "ContosoHelper", false)]
    [InlineData("Contoso", "unknown", false)]  // SafeProcessName's fallback must not match
    [InlineData("", "Contoso", false)]         // no expected name => never a match
    public void The_ambient_check_agrees_with_the_eligibility_matcher(
        string expected, string actual, bool isSameApp)
        => Assert.Equal(isSameApp, LaunchedWindowMatcher.IsExpectedApp(expected, actual));
}
