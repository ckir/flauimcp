using System.ComponentModel;
using FlaUI.Mcp.Core.Interaction;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ClipboardTools
{
    private readonly ServerOptions _options;
    public ClipboardTools(ServerOptions options) => _options = options;

    [McpServerTool(ReadOnly = true), Description("Read the system clipboard as text (CF_UNICODETEXT). Returns {text} (empty if the clipboard holds no text). WARNING: this can surface secrets a user copied (no redaction possible at this layer). ClipboardUnavailable if the clipboard is locked.")]
    public Task<string> DesktopClipboardGet()
        => ToolResponse.Guard(async () => ToolResponse.Ok(new { text = await ClipboardAccess.GetTextAsync() }));
}
