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
    public void Uninstall_removes_both_and_preserves_other_permissions()
    {
        var servers = TempFile();
        var perms = TempFile();
        File.WriteAllText(perms, "{ \"permissions\": { \"allow\": [ \"command(git status)\" ] } }");
        try
        {
            var plugins = Path.Combine(Path.GetTempPath(), "flaui-agy-" + Path.GetRandomFileName());
            var w = new AgyConfigWriter(servers, perms, plugins);
            AgyFixtures.SeedAgyInstall(servers, perms, plugins);
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
