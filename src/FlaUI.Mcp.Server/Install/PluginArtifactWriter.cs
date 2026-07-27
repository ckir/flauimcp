using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

internal static class PluginIds
{
    public const string PluginName      = "flaui-mcp";
    public const string MarketplaceName = "flaui-mcp-marketplace";
    public const string InstallTarget   = "flaui-mcp@flaui-mcp-marketplace";
    public const string SkillResource   = "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md";
    public const string LearnSkill      = "FlaUI.Mcp.Server.seed.flaui-learn.SKILL.md";
    public const string CurateSkill     = "FlaUI.Mcp.Server.seed.flaui-curate.SKILL.md";
    public const string HooksJson       = "FlaUI.Mcp.Server.seed.hooks.hooks.json";
    public const string CurateNudgeSh   = "FlaUI.Mcp.Server.seed.scripts.flaui-curate-nudge.sh";

    /// The isolated staging dir the plugin artifacts are generated into — the SINGLE definition,
    /// shared by install/uninstall (CliRouter) and status (InstallStatus) so they can never disagree
    /// about which directory to write and inspect. FLAUI_MCP_STAGING_DIR redirects it so tests never
    /// touch a real install tree.
    public static string StagingDir(string exePath)
        => System.Environment.GetEnvironmentVariable("FLAUI_MCP_STAGING_DIR")
           ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exePath)!, "plugin");
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

    /// Merge runtime flag args into the staging .mcp.json's server args (idempotent: removals win, no dups),
    /// preserving flags other verbs set. Seeds the file (args:[]) if it does not exist yet.
    public void MergeArgs(string exePath, IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        var path = Path.Combine(_stagingDir, ".mcp.json");
        if (!File.Exists(path)) WriteMcpJson(exePath);

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var server = root["mcpServers"]![PluginIds.PluginName]!;
        var existing = server["args"]!.AsArray();

        var kept = existing.Select(a => a!.GetValue<string>())
                           .Where(a => !remove.Contains(a) && !add.Contains(a))
                           .Concat(add.Where(a => !remove.Contains(a)))
                           .ToList();

        var arr = new JsonArray();
        foreach (var a in kept) arr.Add(a);
        server["args"] = arr;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Generate(string exePath, string version)
    {
        WriteMcpJson(exePath);
        WritePluginJson(version);
        WriteMarketplaceJson();
        WriteSkills();
        WriteHooksAndScripts(exePath);
    }

    private void WritePluginJson(string version)
    {
        var model = new { name = PluginIds.PluginName, version, description = "Drive the Windows desktop via MCP." };
        File.WriteAllText(Path.Combine(_stagingDir, "plugin.json"), JsonSerializer.Serialize(model, Pretty));
    }

    private void WriteMarketplaceJson()
    {
        var dir = Path.Combine(_stagingDir, ".claude-plugin");
        Directory.CreateDirectory(dir);
        var model = new
        {
            schema  = "https://code.claude.com/schemas/marketplace.json",
            name    = PluginIds.MarketplaceName,
            owner   = new { name = "ckir" },
            plugins = new[] { new { name = PluginIds.PluginName, source = ".", description = "Drive the Windows desktop via MCP." } }
        };
        // "$schema" is not a valid C# member name — serialize then fix the key, or use a JsonObject.
        var json = JsonSerializer.Serialize(model, Pretty).Replace("\"schema\":", "\"$schema\":");
        File.WriteAllText(Path.Combine(dir, "marketplace.json"), json);
    }

    private void WriteSkills()
    {
        Extract(PluginIds.SkillResource, Path.Combine("skills", "driving-flaui-mcp", "SKILL.md"));
        Extract(PluginIds.LearnSkill,    Path.Combine("skills", "flaui-learn", "SKILL.md"));
        Extract(PluginIds.CurateSkill,   Path.Combine("skills", "flaui-curate", "SKILL.md"));
    }

    private void WriteHooksAndScripts(string exePath)
    {
        Extract(PluginIds.CurateNudgeSh, Path.Combine("scripts", "flaui-curate-nudge.sh"));

        // hooks.json is GENERATED, not extracted: the SessionStart command needs the installed exe's
        // ABSOLUTE path, which only exists at install time — the same value .mcp.json already gets.
        // The embedded copy supplies every other hook (currently the curate-nudge Stop hook), so a
        // hook added to the repo tree ships without touching this method.
        using var stream = typeof(PluginArtifactWriter).Assembly.GetManifestResourceStream(PluginIds.HooksJson)
            ?? throw new FileNotFoundException($"embedded plugin resource missing: {PluginIds.HooksJson}");
        using var reader = new StreamReader(stream);
        var root = JsonNode.Parse(reader.ReadToEnd())!;

        MergeActivationHook(root, exePath);

        var target = Path.Combine(_stagingDir, "hooks", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, root.ToJsonString(Pretty));
    }

    /// Merge the activation hook into a parsed hooks.json root, PRESERVING any SessionStart entries the
    /// embedded copy already defines. Separated from resource loading purely so it can be tested against
    /// an input the embedded copy does not currently contain — a base file that ALREADY has SessionStart.
    /// An earlier version assigned over the key, which would have silently amputated such a hook while
    /// the surrounding comment promised the opposite.
    internal static void MergeActivationHook(JsonNode root, string exePath)
    {
        var entry = new JsonObject
        {
            // matcher copied from the WORKING SessionStart hook already in this repo
            // (.claude/settings.json) — a proven shape, not an invented one. All three sources
            // matter: compact and clear are exactly when the agent has just lost its context.
            ["matcher"] = "startup|clear|compact",
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"]    = "command",
                    // Serialized via JsonNode, so the Windows backslashes are escaped correctly.
                    ["command"] = $"\"{exePath}\" {ActivationPayload.Verb}",
                }
            }
        };

        var hooks = root["hooks"]!.AsObject();
        switch (hooks["SessionStart"])
        {
            case JsonArray existing:
                existing.Add(entry);
                break;

            // A single entry OBJECT is the other shape this key can legitimately take. Assigning over
            // it would silently delete a working hook — the exact defect the array branch above exists
            // to avoid — so promote it into an array and keep both. DeepClone because a JsonNode cannot
            // be re-parented while still attached to its current parent.
            case JsonObject single:
                hooks["SessionStart"] = new JsonArray { single.DeepClone(), entry };
                break;

            default:
                hooks["SessionStart"] = new JsonArray { entry };
                break;
        }
    }

    /// Copy an embedded resource to a staging-relative path BYTE FOR BYTE. Deliberately a raw stream
    /// copy, not ReadToEnd/WriteAllText: text round-tripping would re-encode newlines and turn a
    /// shipped .sh into CRLF, which bash rejects at the first `\r`.
    private void Extract(string resource, string relativePath)
    {
        var target = Path.Combine(_stagingDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var stream = typeof(PluginArtifactWriter).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"embedded plugin resource missing: {resource}");
        using var outFile = File.Create(target);
        stream.CopyTo(outFile);
    }
}
