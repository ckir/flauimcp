using System.Globalization;
using System.Linq;

namespace FlaUI.Mcp.Server;

/// <summary>Process-wide server options parsed from argv. ReadOnly rejects every
/// non-read-only tool, independent of whether the MCP client honors destructiveHint.
/// v0.10.1: Overlay/OverlayMs drive the opt-in intent overlay (off by default → zero cost).
/// New params carry defaults so existing `new ServerOptions(ReadOnly:…, AllowElevation:…)` call
/// sites (tests) compile unchanged.</summary>
public sealed record ServerOptions(bool ReadOnly, bool AllowElevation, bool Overlay = false, int OverlayMs = 500, bool Autosound = false, bool Presence = false, int NearbySecs = 60, int AwaySecs = 300)
{
    public static ServerOptions FromArgs(string[] args) =>
        new(ReadOnly: args.Contains("--read-only-mode"),
            AllowElevation: args.Contains("--unsafe-allow-elevation"),
            Overlay: args.Contains("--overlay"),
            OverlayMs: ParseOverlayMs(args),
            Autosound: args.Contains("--autosound"),
            Presence: args.Contains("--presence"),
            NearbySecs: ParseIntArg(args, "--nearby-secs=", 60),
            AwaySecs: ParseIntArg(args, "--away-secs=", 300));

    // "--overlay-ms=N": clamp to >= 0 (a negative would throw in Task.Delay; garbage -> default-then-clamp).
    // Absent -> 500 (the record default), preserved here so FromArgs stays the single source of the value.
    private static int ParseOverlayMs(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--overlay-ms=", System.StringComparison.Ordinal));
        if (arg is null) return 500;
        var raw = arg["--overlay-ms=".Length..];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    }

    // "--nearby-secs=N" / "--away-secs=N": positive int; absent OR garbage -> fallback (NOT 0 — a 0 threshold
    // is invalid and would mis-bucket). Mirrors ParseOverlayMs but falls back to the default on bad input.
    private static int ParseIntArg(string[] args, string prefix, int fallback)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, System.StringComparison.Ordinal));
        if (arg is null) return fallback;
        var raw = arg[prefix.Length..];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;
    }
}
