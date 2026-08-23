using System.IO;
using System.Text.RegularExpressions;
using FlaUI.Mcp.Server.Install;
using ModelContextProtocol.Server;
using Xunit;

public class ServerInstructionsWiringTests
{
    // ---- BEHAVIOURAL: what the configuration actually does. No regex, no DI, no source text.
    [Fact]
    public void Apply_serves_the_core_and_never_the_whole_payload()
    {
        var options = new McpServerOptions();

        McpServerConfiguration.Apply(options);

        Assert.Equal(ActivationPayload.Core, options.ServerInstructions);
        Assert.NotEqual(ActivationPayload.Text, options.ServerInstructions);
        // The core must stay client-agnostic even here, so a future edit cannot smuggle the Claude Code
        // incantation onto every connecting client (spec D1).
        Assert.DoesNotContain("ToolSearch", options.ServerInstructions!, System.StringComparison.Ordinal);
        Assert.DoesNotContain("driving-flaui-mcp", options.ServerInstructions!, System.StringComparison.Ordinal);
    }

    // ---- STRUCTURAL: only that Program.cs still hands the seam to AddMcpServer.
    //
    // Strips COMMENTS ONLY. Stripping string literals too was measured to be WORSE: comments-then-strings
    // lets one "http://localhost/" orphan a quote and the string sweep then runs away and swallows the
    // AddMcpServer block, failing the build on a VALID file; strings-then-comments dies the same way on a
    // `"` inside a comment. Comments-only survives both and still catches a commented-out call.
    //
    // ⚠ CORRECTION, and it matters: an earlier revision called this sweep "no longer load-bearing"
    // because the behavioural test owns the content. That was HALF right and wrong where it counted. The
    // behavioural test proves Apply is CORRECT; it says nothing about whether Program.cs ever CALLS it -
    // Step 5's mutant demonstrates exactly that, by leaving the behavioural test green while the wiring
    // is commented out. So this sweep is the ONLY guard on REACHABILITY, and it is fully load-bearing
    // for that. It is no longer load-bearing for CONTENT. Those are different jobs.
    //
    // Because it IS load-bearing, the spoof hole is closed rather than accepted: the pattern below
    // requires the call to START a line (indentation, then `.AddMcpServer(`), which is the fluent form
    // real code uses and which a call quoted inside a string literal in a statement cannot produce.
    // MEASURED against four cases: matches both qualification forms of the real call, rejects a spoof in
    // an exception message, and still goes red on the commented-out mutant.
    // (Roslyn IS available here - the test project references Microsoft.CodeAnalysis.CSharp - and is
    // still not used: a parser would prove more about TEXT, when the behavioural test already proves the
    // thing that actually matters.)
    private static string ProgramSourceWithoutComments()
    {
        var src = File.ReadAllText(RepoPaths.At("src", "FlaUI.Mcp.Server", "Program.cs"));
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);   // block comments
        src = Regex.Replace(src, @"//[^\r\n]*", "", RegexOptions.Multiline);   // line comments
        return src;
    }

    [Fact]
    public void Program_hands_the_configuration_seam_to_AddMcpServer()
        => Assert.Matches(
            @"(?m)^\s*\.AddMcpServer\(\s*(?:FlaUI\.Mcp\.Server\.Install\.)?McpServerConfiguration\.Apply\s*\)",
            ProgramSourceWithoutComments());

    /// This one has to satisfy TWO opposing constraints, and an earlier revision failed each in turn:
    ///   too RIGID  -> `Install.ActivationPayload.Text` slips past, and a negative gate failing open ships
    ///                 the defect silently;
    ///   too LOOSE  -> with string literals no longer stripped, an exception message or log line holding
    ///                 that assignment breaks the build on perfectly valid code.
    /// So: ANCHORED on the AddMcpServer call, within a bounded window, AND permissive about
    /// qualification (`[\w.]*`). Do not drop either half.
    ///
    /// ⚠ CORRECTION - an earlier version of this comment claimed the anchor means "a stray literal must
    /// contain the call too". That is FALSE, and worth stating plainly so nobody relies on it: the engine
    /// matches the REAL AddMcpServer call, then `[\s\S]{0,300}?` happily crosses a quote, so a log or
    /// exception message containing this assignment within ~300 chars of the call WILL trip this gate and
    /// break the build on valid code.
    ///
    /// That false positive is ACCEPTED, deliberately, and the obvious fix is rejected. Narrowing the gap
    /// to `[^"]{0,300}?` removes the false positive but was MEASURED to fail OPEN on a real defect: given
    /// `.AddMcpServer(o => { o.ServerName = "flaui-mcp"; o.ServerInstructions = ActivationPayload.Text; })`
    /// the current pattern fires and the `[^"]` variant does not, because the quoted server name sits
    /// between the anchor and the assignment. A negative gate that fails open ships the defect silently;
    /// this one fails LOUD, at build time, where it is diagnosed in seconds. Loud beats silent here.
    /// ⚠ The right-hand side is `[\w.]*Text`, NOT `[\w.]*ActivationPayload\.Text`. Requiring the class
    /// name made this gate FAIL OPEN, which is the one direction a negative gate must never fail:
    /// `using static FlaUI.Mcp.Server.Install.ActivationPayload;` plus a bare
    /// `ServerInstructions = Text;` is a real inline configuration that the qualified pattern did not
    /// match at all. MEASURED both forms — the qualified pattern is silent on the using-static one, this
    /// one catches both, and neither fires on the real shipped wiring. Do not re-add the class name.
    [Fact]
    public void Program_never_configures_instructions_inline()
        => Assert.DoesNotMatch(
            @"AddMcpServer[\s\S]{0,300}?ServerInstructions\s*=\s*[\w.]*Text",
            ProgramSourceWithoutComments());
}
