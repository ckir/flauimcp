using System.IO;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class AgyConfigWriterTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"flaui-agy-{Guid.NewGuid():N}.json");
    private static void Clean(string p) { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*")) File.Delete(f); }

    [Fact]
    public void Install_writes_both_mcpServers_and_permission_allow()
    {
        var servers = TempFile();
        var perms = TempFile();
        try
        {
            var plugins = Path.Combine(Path.GetTempPath(), "flaui-agy-" + Path.GetRandomFileName());
            var r = new AgyConfigWriter(servers, perms, plugins).Install(@"C:\x\flaui-mcp.exe");
            Assert.Equal(AgentChange.Created, r.Change);

            var s = JsoncFile.Load(servers);
            Assert.Equal(@"C:\x\flaui-mcp.exe", (string)s["mcpServers"]!["flaui-mcp"]!["command"]!);

            var p = JsoncFile.Load(perms);
            var allow = p["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Contains("mcp(flaui-mcp/*)", allow);
        }
        finally { Clean(servers); Clean(perms); }
    }

    [Fact]
    public void Install_does_not_duplicate_the_permission_on_rerun()
    {
        var servers = TempFile();
        var perms = TempFile();
        try
        {
            var plugins = Path.Combine(Path.GetTempPath(), "flaui-agy-" + Path.GetRandomFileName());
            var w = new AgyConfigWriter(servers, perms, plugins);
            w.Install(@"C:\x\flaui-mcp.exe");
            w.Install(@"C:\x\flaui-mcp.exe");
            var allow = JsoncFile.Load(perms)["permissions"]!["allow"]!.AsArray()
                .Select(n => (string)n!).Count(s => s == "mcp(flaui-mcp/*)");
            Assert.Equal(1, allow);
        }
        finally { Clean(servers); Clean(perms); }
    }

    [Fact]
    public void Uninstall_removes_both_and_preserves_other_permissions()
    {
        var servers = TempFile();
        var perms = TempFile();
        File.WriteAllText(perms, "{ \"permissions\": { \"allow\": [ \"command(git status)\" ] } }");
        try
        {
            var plugins = Path.Combine(Path.GetTempPath(), "flaui-agy-" + Path.GetRandomFileName());
            var w = new AgyConfigWriter(servers, perms, plugins);
            w.Install(@"C:\x\flaui-mcp.exe");
            w.Uninstall();

            var s = JsoncFile.Load(servers);
            Assert.False((s["mcpServers"] as JsonObject)?.ContainsKey("flaui-mcp") ?? false);

            var allow = JsoncFile.Load(perms)["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.DoesNotContain("mcp(flaui-mcp/*)", allow);
            Assert.Contains("command(git status)", allow);
        }
        finally { Clean(servers); Clean(perms); }
    }
}
