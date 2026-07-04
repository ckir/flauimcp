using System.IO;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputAuditTests
{
    [Fact]
    public void Writes_event_only_never_the_payload()
    {
        var sw = new StringWriter();
        new InputAudit(sw).Record(window: (nint)42, pid: 1234, process: "notepad", action: "type", payloadLength: 7);
        var line = sw.ToString();
        Assert.Contains("action=type", line);
        Assert.Contains("len=7", line);
        Assert.Contains("pid=1234", line);
        Assert.Contains("process=notepad", line);
    }

    [Fact]
    public void Omits_element_fields_byte_for_byte_when_no_element()
    {
        var sw = new StringWriter();
        new InputAudit(sw).Record(window: (nint)42, pid: 1234, process: "notepad", action: "type", payloadLength: 7);
        var line = sw.ToString().TrimEnd('\r', '\n');
        Assert.EndsWith("action=type len=7", line);
        Assert.DoesNotContain("rid=", line);
        Assert.DoesNotContain("bounds=", line);
    }

    [Fact]
    public void Appends_allow_listed_element_identity_quoted_and_escaped()
    {
        var sw = new StringWriter();
        var id = new ElementIdentity(
            RuntimeId: "42.1376068.4.1", AutomationId: "user name", ClassName: "Edit",
            ControlType: "Edit", Bounds: new Bounds(-1920, 0, 500, 500));
        new InputAudit(sw).Record((nint)42, 1234, "notepad", "type", 7, id);
        var line = sw.ToString().TrimEnd('\r', '\n');
        Assert.Contains("rid=\"42.1376068.4.1\"", line);
        Assert.Contains("aid=\"user name\"", line);
        Assert.Contains("class=\"Edit\"", line);
        Assert.Contains("ctype=\"Edit\"", line);
        Assert.Contains("bounds=-1920,0,500,500", line);
    }

    [Fact]
    public void Redaction_pin_carries_no_name_or_value_even_for_secret_looking_input()
    {
        var sw = new StringWriter();
        var id = new ElementIdentity(
            RuntimeId: "1.2", AutomationId: "pwd\"field\nx", ClassName: "PasswordBox",
            ControlType: "Edit", Bounds: new Bounds(0, 0, 10, 10));
        new InputAudit(sw).Record((nint)1, 2, "app", "type", 5, id);
        var line = sw.ToString();
        Assert.DoesNotContain("name=", line);
        Assert.DoesNotContain("value=", line);
        Assert.DoesNotContain("help=", line);
        Assert.Contains("aid=\"pwd\\\"field\\nx\"", line);
        Assert.DoesNotContain("\npwd", line);
    }

    [Fact]
    public void AuditQuote_escapes_quote_backslash_newline_and_renders_null_as_empty()
    {
        Assert.Equal("\"a b\"", InputAudit.AuditQuote("a b"));
        Assert.Equal("\"a\\\"b\"", InputAudit.AuditQuote("a\"b"));
        Assert.Equal("\"a\\\\b\"", InputAudit.AuditQuote("a\\b"));
        Assert.Equal("\"a\\nb\"", InputAudit.AuditQuote("a\nb"));
        Assert.Equal("\"\"", InputAudit.AuditQuote(null));
        Assert.Equal("\"\"", InputAudit.AuditQuote(""));
    }
}
