using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>Writes the standard `mcpServers.flaui-mcp` entry into any MCP-client JSON config.</summary>
public sealed class GenericMcpConfigWriter
{
    public AgentResult Install(string configPath, string exePath, IReadOnlyList<string>? args = null)
    {
        var obj = JsoncFile.Load(configPath);
        var servers = obj["mcpServers"] as JsonObject;
        if (servers is null) { servers = new JsonObject(); obj["mcpServers"] = servers; }

        var existing = servers[McpServerEntry.ServerName] as JsonObject;
        var desired = McpServerEntry.ForExe(exePath, args).ToJsonNode();
        if (existing is not null && existing.ToJsonString() == desired.ToJsonString())
            return new AgentResult("generic", AgentChange.Unchanged, configPath);

        var change = existing is null ? AgentChange.Created : AgentChange.Updated;
        servers[McpServerEntry.ServerName] = desired;
        JsoncFile.Save(configPath, obj);
        return new AgentResult("generic", change, configPath);
    }

    public AgentResult Uninstall(string configPath)
    {
        var obj = JsoncFile.Load(configPath);
        if (obj["mcpServers"] is not JsonObject servers || !servers.ContainsKey(McpServerEntry.ServerName))
            return new AgentResult("generic", AgentChange.NotFound, configPath);
        servers.Remove(McpServerEntry.ServerName);
        JsoncFile.Save(configPath, obj);
        return new AgentResult("generic", AgentChange.Removed, configPath);
    }

    public string PrintConfig(string exePath)
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [McpServerEntry.ServerName] = McpServerEntry.ForExe(exePath).ToJsonNode() }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
