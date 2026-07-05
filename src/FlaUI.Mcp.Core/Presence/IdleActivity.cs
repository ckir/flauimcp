namespace FlaUI.Mcp.Core.Presence;

/// <summary>Coarse presence buckets (spec §3.1) — the ONLY activity information that leaves the process.
/// Raw idle-ms is never exposed (privacy: it is a behavioral-biometric stream).</summary>
public enum Activity { Active, Nearby, Away }

/// <summary>Pure idle-ms → coarse-enum bucketer + threshold validation. Rollover-safety lives in the seam
/// that produces idleMs (Task 2); this stays pure/headless.</summary>
public static class IdleActivity
{
    public static Activity Bucket(long idleMs, long nearbyMs, long awayMs)
    {
        if (idleMs < nearbyMs) return Activity.Active;      // includes the defensive negative case
        if (idleMs < awayMs) return Activity.Nearby;
        return Activity.Away;
    }

    /// <summary>Y (away) must be strictly greater than X (nearby); both positive.</summary>
    public static bool IsValidThresholds(int nearbySecs, int awaySecs)
        => nearbySecs > 0 && awaySecs > nearbySecs;
}
