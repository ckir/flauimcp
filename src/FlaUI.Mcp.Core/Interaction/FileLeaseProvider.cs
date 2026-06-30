using System;
using System.IO;
using System.Threading;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>File-backed lease at <c>%LOCALAPPDATA%\FlaUI.Mcp\input.lease</c> (dir overridable via
/// FLAUI_MCP_DATA_DIR). Opened FileShare.ReadWrite with a short retry to race the `unlock` writer.</summary>
public sealed class FileLeaseProvider : ILeaseProvider
{
    public static string LeaseDir()
    {
        var dir = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlaUI.Mcp");
        return dir;
    }

    public static string LeasePath() => Path.Combine(LeaseDir(), "input.lease");

    public InputLease? Read(out DateTime lastWriteUtc)
    {
        lastWriteUtc = default;
        var path = LeasePath();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var line = sr.ReadLine();
                return InputLease.TryParse(line, out var lease) ? lease : null;
            }
            catch (IOException) { Thread.Sleep(20); }      // sharing violation — retry
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }
}
