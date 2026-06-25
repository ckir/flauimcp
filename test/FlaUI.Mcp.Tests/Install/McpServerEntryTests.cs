using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class McpServerEntryTests
{
    [Fact]
    public void ToJsonNode_emits_command_and_empty_args()
    {
        var node = McpServerEntry.ForExe(@"C:\tools\flaui-mcp.exe").ToJsonNode();
        Assert.Equal(@"C:\tools\flaui-mcp.exe", (string)node["command"]!);
        Assert.Empty(node["args"]!.AsArray());
    }
}
