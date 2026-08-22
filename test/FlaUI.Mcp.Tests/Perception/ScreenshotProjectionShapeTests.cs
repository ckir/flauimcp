using System.Linq;
using System.Text.Json;
using Xunit;
// ⚠ Add these usings to the file: System.IO, System.Text.RegularExpressions.
using System.IO;
using System.Text.RegularExpressions;

namespace FlaUI.Mcp.Tests.Perception;

public class ScreenshotProjectionShapeTests
{
    // A projection-shape tripwire. A field silently dropped from the anonymous object reaches an agent as
    // an ABSENT field it has no way to notice -- and the tool description promises these by name, so a
    // mismatch between the two is a broken contract rather than a missing nicety.
    [Fact]
    public void The_metadata_object_carries_exactly_the_documented_fields()
    {
        var metadata = new
        {
            bounds = new { x = 1, y = 2, w = 3, h = 4 },
            dpiScale = 1.0,
            scaleApplied = 1.0,
            redactions = 0,
            maskEscalations = 0,
            escalated = System.Array.Empty<object>(),
            unmaskedProcesses = System.Array.Empty<string>(),
            captureMethod = "printWindow",
            captureWarnings = System.Array.Empty<object>(),
        };

        var json = JsonSerializer.Serialize(metadata);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(new[]
        {
            "bounds", "dpiScale", "scaleApplied", "redactions", "maskEscalations", "escalated",
            "unmaskedProcesses", "captureMethod", "captureWarnings",
        }, names);
    }

    // captureWarnings entries are {code, recourse} OBJECTS, not bare strings. The analogy to
    // unmaskedProcesses holds only on the axis where the entries are things a consumer can branch on.
    [Fact]
    public void A_warning_entry_serializes_as_code_plus_recourse()
    {
        var w = FlaUI.Mcp.Core.Perception.CaptureWarnings.For(
            FlaUI.Mcp.Core.Perception.CaptureWarnings.UniformCanvas);
        var json = JsonSerializer.Serialize(new { code = w.Code, recourse = w.Recourse });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "code", "recourse" },
                     doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("uniformCanvas", doc.RootElement.GetProperty("code").GetString());
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlaUI.Mcp.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // The tool description at ScreenshotTools.cs:17 promises these field names. A field in the payload
    // but missing from that list is one the model has no reason to read; a field in the list but missing
    // from the payload is a broken promise. This asserts the two agree — BOTH directions, because a
    // sweep that reads only one side of a promise proves nothing. (Item 12 shipped exactly that defect:
    // BuildPropertySweepTests passed against a COMMENTED-OUT property.)
    [Fact]
    public void The_tool_description_enumerates_exactly_the_fields_the_projection_emits()
    {
        var root = RepoRoot();
        // ONE read. An earlier version bound the identical path to two variables (`projection` and
        // `src`), which reads as though two different files are being compared and is the kind of
        // detail a reviewer trusts rather than checks. The description and the projection are both in
        // this one file; that is the whole reason a single-file sweep can check both directions.
        var src = File.ReadAllText(Path.Combine(root, "src", "FlaUI.Mcp.Server", "Tools", "ScreenshotTools.cs"));
        var m = Regex.Match(src, @"JSON metadata \{([^}]+)\}");
        Assert.True(m.Success, "the tool description no longer contains a {field,field,...} enumeration");
        var documented = m.Groups[1].Value.Split(',').Select(s => s.Trim()).ToArray();
        var expected = new[]
        {
            "bounds", "dpiScale", "scaleApplied", "redactions", "maskEscalations", "escalated",
            "unmaskedProcesses", "captureMethod", "captureWarnings",
        };
        Assert.Equal(expected, documented);

        // THE OTHER DIRECTION. Without this the test passes while the projection emits nothing at all.
        //
        // ⚠⚠ COMMENTS ARE STRIPPED FIRST, AND THAT IS LOAD-BEARING. Matching the raw source means
        // `// captureMethod = result.CaptureMethod,` still satisfies the regex, so the tripwire is
        // defeated by typing two slashes -- which is EXACTLY the defect item 12 shipped, reappearing
        // inside the very test written to prevent it. MEASURED: `\bcaptureMethod\s*=` matches the
        // commented line. Do not "simplify" this back.
        var code = StripComments(src);
        foreach (var field in expected)
            Assert.True(Regex.IsMatch(code, $@"\b{Regex.Escape(field)}\s*="),
                $"the tool description promises '{field}' but the metadata projection never assigns it");
    }

    /// <summary>Remove block and line comments so a commented-out assignment cannot satisfy a sweep.</summary>
    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join("\n", noBlocks
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }
}
