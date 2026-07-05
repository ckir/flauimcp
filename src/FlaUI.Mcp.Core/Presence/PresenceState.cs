using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FlaUI.Mcp.Core.Presence;

/// <summary>The live presence config: enabled + thresholds. One line, key=value;… .</summary>
public readonly record struct PresenceConfig(bool Enabled, int NearbySecs, int AwaySecs)
{
    public static string Format(PresenceConfig c) =>
        $"enabled={(c.Enabled ? 1 : 0)};nearbySecs={c.NearbySecs};awaySecs={c.AwaySecs}";

    public static bool TryParse(string? line, out PresenceConfig cfg)
    {
        cfg = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        bool? enabled = null; int? near = null, away = null;
        foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) return false;
            switch (kv[0].Trim())
            {
                case "enabled": enabled = kv[1].Trim() == "1"; break;
                case "nearbySecs": if (int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) near = n; break;
                case "awaySecs": if (int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)) away = a; break;
            }
        }
        if (enabled is null || near is null || away is null) return false;
        cfg = new PresenceConfig(enabled.Value, near.Value, away.Value);
        return true;
    }
}

/// <summary>Live per-query reader of the presence state file (spec §3.2 immediate-off). Mirrors
/// FileLeaseProvider: same data dir, FileShare.ReadWrite + short retry. Absent file → the launch default.</summary>
public sealed class PresenceState
{
    public static string StateDir()
    {
        var dir = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlaUI.Mcp");
        return dir;
    }
    public static string StatePath() => Path.Combine(StateDir(), "presence.state");

    /// <summary>Read the live config. Absent file → `launchDefault` (the --presence launch flag's config).
    /// Unreadable/garbage → disabled (fail-closed to no-telemetry).</summary>
    public PresenceConfig Read(PresenceConfig launchDefault)
    {
        var path = StatePath();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return launchDefault;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return PresenceConfig.TryParse(sr.ReadLine(), out var c) ? c : new PresenceConfig(false, 60, 300);
            }
            catch (IOException) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) { return new PresenceConfig(false, 60, 300); }
        }
        return new PresenceConfig(false, 60, 300);
    }
}
