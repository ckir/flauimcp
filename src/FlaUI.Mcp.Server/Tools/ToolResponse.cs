using System.Text.Json;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Server;
using ModelContextProtocol.Protocol;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Shared MCP tool response helpers: compact JSON serialization and the
/// ToolException -> structured-error boundary. A single bad call never escapes unmapped.</summary>
public static class ToolResponse
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Ok(object payload) => JsonSerializer.Serialize(payload, Json);

    public static async Task<string> Guard(Func<Task<string>> body)
    {
        try { return await body(); }
        catch (ToolException ex)
        {
            return JsonSerializer.Serialize(
                new { error = ex.Code.ToString(), message = ex.Message, suggestedRecovery = ex.SuggestedRecovery }, Json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new { error = "INTERNAL", message = ex.Message, suggestedRecovery = (string?)"re-check arguments and retry" }, Json);
        }
    }

    public static async Task<CallToolResult> GuardImage(Func<Task<CallToolResult>> body)
    {
        try { return await body(); }
        catch (ToolException ex) { return ErrResult(ex.Code.ToString(), ex.Message, ex.SuggestedRecovery); }
        catch (Exception ex) { return ErrResult("INTERNAL", ex.Message, "re-check arguments and retry"); }
    }

    private static CallToolResult ErrResult(string code, string message, string? recovery) => new()
    { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(new { error = code, message, suggestedRecovery = recovery }, Json) } } };

    public static CallToolResult Image(byte[] png, object metadata) => new()
    { IsError = false, Content = new List<ContentBlock> { ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = JsonSerializer.Serialize(metadata, Json) } } };

    public static Task<string> GuardWrite(ServerOptions options, Func<Task<string>> body)
    {
        if (options.ReadOnly)
            return Guard(() => throw new ToolException(
                ToolErrorCode.WriteBlockedReadOnly,
                "Server is running in --read-only-mode; state-changing tools are disabled.",
                "restart the server without --read-only-mode to enable actions"));
        return Guard(body);
    }
}
