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
}
