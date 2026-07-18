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

    [Fact]
    public void Generate_writes_all_four_artifacts_with_flat_marketplace_source_and_skill()
    {
        var staging = TempDir();
        new PluginArtifactWriter(staging).Generate(@"C:\p\flaui-mcp.exe", version: "0.16.2");

        Assert.True(File.Exists(Path.Combine(staging, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(staging, "plugin.json")));

        var mkt = Path.Combine(staging, ".claude-plugin", "marketplace.json");
        Assert.True(File.Exists(mkt));
        using var doc = JsonDocument.Parse(File.ReadAllText(mkt));
        Assert.Equal("flaui-mcp-marketplace", doc.RootElement.GetProperty("name").GetString());
        var plugin = doc.RootElement.GetProperty("plugins")[0];
        Assert.Equal("flaui-mcp", plugin.GetProperty("name").GetString());
        Assert.Equal(".", plugin.GetProperty("source").GetString()); // FLAT, not ./plugins/<name>

        var skill = Path.Combine(staging, "skills", "driving-flaui-mcp", "SKILL.md");
        Assert.True(File.Exists(skill));
        Assert.Contains("Driving FlaUI.Mcp", File.ReadAllText(skill)); // real skill content from embedded resource
    }

    [Fact]
    public void MergeArgs_adds_removes_and_preserves_other_flags_idempotently()
    {
        var staging = TempDir();
        var exe = @"C:\p\flaui-mcp.exe";
        var w = new PluginArtifactWriter(staging);
        w.MergeArgs(exe, add: new[] { "--overlay" },   remove: System.Array.Empty<string>()); // seeds .mcp.json if missing
        w.MergeArgs(exe, add: new[] { "--autosound" }, remove: System.Array.Empty<string>());
        w.MergeArgs(exe, add: new[] { "--autosound" }, remove: System.Array.Empty<string>()); // idempotent (no dup)
        w.MergeArgs(exe, add: System.Array.Empty<string>(), remove: new[] { "--overlay" });    // remove

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, ".mcp.json")));
        var args = doc.RootElement.GetProperty("mcpServers").GetProperty("flaui-mcp").GetProperty("args");
        var list = new System.Collections.Generic.List<string?>();
        foreach (var e in args.EnumerateArray()) list.Add(e.GetString());
        Assert.Equal(new[] { "--autosound" }, list); // overlay removed, autosound present exactly once
    }
}
