using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Pure predicate — no UIA / desktop session, so it runs in the headless CI subset.
public class PerceptionPolicyTests
{
    [Theory]
    [InlineData("1Password")]   // case-insensitive
    [InlineData("keepassxc")]
    [InlineData("bitwarden")]
    [InlineData(" lastpass ")]  // trimmed
    public void Denied_credential_stores_are_blocked(string processName)
        => Assert.True(PerceptionPolicy.IsDenied(processName));

    /// <summary>BC: ordinary named processes stay allowed. This half is untouched — the denylist must not
    /// become a blanket refusal, or the tool stops working.</summary>
    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("FlaUI.Mcp.TestApp")]
    public void Ordinary_processes_are_allowed(string? processName)
        => Assert.False(PerceptionPolicy.IsDenied(processName));

    /// <summary>⚠⚠ DELIBERATE BEHAVIOUR CHANGE, and the SECOND pre-existing test SP3 has edited. These
    /// three cases used to live in the theory above, asserting that an unidentifiable process was ALLOWED.
    /// They now assert the opposite. Operator decision, taken at capstone round 5 (finding Q-j2).
    ///
    /// WHY THE OLD ASSERTION WAS WRONG: `PerceptionManager.SafeProcessName` returns null when the process
    /// cannot be identified, and `IsDenied(null)` returning FALSE meant a window that MIGHT be a credential
    /// store was served, because the floor could not read its name. "I could not identify this" is not
    /// evidence of safety — it is the absence of evidence, and the floor was reading it as permission.
    ///
    /// ⚠ This STRENGTHENS the assertion; it does not weaken one. Spec §4.4 forbids relaxing a test so SP3
    /// can pass, and nothing here is relaxed — strictly fewer windows are served than before. The rule that
    /// ordinary processes stay allowed is preserved above, unchanged, which is what stops this from
    /// degenerating into "deny everything".
    ///
    /// Affordable only because the null rate is low: SafeProcessName resolves elevated AND protected
    /// processes (measured non-elevated: wininit, services, csrss, lsass, MsMpEng) and falls back to the
    /// window's HWND when the UIA ProcessId read throws. If that stops holding, fix identification —
    /// do NOT relax this back.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_unidentifiable_process_is_DENIED(string? processName)
        => Assert.True(PerceptionPolicy.IsDenied(processName));
}
