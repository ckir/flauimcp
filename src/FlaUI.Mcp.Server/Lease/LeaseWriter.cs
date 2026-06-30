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
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var sid = id.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
                throw new InvalidOperationException("Could not resolve the current user's SID; refusing to write an unsecured lease.");
            return sid;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not resolve the current user's SID; refusing to write an unsecured lease.", ex);
        }
    }
}
