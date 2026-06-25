using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>The MCP server definition agents store under their `mcpServers` map.</summary>
public sealed record McpServerEntry(string Command)
{
    public const string ServerName = "flaui-mcp";

    public static McpServerEntry ForExe(string exePath) => new(exePath);

    public JsonObject ToJsonNode() =>
        new() { ["command"] = Command, ["args"] = new JsonArray() };
}
