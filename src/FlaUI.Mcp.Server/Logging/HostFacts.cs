// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>`server.start` facts (spec §5.2). Every call is fail-soft to "unknown". These are the two facts
/// that each cost a session to diagnose before (DPI mismatch at 125%, ROADMAP item 16 foreground lock).</summary>
public static class HostFacts
{
    public static string Session()
    {
        try { return GetSystemMetrics(SM_REMOTESESSION) != 0 ? "rdp" : "console"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return "unknown"; }
    }

    public static string DpiAwareness()
    {
        try
        {
            var ctx = GetThreadDpiAwarenessContext();
            if (AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return "permonitorv2";
            return GetAwarenessFromDpiAwarenessContext(ctx) switch { 0 => "unaware", 1 => "system", 2 => "permonitor", _ => "unknown" };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return "unknown"; }
    }

    public static double DpiScale()
    {
        try { var dpi = GetDpiForSystem(); return dpi == 0 ? 1.0 : dpi / 96.0; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return 1.0; }
    }

    /// <summary>The `--x` flags present, without values: `--redaction-rules <path>` and `--log-level=debug`
    /// yield their names only, so no path or free text reaches the log.</summary>
    public static string[] FlagNames(string[] args) =>
        args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.Split('=')[0]).ToArray();

    private const int SM_REMOTESESSION = 0x1000;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
