using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Warnings from an uninstall, parked where they outlive the uninstall.
///
/// WHY THIS IS NOT install.log: uninstall deletes the exe (see docs/operator-manual.md, Uninstall), so
/// `flaui-mcp status` — the reader for install.log — no longer exists moments later; and
/// `--purge-data` deletes install.log itself. A warning written there during uninstall is destroyed
/// at the moment it is written, for exactly the user who needs it.
///
/// The Inno uninstaller reads this file after our CLI has run and shows it to the user
/// (installer/flaui-mcp.iss), because it is the only actor still standing once the exe is gone.
/// </summary>
public static class UninstallWarnings
{
    public const string FileName = "uninstall-warnings.log";

    public static string PathIn(string stateDir) => Path.Combine(stateDir, FileName);

    /// <summary>Best-effort. Returns the path written, or null if there was nothing to say (or we
    /// could not say it). An EMPTY set removes any stale file rather than leaving last time's
    /// warnings to be shown as if they were current.</summary>
    public static string? Write(string stateDir, IReadOnlyList<string> lines)
    {
        try
        {
            var path = PathIn(stateDir);
            if (lines.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return null;
            }
            Directory.CreateDirectory(stateDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            File.WriteAllLines(path, new[] { $"flaui-mcp uninstall — {stamp}", "" }.Concat(lines));
            return path;
        }
        catch { return null; }   // the reporter must never itself become the failure
    }
}
