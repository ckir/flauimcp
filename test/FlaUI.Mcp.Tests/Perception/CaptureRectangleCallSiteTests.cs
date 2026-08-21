using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureRectangleCallSiteTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // Every production call site must name its scope EXPLICITLY. There is no safe default: the scope
    // decides which detector runs, and a caller that inherits one silently gets the wrong answer. The
    // OCR path (FindTextTools) is the caller this sweep exists for -- it was invisible to the spec until
    // it was measured, and it is the one most likely to be forgotten again.
    [Fact]
    public void Every_production_CaptureRectangle_call_names_its_scope()
    {
        var root = RepoRoot();
        var sites = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: f, Line: i + 1, Text: l)))
            .Where(x => x.Text.Contains("ScreenCapture.CaptureRectangle("))
            .ToList();

        // ⚠ FOUR CALL SITES, and the DECLARATION is not among them. The declaration reads
        // `public static CaptureResult CaptureRectangle(` -- no `ScreenCapture.` prefix -- so the
        // Contains() filter above never matches it. An earlier version of this test asserted
        // "1 declaration + 3 call sites" and then filtered for the declaration text that cannot be
        // present; it would have failed 4 != 3 even with every call site correctly updated.
        Assert.Equal(4, sites.Count);

        // Kept as a defensive filter in case the declaration is ever rewritten to self-qualify, but it
        // matches nothing today -- which is why the expected count is unchanged at 4.
        var calls = sites.Where(x => !x.Text.Contains("public static CaptureResult")).ToList();
        Assert.Equal(4, calls.Count);

        foreach (var c in calls)
            Assert.True(Regex.IsMatch(c.Text, @"CaptureScope\.\w+"),
                $"{Path.GetFileName(c.File)}:{c.Line} calls CaptureRectangle without naming a CaptureScope");
    }
}
