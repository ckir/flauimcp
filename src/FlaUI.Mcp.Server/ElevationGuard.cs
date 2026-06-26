using System.Runtime.Versioning;
using System.Security.Principal;

namespace FlaUI.Mcp.Server;

/// <summary>Startup safety: this server is meant to run at the user's integrity level, never
/// elevated. Running elevated lets UIA read and drive elevated windows and undermines UAC's
/// secure-desktop boundary — expanding the blast radius of a prompt-injection that reaches the
/// desktop. We WARN rather than refuse (so a deliberately-elevated desktop session still works),
/// and we write to STDERR because STDOUT is the MCP stdio protocol channel and must not be
/// polluted.</summary>
public static class ElevationGuard
{
    /// <summary>True if the current process runs with Administrator rights.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Pure decision seam: write the elevation warning iff <paramref name="isElevated"/>.
    /// Returns whether a warning was written.</summary>
    public static bool WarnIfElevated(bool isElevated, TextWriter stderr)
    {
        if (!isElevated) return false;
        stderr.WriteLine("[flaui-mcp] WARNING: running with elevated (Administrator) privileges. " +
            "This server is designed to run at user integrity; elevation expands the blast radius of a " +
            "compromised agent and lets it drive elevated windows. Restart it without elevation.");
        return true;
    }
}
