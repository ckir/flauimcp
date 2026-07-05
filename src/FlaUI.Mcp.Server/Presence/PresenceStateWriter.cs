using System.IO;
using FlaUI.Mcp.Core.Presence;

namespace FlaUI.Mcp.Server.Presence;

/// <summary>Writes/removes the live presence state file for the CLI (spec §3.2). `presence off` writes an
/// explicit disabled line so a running server sees it on its NEXT query — immediate revocation, no reconnect.</summary>
public static class PresenceStateWriter
{
    public static string Set(bool enabled, int nearbySecs, int awaySecs)
    {
        Directory.CreateDirectory(PresenceState.StateDir());
        File.WriteAllText(PresenceState.StatePath(), PresenceConfig.Format(new PresenceConfig(enabled, nearbySecs, awaySecs)));
        return enabled
            ? $"presence ON (nearby {nearbySecs}s, away {awaySecs}s) — active immediately."
            : "presence OFF — telemetry stopped immediately.";
    }
}
