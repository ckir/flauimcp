namespace FlaUI.Mcp.Core.Perception;

/// <summary>Perception security floor. A snapshot reads a window's entire UIA tree into the agent's
/// context, so snapshotting a secrets-bearing app (a password manager) is itself an exfiltration
/// risk — and prompt injection can drive the agent to do it. This is the denylist FLOOR: reject
/// snapshots of windows owned by known credential-store processes outright. UIA trees are
/// process-homogeneous per window, so a window-level reject (by owning process name) is sufficient;
/// per-node pruning solves a case the architecture doesn't produce. Complementary always-on
/// <c>IsPassword</c> redaction in SnapshotEngine catches secret fields INSIDE otherwise-allowed apps
/// (e.g. a browser password box) — the gap a process list alone misses.</summary>
public static class PerceptionPolicy
{
    // Process base names (no ".exe", as System.Diagnostics.Process.ProcessName reports them).
    // Matched case-insensitively. Conservative default set of common desktop credential stores.
    private static readonly HashSet<string> DeniedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1password", "1passwordbrowsersupport",
        "bitwarden", "bitwarden-desktop",
        "keepass", "keepassxc",
        "keeper",
        "dashlane",
        "lastpass",
        "nordpass",
        "enpass",
        "protonpass", "proton pass",
        "roboform",
        "passwordsafe", "pwsafe",
    };

    /// <summary>True if a window owned by this process must not be snapshotted.
    ///
    /// ⚠⚠ AN UNIDENTIFIABLE PROCESS IS DENIED. This used to read
    /// `!string.IsNullOrWhiteSpace(processName) &amp;&amp; DeniedProcesses.Contains(...)`, so `IsDenied(null)`
    /// was FALSE — a window whose owning process could not be named was ALLOWED, including one that might
    /// be a credential store. That is the security floor failing open on the exact input it cannot judge,
    /// and it predates the redaction feature entirely (found at capstone round 5, finding Q-j2, while
    /// verifying the same null-handling bug in the rules path).
    ///
    /// "I could not identify this process" is not evidence that it is safe. The denylist exists to refuse
    /// things it recognises as dangerous, and an unreadable name means recognition did not happen — so the
    /// honest answer is refusal, not permission.
    ///
    /// ⚠ Affordable ONLY because the null rate is genuinely low: PerceptionManager.SafeProcessName resolves
    /// elevated and protected processes (measured: wininit, services, csrss, lsass, MsMpEng from a
    /// NON-elevated caller) and falls back to the window's HWND when the UIA ProcessId read throws. After
    /// that, null means the process is gone or unidentifiable — a window there is nothing to automate
    /// anyway. If that ever stops being true, fix the identification, do NOT relax this back to fail-open.</summary>
    public static bool IsDenied(string? processName) =>
        string.IsNullOrWhiteSpace(processName) || DeniedProcesses.Contains(processName.Trim());
}
