using System;
using System.IO;
using System.Security.Principal;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Server.Lease;

/// <summary>Writes/removes the input lease for the CLI. The SID binds the lease to the granting user so
/// a cross-session writer can't grant input to a different session.</summary>
public static class LeaseWriter
{
    public static string Grant(int minutes, bool allowShells)
    {
        var dir = FileLeaseProvider.LeaseDir();
        Directory.CreateDirectory(dir);
        var sid = CurrentSid();
        var caps = allowShells ? new[] { "shells" } : Array.Empty<string>();
        var expiry = DateTime.UtcNow.AddMinutes(Math.Clamp(minutes, 1, 1440));
        File.WriteAllText(FileLeaseProvider.LeasePath(), InputLease.Format(expiry, sid, caps));
        return $"input unlocked until {expiry:O} (sid {sid}{(allowShells ? ", shells" : "")})";
    }

    public static string Revoke()
    {
        var path = FileLeaseProvider.LeasePath();
        if (File.Exists(path)) { File.Delete(path); return "input locked (lease removed)"; }
        return "input already locked (no lease)";
    }

    private static string CurrentSid()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value ?? "unknown"; }
        catch { return "unknown"; }
    }
}
