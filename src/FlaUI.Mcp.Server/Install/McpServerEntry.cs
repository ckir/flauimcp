using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>The MCP server definition agents store under their `mcpServers` map.</summary>
public sealed record McpServerEntry(string Command, IReadOnlyList<string>? Args = null)
{
    public const string ServerName = "flaui-mcp";

    /// <summary>Launch args that enable the opt-in intent overlay (see the `flaui-mcp overlay on` verb).</summary>
    public static readonly IReadOnlyList<string> OverlayArgs = new[] { "--overlay", "--overlay-ms=800" };

    public static McpServerEntry ForExe(string exePath, IReadOnlyList<string>? args = null) => new(exePath, args);

    public JsonObject ToJsonNode()
    {
        var arr = new JsonArray();
        if (Args is not null) foreach (var a in Args) arr.Add(a);
        return new() { ["command"] = Command, ["args"] = arr };
    }
}
