using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureRectangleCallSiteTests
{
    private const string Needle = "ScreenCapture.CaptureRectangle(";

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    /// <summary>Blank out comment lines while PRESERVING the line count, so the line numbers this sweep
    /// reports still match the real file.
    ///
    /// ⚠ A source sweep that does not do this is defeated by typing two slashes, and this repo has now
    /// shipped or nearly shipped that defect FOUR times. `///` starts with `//` so one prefix test covers
    /// both; `*` covers the interior of a block comment.</summary>
    private static string BlankComments(string source)
        => string.Join("\n", source.Split('\n').Select(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)
                ? string.Empty : l;
        }));

    /// <summary>Every call to the scrape seam, with its FULL argument list — read by balancing
    /// parentheses across however many physical lines the call happens to span.
    ///
    /// ⚠ THIS USED TO BE A PER-LINE CHECK, and that was a booby trap. The sweep required
    /// `ScreenCapture.CaptureRectangle(` and `CaptureScope.X` to sit on the SAME physical line, so the
    /// four call sites had to be collapsed to ~200-character lines to satisfy it. Anyone re-wrapping
    /// them — a formatter, a reviewer, an editor's line-length rule — would have turned this guard red
    /// with the message "calls CaptureRectangle without naming a CaptureScope" about a call that names
    /// one perfectly well. A guard whose failure message lies about the cause is worse than no guard.
    /// Reading the balanced argument list makes the sweep independent of formatting.</summary>
    private static List<(string File, int Line, string Args)> CallSites(string root)
    {
        var sites = new List<(string, int, string)>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = BlankComments(File.ReadAllText(f).Replace("\r\n", "\n"));
            for (int i = text.IndexOf(Needle, StringComparison.Ordinal); i >= 0;
                     i = text.IndexOf(Needle, i + 1, StringComparison.Ordinal))
            {
                int open = i + Needle.Length - 1;          // the '(' itself
                int depth = 0, j = open;
                for (; j < text.Length; j++)
                {
                    if (text[j] == '(') depth++;
                    else if (text[j] == ')' && --depth == 0) break;
                }
                int line = text.Take(i).Count(c => c == '\n') + 1;
                sites.Add((f, line, text.Substring(open, Math.Min(j, text.Length - 1) - open + 1)));
            }
        }
        return sites;
    }

    // Every production call site must name its scope EXPLICITLY. There is no safe default: the scope
    // decides which detector runs, and a caller that inherits one silently gets the wrong answer. The
    // OCR path (FindTextTools) is the caller this sweep exists for -- it was invisible to the spec until
    // it was measured, and it is the one most likely to be forgotten again.
    //
    // ⚠ FIVE CALL SITES, and the DECLARATION is not among them: it reads
    // `public static CaptureResult CaptureRectangle(` with no `ScreenCapture.` prefix, so the needle
    // never matches it. An earlier version asserted "1 declaration + 3 call sites" and would have failed
    // 4 != 3 even with every call site correctly updated.
    //
    // ⚠⚠ THE FIFTH IS A FORWARDER, AND IT CANNOT NAME A LITERAL SCOPE. `WindowCaptureCoordinator`'s
    // constructor defaults its injectable `_scrape` to `ScreenCapture.CaptureRectangle`, and that default
    // must be an explicit LAMBDA rather than a method group: `CaptureRectangle` carries an optional sixth
    // parameter (the `IScreenImageSource?` test seam), so its natural type is the six-argument delegate
    // and `??` against the five-argument field is a `CS0019`. The lambda receives the scope as a
    // PARAMETER and passes it straight through, so no `CaptureScope.Something` literal appears -- and the
    // original assertion below, which demanded that literal at every site, went red on it.
    //
    // Relaxing the rule for that site would gut the sweep, so it is pinned by SHAPE instead: the
    // forwarder must forward, verbatim. Hardcoding a scope there (`..., CaptureScope.Window, warn`) turns
    // this test red exactly as it should, because that is the silent-default defect the sweep exists to
    // catch -- it would make every coordinator fallback claim window scope regardless of what was asked.
    [Fact]
    public void Every_production_CaptureRectangle_call_names_its_scope()
    {
        var calls = CallSites(RepoRoot());
        Assert.Equal(5, calls.Count);

        var forwarders = calls.Where(c =>
            Path.GetFileName(c.File) == "WindowCaptureCoordinator.cs").ToList();
        var literal = calls.Except(forwarders).ToList();

        // The coordinator holds exactly ONE such site, and it forwards its scope parameter untouched.
        var fwd = Assert.Single(forwarders);
        Assert.Equal("(r, masks, w, sc, warn)", fwd.Args);

        Assert.Equal(4, literal.Count);
        foreach (var c in literal)
            Assert.True(Regex.IsMatch(c.Args, @"CaptureScope\.\w+"),
                $"{Path.GetFileName(c.File)}:{c.Line} calls CaptureRectangle without naming a CaptureScope");
    }

    // ⚠ THE ONE CALL SITE WHOSE SCOPE IS NOT CONSTANT, and the only guard on it.
    //
    // ScreenshotTools' window/element call serves BOTH scopes and must choose between them from `@ref`.
    // The two are NOT interchangeable: CaptureRectangle emits uniformCanvas for Window and deliberately
    // NOT for Element, because on an element the captured region is the ELEMENT and claiming "the window
    // rendered as one colour" is a statement the tool never measured.
    //
    // MEASURED: collapsing that ternary to a bare `CaptureScope.Window` turned NOTHING red across the
    // whole 986-test headless suite. Nothing exercises the discriminator -- ScreenshotTools reaches a
    // live UIA walk and a real screen grab, so there is no headless route to it. This is a structural
    // guard, not a behavioural one, and it is deliberately narrow: it catches exactly the mutation that
    // was measured to slip through.
    [Fact]
    public void The_window_element_call_site_still_discriminates_on_ref()
    {
        var site = CallSites(RepoRoot()).Single(c =>
            c.File.EndsWith("ScreenshotTools.cs", StringComparison.Ordinal) &&
            c.Args.Contains("geo.Bounds", StringComparison.Ordinal));

        Assert.Contains("CaptureScope.Window", site.Args);
        Assert.Contains("CaptureScope.Element", site.Args);
    }
}
