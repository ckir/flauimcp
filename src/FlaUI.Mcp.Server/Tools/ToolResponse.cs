using System.Text.Json;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Shared MCP tool response helpers: compact JSON serialization and the
/// ToolException -> structured-error boundary. A single bad call never escapes unmapped.</summary>
internal static class ToolResponse
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
}
