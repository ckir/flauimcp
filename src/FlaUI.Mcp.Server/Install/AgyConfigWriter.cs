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
    private readonly string _serversPath;
    private readonly string _permsPath;

    public AgyConfigWriter(string mcpServersPath, string permissionsPath)
    {
        _serversPath = mcpServersPath;
        _permsPath = permissionsPath;
    }

    public AgentResult Install(string exePath, IReadOnlyList<string>? args = null)
    {
        // Edit 1: mcpServers (servers file).
        var sObj = JsoncFile.Load(_serversPath);
        var servers = sObj["mcpServers"] as JsonObject;
        if (servers is null) { servers = new JsonObject(); sObj["mcpServers"] = servers; }
        var existing = servers[McpServerEntry.ServerName] as JsonObject;
        var desired = McpServerEntry.ForExe(exePath, args).ToJsonNode();
        bool serversChanged = existing is null || existing.ToJsonString() != desired.ToJsonString();
        if (serversChanged) { servers[McpServerEntry.ServerName] = desired; JsoncFile.Save(_serversPath, sObj); }

        bool hasPerm = EnsurePermission();

        var change = (serversChanged || !hasPerm)
            ? (existing is null ? AgentChange.Created : AgentChange.Updated)
            : AgentChange.Unchanged;
        return new AgentResult("agy", change, $"{_serversPath}; {_permsPath}");
    }

    /// <summary>Non-destructive variant (spec §4.4 ops fold): merges `addArgs`/`removeArgs` into the args
    /// ALREADY registered for this server, preserving other flag groups (e.g. overlay coexisting with
    /// autosound) instead of replacing the whole `args` array.</summary>
    public AgentResult Install(string exePath, IReadOnlyList<string> addArgs, IReadOnlyList<string> removeArgs)
    {
        var sObj = JsoncFile.Load(_serversPath);
        var servers = sObj["mcpServers"] as JsonObject;
        if (servers is null) { servers = new JsonObject(); sObj["mcpServers"] = servers; }
        var existing = servers[McpServerEntry.ServerName] as JsonObject;
        var merged = ConfigArgsMerge.Apply(ReadArgs(existing), addArgs, removeArgs);
        var desired = McpServerEntry.ForExe(exePath, merged).ToJsonNode();
        bool serversChanged = existing is null || existing.ToJsonString() != desired.ToJsonString();
        if (serversChanged) { servers[McpServerEntry.ServerName] = desired; JsoncFile.Save(_serversPath, sObj); }

        bool hasPerm = EnsurePermission();

        var change = (serversChanged || !hasPerm)
            ? (existing is null ? AgentChange.Created : AgentChange.Updated)
            : AgentChange.Unchanged;
        return new AgentResult("agy", change, $"{_serversPath}; {_permsPath}");
    }

    /// <summary>Read the currently-registered `args` array off an existing server entry (empty if absent).</summary>
    private static string[] ReadArgs(JsonObject? entry) =>
        entry?["args"] is JsonArray arr ? arr.Select(a => (string?)a ?? "").ToArray() : System.Array.Empty<string>();

    /// <summary>Edit 2: permissions.allow (permissions file — reload separately in case it is the same file
    /// as the servers file). Returns whether the permission was ALREADY present before this call.</summary>
    private bool EnsurePermission()
    {
        var pObj = JsoncFile.Load(_permsPath);
        var permissions = pObj["permissions"] as JsonObject;
        if (permissions is null) { permissions = new JsonObject(); pObj["permissions"] = permissions; }
        var allow = permissions["allow"] as JsonArray;
        if (allow is null) { allow = new JsonArray(); permissions["allow"] = allow; }
        bool hasPerm = allow.Any(n => (string?)n == Permission);
        if (!hasPerm) { allow.Add(Permission); JsoncFile.Save(_permsPath, pObj); }
        return hasPerm;
    }

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

        return new AgentResult("agy",
            removedServer || removedPerm ? AgentChange.Removed : AgentChange.NotFound,
            $"{_serversPath}; {_permsPath}");
    }
}
