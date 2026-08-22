using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

/// <summary>AGY-CAPSTONE round 1, finding 1. The full-desktop capture path checked the denylist ONCE,
/// before the mask walk, and never again.
///
/// ⚠⚠ WHY THAT LEAKED. `AllMaskRectsAsync` SKIPS denylisted windows rather than masking them, so a
/// credential window that appears AFTER the refusal and BEFORE the shutter contributes no mask rects and
/// is photographed in the clear — on the one branch that captures the WHOLE virtual screen. The
/// coordinator's fallback scrape gained a second, post-shutter check in AGY-AFTER round 9 for exactly
/// this race; the full-desktop path was the adjacent path that did not get it. MEASURED: the gap is the
/// mask walk, ~2900ms on a 10-window desktop (Task 25 Step 4b) — roughly ten times the "tens to hundreds
/// of milliseconds" the coordinator's copy of that reasoning assumes.
///
/// ⚠ STRUCTURAL, AND HONESTLY SO. The full-desktop branch calls a concrete `PerceptionManager` rather
/// than an injected delegate, so there is no headless route to the behaviour — unlike the coordinator,
/// whose equivalent guard IS behaviourally tested because it takes `denylistedVisible` as a seam. This
/// pins that BOTH calls exist; it cannot prove the second one runs after the shutter. Read it as a
/// tripwire against the guard being deleted, not as proof it works.</summary>
public class DenylistShutterGuardTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    /// <summary>⚠ Comments are stripped first. This repo has shipped a comment-blind sweep FIVE times, and
    /// the very fix this test guards is surrounded by prose explaining the denylist — so counting raw
    /// source here would be self-defeating in both directions.</summary>
    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join("\n", noBlocks
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_full_desktop_path_checks_the_denylist_on_both_sides_of_the_shutter()
    {
        var src = StripComments(File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "FlaUI.Mcp.Server", "Tools", "ScreenshotTools.cs")));

        var checks = Regex.Matches(src, @"DenylistedWindowsVisibleAsync\s*\(").Count;
        Assert.True(checks == 2,
            $"expected TWO denylist checks in ScreenshotTools (one before the mask walk, one after the " +
            $"capture) but found {checks}. A credential window appearing during the ~2900ms mask walk " +
            "contributes no masks and lands in the image unredacted.");

        // And the second must come AFTER the capture, or it is the same check twice.
        var shutter = src.IndexOf("ScreenCapture.CaptureRectangle", StringComparison.Ordinal);
        var last = src.LastIndexOf("DenylistedWindowsVisibleAsync", StringComparison.Ordinal);
        Assert.True(shutter > 0 && last > shutter,
            "the second denylist check does not appear after the capture call - a pre-shutter check " +
            "cannot see a window that appears while the shutter is open");
    }
}
