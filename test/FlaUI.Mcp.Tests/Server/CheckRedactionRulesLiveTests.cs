using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>SP3 Task 11, the LIVE half of the dry-run (spec §5.5, plan lines 1586-1588). Validating the
/// rule FILE answers "is this well-formed"; only a walk of a real window answers "what would it actually
/// withhold", which is the question an operator tuning a regex is asking.
///
/// ⚠ These are Desktop-trait because they need a real window, but they need NO input lease and NO physical
/// console: they are perception-only (a Win32 window enumeration plus a UIA name read), and MEASURED to
/// pass over RDP. Only SendInput requires the console.</summary>
[Trait("Category", "Desktop")]
public class CheckRedactionRulesLiveTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public CheckRedactionRulesLiveTests(TestAppFixture app) => _app = app;

    // Mirrors FlaUI.Mcp.TestApp.MainWindow.SecretValue (duplicated deliberately — TestApp is an exe the
    // fixture launches, not a referenced library; same reasoning as PasswordRedactionTests.cs:17-19).
    private const string Secret = "hunter2-NEVER-LEAK";

    /// A rule matching the fixture's one list item that has a Name and NO AutomationId.
    private static string RuleFile(string dir)
    {
        var p = Path.Combine(dir, "rules.json");
        File.WriteAllText(p, """
            {"version":1,"rules":[{"name":"named-only","global":true,"namePattern":"^NamedOnly$"}]}
            """);
        return p;
    }

    private static (string Out, int Code) Run(params string[] args)
    {
        var sw = new StringWriter();
        int code = CheckRedactionRulesCommand.Run(args, sw, instancesDir: Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        return (sw.ToString(), code);
    }

    [Fact]
    public void List_windows_reports_the_running_fixture_by_pid()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (text, code) = Run("check-redaction-rules", RuleFile(dir), "--list-windows");

        Assert.Equal(5, code);                                  // no server running; the walk is orthogonal
        Assert.Contains("windows (", text);
        Assert.Contains($"pid={_app.Process.Id}", text);
    }

    /// <summary>The output policy, both halves at once — they deliberately pull in OPPOSITE directions.
    /// A RULE-matched element shows its RAW name (the operator is debugging their own regex against their
    /// own screen; a redacted view makes that impossible), while an OS-password element stays
    /// "[REDACTED]" (that secret is not the operator's rule to debug).</summary>
    [Fact]
    public void A_rule_match_shows_its_raw_name_while_an_os_password_stays_redacted()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (text, _) = Run("check-redaction-rules", RuleFile(dir), "--window", _app.Process.Id.ToString());

        Assert.Contains("[rule:named-only] name=NamedOnly", text);   // RAW name, so the regex is debuggable
        Assert.Contains("[os-password] name=[REDACTED]", text);      // never the password's own name
        Assert.Contains("would withhold", text);
    }

    /// <summary>NO element VALUE is read on any path — only the name — so the typed password cannot reach
    /// this output even by accident. Asserting the secret's ABSENCE alone would pass against empty output,
    /// so the positive half is asserted too.</summary>
    [Fact]
    public void The_walk_never_prints_an_element_value()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (text, _) = Run("check-redaction-rules", RuleFile(dir), "--window", _app.Process.Id.ToString());

        Assert.DoesNotContain(Secret, text);
        Assert.Contains("would withhold", text);
    }

    /// <summary>A per-instance "w1" handle cannot survive a process boundary, so --window takes a PID.
    /// Passing a handle-shaped token must fail with a message that says so, not an opaque walk error.</summary>
    [Fact]
    public void A_handle_shaped_window_argument_is_rejected_with_a_useful_message()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (text, _) = Run("check-redaction-rules", RuleFile(dir), "--window", "w1");

        Assert.Contains("--window expects a PID", text);
    }
}
