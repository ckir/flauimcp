using System;
using System.IO;

namespace FlaUI.Mcp.Server.Install;

/// Resolves a bare CLI name (agy/claude) to a full path using PATH + PATHEXT and known
/// npm/cargo/local fallback dirs, so registration can gate on presence (absent => skip+report,
/// never abort). Pure given its injected PATH/PATHEXT/fileExists — unit-tested headlessly.
public static class CliResolver
{
    public static string? Resolve(
        string cli,
        string? path = null,
        string? pathext = null,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        pathext ??= Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";

        var exts = pathext.ToLowerInvariant().Split(';', StringSplitOptions.RemoveEmptyEntries);
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in EnumerateDirs(dirs))
        {
            // If cli already carries an extension, try it verbatim first.
            var verbatim = Path.Combine(dir, cli);
            if (Path.HasExtension(cli) && fileExists(verbatim)) return verbatim;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, cli + ext);
                if (fileExists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateDirs(string[] pathDirs)
    {
        foreach (var d in pathDirs) yield return d;
        var appdata = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appdata)) yield return Path.Combine(appdata, "npm");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".cargo", "bin");
            yield return Path.Combine(home, ".local", "bin");
        }
    }
}
