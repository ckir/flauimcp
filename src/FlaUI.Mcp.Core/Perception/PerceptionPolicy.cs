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

    /// <summary>True if a window owned by this process must not be snapshotted.</summary>
    public static bool IsDenied(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && DeniedProcesses.Contains(processName.Trim());
}
