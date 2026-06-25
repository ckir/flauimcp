using System.Text.Json.Nodes;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class GenericMcpConfigWriterTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"flaui-gen-{Guid.NewGuid():N}.json");
    private static void Clean(string p) { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*")) File.Delete(f); }

    [Fact]
    public void Install_creates_then_is_idempotent()
    {
        var path = TempFile();
        try
        {
            var w = new GenericMcpConfigWriter();
            var r1 = w.Install(path, @"C:\x\flaui-mcp.exe");
            Assert.Equal(AgentChange.Created, r1.Change);

            var obj = JsoncFile.Load(path);
            Assert.Equal(@"C:\x\flaui-mcp.exe",
                (string)obj["mcpServers"]!["flaui-mcp"]!["command"]!);

            var r2 = w.Install(path, @"C:\x\flaui-mcp.exe");
            Assert.Equal(AgentChange.Unchanged, r2.Change);
        }
        finally { Clean(path); }
    }

    [Fact]
    public void Uninstall_removes_only_the_flaui_key()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ \"mcpServers\": { \"other\": { \"command\": \"o\" } } }");
        try
        {
            var w = new GenericMcpConfigWriter();
            w.Install(path, @"C:\x\flaui-mcp.exe");
            var r = w.Uninstall(path);
            Assert.Equal(AgentChange.Removed, r.Change);

            var obj = JsoncFile.Load(path);
            Assert.False(obj["mcpServers"]!.AsObject().ContainsKey("flaui-mcp"));
            Assert.True(obj["mcpServers"]!.AsObject().ContainsKey("other"));
        }
        finally { Clean(path); }
    }

    [Fact]
    public void PrintConfig_returns_snippet_with_the_exe()
    {
        var snippet = new GenericMcpConfigWriter().PrintConfig(@"C:\x\flaui-mcp.exe");
        Assert.Contains("mcpServers", snippet);
        Assert.Contains("flaui-mcp", snippet);
        Assert.Contains(@"C:\\x\\flaui-mcp.exe", snippet); // JSON-escaped backslashes
    }
}
