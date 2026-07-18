using System.IO;
using System.Linq;
using System.Text.Json;

namespace FlaUI.Mcp.Server.Install;

internal static class PluginIds
{
    public const string PluginName      = "flaui-mcp";
    public const string MarketplaceName = "flaui-mcp-marketplace";
    public const string InstallTarget   = "flaui-mcp@flaui-mcp-marketplace";
    public const string SkillResource   = "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md";
}

/// Generates the unified plugin directory in the isolated staging dir at install time.
/// Everything is written via JsonSerializer (never string interpolation) so Windows backslash
/// paths are correctly escaped. The installer packages ONLY the exe (flaui-mcp.iss:25); these
/// artifacts do not exist on disk until this runs.
public sealed class PluginArtifactWriter
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly string _stagingDir;

    public PluginArtifactWriter(string stagingDir) => _stagingDir = stagingDir;

    public void WriteMcpJson(string exePath)
    {
        Directory.CreateDirectory(_stagingDir);
        var path = Path.Combine(_stagingDir, ".mcp.json");
        // PRESERVE any runtime flags a prior overlay/autosound/presence verb set — only (re)write `command`, so a
        // re-install / Generate() over an existing plugin never wipes the user's flags. (plan-review R2.)
        var existingArgs = ReadExistingArgs(path);
        var model = new
        {
            mcpServers = new System.Collections.Generic.Dictionary<string, object>
            {
                [PluginIds.PluginName] = new { command = exePath, args = existingArgs }
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(model, Pretty));
    }

    private static string[] ReadExistingArgs(string path)
    {
        if (!File.Exists(path)) return System.Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers)
                && servers.TryGetProperty(PluginIds.PluginName, out var srv)
                && srv.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                return a.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }
        catch { /* malformed -> reseed fresh */ }
        return System.Array.Empty<string>();
    }
}
