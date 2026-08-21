using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureGeometryCallSiteTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    /// <summary>⚠ COMMENT-BLINDNESS IS THE DEFECT THIS REPO KEEPS SHIPPING, and both sweeps below need
    /// this. MEASURED: PerceptionManager.cs:1130 is a `///` doc comment reading "Wraps
    /// ResolveWindowCaptureGeometryAsync(handle," — the call-site sweep counted it as a fourth CALL,
    /// which is the entire reason an earlier draft of this plan called that test "known-red". It was not
    /// known-red; it was comment-blind. `///` starts with `//` so one prefix test covers both, and `*`
    /// covers the interior of a block comment.</summary>
    private static bool IsCode(string line)
    {
        var t = line.TrimStart();
        return !(t.StartsWith("//", System.StringComparison.Ordinal)
              || t.StartsWith("*", System.StringComparison.Ordinal));
    }

    private static List<(string File, int Line, string Text)> CodeLines(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .Where(x => IsCode(x.Text))
            .ToList();

    // The FULL-DESKTOP aggregator is the only caller that may clip. Every other call site takes the
    // unclipped default, and this pins that exactly one site names `clipToVirtualScreen: true`.
    //
    // ⚠ This sweep is the ONLY thing that catches a new caller silently inheriting the wrong yardstick.
    // A test of the yardstick logic itself cannot see a caller that never passed the parameter.
    [Fact]
    public void Exactly_one_production_call_site_clips_the_yardstick()
    {
        var clipping = CodeLines(RepoRoot())
            .Where(x => x.Text.Contains("clipToVirtualScreen: true")).ToList();
        Assert.Single(clipping);
        Assert.EndsWith("PerceptionManager.cs", clipping[0].File);
    }

    // Every call site of the geometry walk, counted. If this number changes, a new caller appeared and
    // somebody must decide its yardstick deliberately rather than inherit one.
    [Fact]
    public void The_geometry_walk_has_exactly_three_production_call_sites()
    {
        var calls = CodeLines(RepoRoot())
            .Where(x => Regex.IsMatch(x.Text, @"ResolveWindowCaptureGeometryAsync\s*\("))
            .Where(x => !x.Text.Contains("public Task<CaptureGeometry>"))
            .ToList();

        // ScreenshotTools:53 (window/element), PerceptionManager:1136 (OCR),
        // PerceptionManager:1181 (full-desktop).
        //
        // ⚠ THREE, and it stays three after Task 17. The coordinator takes the walk as a DELEGATE rather
        // than calling this method directly, which is what keeps it headless-testable — so it adds no
        // call site here. If this number ever goes UP, a new caller appeared and somebody must choose its
        // yardstick deliberately instead of inheriting one.
        Assert.Equal(3, calls.Count);
    }
}
