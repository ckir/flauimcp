using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class OcrBookendTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    /// <summary>Comment lines blanked, line count preserved. Same reason as every other sweep in this
    /// repo: a source check that does not do this is defeated by typing two slashes.</summary>
    private static string CodeOnly(string source)
        => string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Select(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)
                ? string.Empty : l;
        }));

    // The documented contract: "A destroyed window reports changed: it is not safe to photograph either."
    // IntPtr.Zero is never a live window, so GetWindowRect fails and the probe returns null.
    [Fact]
    public void A_destroyed_or_invalid_window_reports_changed()
        => Assert.True(ScreenCapture.WindowRectChanged(IntPtr.Zero, new Rectangle(0, 0, 800, 600)));

    // ⚠ THE ONLY GUARD ON THE WIRING. The bookend itself cannot be exercised headlessly -- reaching it
    // needs a live UIA walk and a real screen grab -- so this pins that BOTH halves exist at BOTH sites.
    // Deleting either half is the realistic regression, and half 2 is the one that closes the race.
    [Fact]
    public void Both_OCR_capture_sites_are_bracketed_by_a_bookend()
    {
        var text = CodeOnly(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "FlaUI.Mcp.Server", "Tools", "FindTextTools.cs")));

        // two capture sites x two halves
        //
        // ⚠ `WindowRectChanged`, not `WindowSizeChanged`. It compared SIZE only, which is right for the
        // SCREENSHOT path (PrintWindow renders the window's own handle, so a pure move cannot affect it)
        // and insufficient here, because this path SCRAPES ABSOLUTE SCREEN COORDINATES: a window that
        // moves without resizing passed the old check and the scrape then photographed the rectangle the
        // window used to occupy. *(AGY-CAPSTONE round 2, finding 2.)*
        Assert.Equal(4, Regex.Matches(text, @"ScreenCapture\.WindowRectChanged\s*\(").Count);
    }

    // The two halves diagnose DIFFERENT things and must stay distinguishable to an agent reading the
    // refusal. Collapsing them to one message would still pass the count check above.
    [Fact]
    public void The_two_halves_keep_distinct_messages()
    {
        var text = CodeOnly(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "FlaUI.Mcp.Server", "Tools", "FindTextTools.cs")));

        Assert.Equal(2, Regex.Matches(text, "changed size between reading its redacted regions").Count);
        Assert.Equal(2, Regex.Matches(text, "changed size while it was being captured").Count);
    }
}
