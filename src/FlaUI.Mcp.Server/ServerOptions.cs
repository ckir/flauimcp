using System.Globalization;
using System.Linq;

namespace FlaUI.Mcp.Server;

/// <summary>Process-wide server options parsed from argv. ReadOnly rejects every
/// non-read-only tool, independent of whether the MCP client honors destructiveHint.
/// v0.10.1: Overlay/OverlayMs drive the opt-in intent overlay (off by default → zero cost).
/// New params carry defaults so existing `new ServerOptions(ReadOnly:…, AllowElevation:…)` call
/// sites (tests) compile unchanged.</summary>
public sealed record ServerOptions(bool ReadOnly, bool AllowElevation, bool Overlay = false, int OverlayMs = 500, bool Autosound = false)
{
    public static ServerOptions FromArgs(string[] args) =>
        new(ReadOnly: args.Contains("--read-only-mode"),
            AllowElevation: args.Contains("--unsafe-allow-elevation"),
            Overlay: args.Contains("--overlay"),
            OverlayMs: ParseOverlayMs(args),
            Autosound: args.Contains("--autosound"));

    // "--overlay-ms=N": clamp to >= 0 (a negative would throw in Task.Delay; garbage -> default-then-clamp).
    // Absent -> 500 (the record default), preserved here so FromArgs stays the single source of the value.
    private static int ParseOverlayMs(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--overlay-ms=", System.StringComparison.Ordinal));
        if (arg is null) return 500;
        var raw = arg["--overlay-ms=".Length..];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    }
}
