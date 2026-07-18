using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class PluginArtifactWriterTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "flaui-plugin-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Mcp_json_has_windows_path_escaped_and_correct_shape()
    {
        var staging = TempDir();
        var exe = @"C:\Users\me\AppData\Local\Programs\FlaUI.Mcp\flaui-mcp.exe";

        new PluginArtifactWriter(staging).WriteMcpJson(exe);

        var raw = File.ReadAllText(Path.Combine(staging, ".mcp.json"));
        Assert.Contains(@"C:\\Users\\me", raw); // backslashes JSON-escaped, not raw \U/\P

        using var doc = JsonDocument.Parse(raw); // must be valid JSON
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("flaui-mcp");
        Assert.Equal(exe, server.GetProperty("command").GetString());
        Assert.Equal(0, server.GetProperty("args").GetArrayLength());
    }

    [Fact]
    public void WriteMcpJson_preserves_existing_args_and_only_updates_command()
    {
        var staging = TempDir();
        var w = new PluginArtifactWriter(staging);
        // Seed a .mcp.json that already carries a runtime flag in args (as a prior overlay/autosound verb would).
        var path = Path.Combine(staging, ".mcp.json");
        File.WriteAllText(path,
            "{\"mcpServers\":{\"flaui-mcp\":{\"command\":\"C:\\\\v1\\\\flaui-mcp.exe\",\"args\":[\"--overlay\"]}}}");

        w.WriteMcpJson(@"C:\v2\flaui-mcp.exe"); // re-install with a new exe path

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var srv = doc.RootElement.GetProperty("mcpServers").GetProperty("flaui-mcp");
        Assert.Equal(@"C:\v2\flaui-mcp.exe", srv.GetProperty("command").GetString()); // command updated
        var args = srv.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("--overlay", args); // flags preserved across re-install
    }
}
