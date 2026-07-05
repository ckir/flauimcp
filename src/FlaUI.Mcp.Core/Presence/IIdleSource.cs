namespace FlaUI.Mcp.Core.Presence;

/// <summary>Seam over the OS last-input clock. Returns milliseconds since the last physical input as a
/// non-negative long (rollover already resolved). A fake supplies the number in headless tests — the real
/// Win32 GetLastInputInfo is NEVER called in a unit test.</summary>
public interface IIdleSource
{
    long IdleMs();
}

/// <summary>Pure rollover-safe idle computation over 32-bit tick counts (spec §3.1). `unchecked` makes the
/// subtraction wrap correctly across the ~49.7-day GetTickCount boundary.</summary>
public static class IdleMath
{
    public static uint Compute(uint now, uint last) => unchecked(now - last);
}
