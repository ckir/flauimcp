namespace FlaUI.Mcp.Core.Windows;

/// <summary>Static, pure-Win32-input capability hint for terminal-multiplexer windows (spec §5.1).
/// Keyed ONLY on the process base-name already computed by ListWindowsAsync (SafeProcessName) — it must
/// NEVER trigger a UIA walk (desktop_list_windows is deliberately pure-Win32/non-blocking). No live tab
/// count (that would require a tree walk). The recognition SET is a small, documented, easily-extended
/// list: a rename or a new multiplexer just adds an entry (spec §5.1 recognition set).</summary>
public static class MultiplexerHint
{
    // .NET Process.ProcessName returns "WindowsTerminal" (no ".exe"); the Preview channel is a distinct
    // process. Exact, case-sensitive match on the bare name (verified on the current build).
    private static readonly HashSet<string> Multiplexers = new(System.StringComparer.Ordinal)
    {
        "WindowsTerminal",
        "WindowsTerminalPreview",
    };

    // One short sentence (desktop_list_windows is called frequently — a verbose recipe would be token
    // noise). It still carries the one load-bearing warning for an agent that never opens the skill:
    // this is ONLY the active tab; a WT window is NOT evidence a program is absent/headless.
    private const string TerminalHint =
        "Multiplexed terminal — this shows ONLY the active tab; a WT window is NOT evidence a program is " +
        "absent/headless. Snapshot to enumerate tabs, or use desktop_read_terminal_tab; see skill driving-flaui-mcp.";

    /// <summary>The hint for a recognized multiplexer process, else null (omitted from JSON).
    ///
    /// ⚠ The null guard is LOAD-BEARING, not defensive padding. Multiplexers uses StringComparer.Ordinal,
    /// and that comparer's GetHashCode(null) THROWS — so HashSet.Contains(null) raises
    /// ArgumentNullException rather than returning false. Without this guard, making the caller's process
    /// name nullable would crash desktop_list_windows outright for every window whose process could not be
    /// resolved. (A design consult asserted Contains(null) "returns false"; it does not.)</summary>
    public static string? For(string? processName)
        => processName is not null && Multiplexers.Contains(processName) ? TerminalHint : null;
}
