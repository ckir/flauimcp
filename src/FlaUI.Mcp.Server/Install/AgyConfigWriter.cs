using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Antigravity (Gemini-CLI-style) writer. Two edits: `mcpServers.flaui-mcp` in the servers
/// file, and `mcp(flaui-mcp/*)` appended to `permissions.allow` in the permissions file
/// (the two paths may be the same file). After install the caller must tell the user to
/// restart agy so the tool registry re-initializes.
/// </summary>
public sealed class AgyConfigWriter
{
    private const string Permission = "mcp(flaui-mcp/*)";
    private const string PluginName = "flaui-mcp";
    private readonly string _serversPath;
    private readonly string _permsPath;
    private readonly string _pluginsDir;

    public AgyConfigWriter(string mcpServersPath, string permissionsPath, string pluginsDir)
    {
        _serversPath = mcpServersPath;
        _permsPath = permissionsPath;
        _pluginsDir = pluginsDir;
    }

    private string PluginRoot => System.IO.Path.Combine(_pluginsDir, PluginName);

    /// <summary>
    /// Remove the deployed seed. Recursive is safe here: PluginRoot is our own namespace and holds
    /// only what <see cref="DeploySkill"/> wrote — the curated growth files live outside it, under
    /// `~/.claude/flaui-mcp/` (see the skill's "Load your learned rules first"). Never throws, for the
    /// same reason DeploySkill doesn't: a stuck skill dir must not derail the rest of the uninstall.
    /// </summary>
    private string? RemoveSkill()
    {
        try
        {
            if (System.IO.Directory.Exists(PluginRoot))
                System.IO.Directory.Delete(PluginRoot, recursive: true);
            return null;
        }
        catch (System.Exception e)
        {
            return $"seed driving skill left behind at {PluginRoot}: {e.Message}";
        }
    }

    /// <summary>Everything this writer touched — including the skill dir, which the detail line used to
    /// omit entirely, so the seed skill's whole existence was invisible in the install output.</summary>
    private string Detail(string? skillWarning) =>
        skillWarning is null
            ? $"{_serversPath}; {_permsPath}; {PluginRoot}"
            : $"{_serversPath}; {_permsPath}";

    public AgentResult Uninstall()
    {
        var sObj = JsoncFile.Load(_serversPath);
        bool removedServer = sObj["mcpServers"] is JsonObject servers && servers.Remove(McpServerEntry.ServerName);
        if (removedServer) JsoncFile.Save(_serversPath, sObj);

        var pObj = JsoncFile.Load(_permsPath);
        bool removedPerm = false;
        if (pObj["permissions"] is JsonObject permissions && permissions["allow"] is JsonArray allow)
        {
            for (int i = allow.Count - 1; i >= 0; i--)
                if ((string?)allow[i] == Permission) { allow.RemoveAt(i); removedPerm = true; }
            if (removedPerm) JsoncFile.Save(_permsPath, pObj);
        }

        var skillWarning = RemoveSkill();
        return new AgentResult("agy",
            removedServer || removedPerm ? AgentChange.Removed : AgentChange.NotFound,
            Detail(skillWarning), skillWarning);
    }
}
