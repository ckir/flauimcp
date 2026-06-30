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

    [McpServerTool(Destructive = true), Description("Write text to the system clipboard (CF_UNICODETEXT). Useful to stage text the user (or a later Phase-4 paste) can insert. Blocked in --read-only-mode. ClipboardUnavailable if the clipboard is locked.")]
    public Task<string> DesktopClipboardSet(
        [Description("The text to place on the clipboard.")] string text)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await ClipboardAccess.SetTextAsync(text);
            return ToolResponse.Ok(new { ok = true });
        });
}
