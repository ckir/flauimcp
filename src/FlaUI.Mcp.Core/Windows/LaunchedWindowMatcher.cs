namespace FlaUI.Mcp.Core.Windows;

/// <summary>
/// Decides whether a desktop window belongs to the app we just launched.
/// Apps like Win11 Notepad spawn the real window under a different PID than the one
/// returned by <c>Process.Start</c>; we match a candidate window's process name to the
/// launched executable's base name so we attach to OUR app, not an unrelated window that
/// happened to open during the launch wait. Process-name matching (vs. "first new titled
/// window") is what closes that race.
/// </summary>
public static class LaunchedWindowMatcher
{
    /// <param name="expectedProcessName">Launched exe base name without extension, e.g. "notepad".</param>
    /// <param name="windowProcessName">Process name of the candidate window's owning process, e.g. "Notepad".</param>
    /// <summary>⚠ <paramref name="windowProcessName"/> is NULLABLE because the window listing now reports
    /// null for a process it could not identify, rather than the placeholder "unknown" it used to invent.
    /// The behaviour here is unchanged and already correct: string.Equals(expected, null) is false, so an
    /// unidentifiable window is never claimed to BE the app we just launched — which is the right answer,
    /// since we cannot confirm it.</summary>
    public static bool IsExpectedApp(string expectedProcessName, string? windowProcessName) =>
        !string.IsNullOrEmpty(expectedProcessName)
        && string.Equals(expectedProcessName, windowProcessName, StringComparison.OrdinalIgnoreCase);
}
