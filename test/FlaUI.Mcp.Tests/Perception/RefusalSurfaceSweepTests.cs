using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RefusalSurfaceSweepTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // Every ToolErrorCode this feature's capture path can throw, with the reason it exists. Adding a
    // refusal WITHOUT adding it here fails this test, which is the only thing standing between the
    // spec's completeness claim and another silent drift.
    //
    // ⚠ This does NOT verify the spec prose - a test cannot read intent. What it pins is that the SET of
    // codes thrown by these files is the set someone deliberately wrote down. When it goes red, the fix
    // is to update BOTH this list and the spec's section 4 table, in the same commit.
    private static readonly HashSet<string> Documented = new()
    {
        "TargetDenied",           // denylisted process at geometry time; denylisted window on a fallback
        "ElementNotActionable",   // minimized, destroyed, degenerate-on-exhaustion, empty crop, OCR degenerate
        "RedactionUnmaskable",    // resize/bookend exhaustion with masks; the desktop walk refusing
        "CaptureUnavailable",     // null GDI handle, desktop not renderable, unwired denylist guard
    };

    /// <summary>Remove block and line comments so prose that names an error code cannot be mistaken for
    /// a throw, and a commented-out throw cannot be mistaken for a live one.</summary>
    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join("\n", noBlocks
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_capture_path_throws_only_documented_error_codes()
    {
        var root = RepoRoot();
        var files = new[]
        {
            Path.Combine(root, "src", "FlaUI.Mcp.Core", "Perception", "ScreenCapture.cs"),
            Path.Combine(root, "src", "FlaUI.Mcp.Core", "Perception", "WindowCaptureCoordinator.cs"),
            Path.Combine(root, "src", "FlaUI.Mcp.Server", "Capture", "PrintWindowImageSource.cs"),
        };

        var thrown = new HashSet<string>();
        foreach (var f in files)
        {
            Assert.True(File.Exists(f), $"{f} does not exist - the sweep is pointing at the wrong path");
            // ⚠⚠ COMMENTS ARE STRIPPED FIRST, AND THIS IS NOT OPTIONAL. Reading the raw source counts every
            // `ToolErrorCode.X` that appears in a COMMENT as a code the path throws -- and these three
            // files are unusually comment-dense, with prose that names error codes to explain guards.
            // The failure runs both ways: a commented-out `throw` still reads as live, and a code named
            // only in prose becomes a phantom refusal. **This repo has now shipped a comment-blind sweep
            // FIVE times** (item 12's property sweep, this plan's metadata sweep, its warning-code
            // reachability gate, the tool-description tripwire, and this one). Step 0b's second mutant is
            // what keeps it honest.
            foreach (Match m in Regex.Matches(StripComments(File.ReadAllText(f)), @"ToolErrorCode\.(\w+)"))
                thrown.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(thrown);
        var undocumented = thrown.Except(Documented).OrderBy(x => x).ToList();
        Assert.True(undocumented.Count == 0,
            "these error codes are thrown but not documented in the spec's section 4 refusal table: " +
            string.Join(", ", undocumented));
    }
}
