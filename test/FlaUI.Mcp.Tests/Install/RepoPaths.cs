using System;
using System.IO;
using System.Linq;

/// <summary>Locates files that live in the REPO (not the test output dir), by walking up from the
/// test assembly's base directory until a directory containing FlaUI.Mcp.slnx is found. Tests that
/// assert on tracked source files (SKILL.md copies, plugin tree) need this; tests that only exercise
/// staging dirs do not.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlaUI.Mcp.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                $"repo root (containing FlaUI.Mcp.slnx) not found above {AppContext.BaseDirectory}");
        return dir.FullName;
    }

    public static string At(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
}
