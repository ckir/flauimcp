using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>ROADMAP item 12's anti-gaming half. The root Directory.Build.props turns warnings into
/// errors; this sweep enforces that no other build file quietly turns them back off.
///
/// It covers BOTH .csproj and nested Directory.Build.props, because a .csproj-only sweep is trivially
/// bypassable: MEASURED, a root props setting `true` plus a `sub/Directory.Build.props` setting `false`
/// produced `1 Warning(s) / 0 Error(s)` and Build succeeded — MSBuild honours the NEAREST props file.
///
/// HONEST LIMIT, stated so nobody mistakes this for a proof: Directory.Build.targets, an &lt;Import&gt;,
/// a .props included under another name, or `-p:TreatWarningsAsErrors=false` on the command line all
/// still bypass it. This raises the cost of an accidental or lazy override; it does not make the gate
/// tamper-proof, and it is not worth pretending otherwise.</summary>
[Trait("Category", "SourceSweep")]
public class BuildPropertySweepTests
{
    private const string Property = "TreatWarningsAsErrors";

    // Build output, third-party, or scratch — never tracked build inputs. `.clavity` is gitignored
    // runtime state and DOES contain throwaway .csproj files (measured), so omitting it would make this
    // test fail on scratch nobody ships.
    private static readonly string[] ExcludedDirs =
        { "bin", "obj", ".git", ".clavity", ".serena", "dist", "publish", "node_modules" };

    [Fact]
    public void The_root_props_file_is_the_one_that_sets_the_property()
    {
        var root = Path.Combine(RepoPaths.Root, "Directory.Build.props");
        Assert.True(File.Exists(root), $"the root {root} must exist — it is what makes warnings errors");
        Assert.Contains($"<{Property}>true</{Property}>", File.ReadAllText(root), StringComparison.Ordinal);
    }

    [Fact]
    public void No_other_build_file_overrides_TreatWarningsAsErrors()
    {
        var rootProps = Path.Combine(RepoPaths.Root, "Directory.Build.props");
        var offenders = new List<string>();

        foreach (var file in BuildFiles())
        {
            // The root props file is the one that is SUPPOSED to set it.
            if (string.Equals(file, rootProps, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.ReadAllText(file).Contains(Property, StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetRelativePath(RepoPaths.Root, file));
        }

        Assert.True(offenders.Count == 0,
            $"{Property} must be set ONLY by the root Directory.Build.props. These build files mention " +
            $"it and would override or contradict it: {string.Join(", ", offenders)}. If a warning " +
            "genuinely needs silencing, add a targeted <NoWarn> for that specific warning ID with a " +
            "written reason instead.");
    }

    /// Every TRACKED build file: .csproj anywhere, plus Directory.Build.props anywhere (including the
    /// root one, which the caller filters). Scoped by FILE NAME rather than by content, so a document
    /// that merely discusses the property — this plan, the spec — is never swept.
    private static IEnumerable<string> BuildFiles()
    {
        foreach (var f in Directory.EnumerateFiles(RepoPaths.Root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            var isBuildFile = name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(name, "Directory.Build.props", StringComparison.OrdinalIgnoreCase);
            if (!isBuildFile) continue;

            var rel = Path.GetRelativePath(RepoPaths.Root, f);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => ExcludedDirs.Contains(s, StringComparer.OrdinalIgnoreCase))) continue;

            yield return f;
        }
    }
}
