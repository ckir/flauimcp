using System.Text.Json;

namespace FlaUI.Mcp.Server.Install;

/// <summary>The SessionStart payload. It is a PAYLOAD, not a signpost: a skimmed reminder leaves
/// nothing behind, whereas a skimmed payload still leaves an executable load line in context.
///
/// Compiled in rather than shipped as a script deliberately — a hook command of the form
/// `bash "..."` has no determinate interpreter on Windows (bare `bash` resolves to the WSL launcher
/// before Git Bash), and a script extracted with CRLF dies at its first `\r`.
///
/// Budgets (asserted by ActivationPayloadTests): 15 lines, and 1100 chars of PROSE excluding the load
/// line — that line is mechanical API verbosity, and counting it would force cutting a safety rule to
/// pay for a tool name. SessionStart hooks BLOCK the first turn (measured), so this must stay cheap
/// and must never grow into a second copy of the skill.</summary>
public static class ActivationPayload
{
    /// The CLI verb the generated SessionStart hook invokes. Named once so hooks.json generation and
    /// the router can never drift apart.
    public const string Verb = "activation-payload";

    private const string P = "mcp__flaui-mcp__desktop_";
    private const string Q = "mcp__plugin_flaui-mcp_flaui-mcp__desktop_";

    private static string Pair(string tool) => $"{P}{tool},{Q}{tool}";

    /// Both prefixes: tools are plugin-prefixed when registered as a plugin, bare when registered
    /// directly. `select:` ignores names it cannot match, so naming both is registration-agnostic.
    private static readonly string LoadLine =
        $"ToolSearch \"select:{Pair("list_windows")},{Pair("open_window")},{Pair("snapshot")}," +
        $"{Pair("get_text")},{Pair("input_status")}\"";

    public static readonly string Text = string.Join("\n", new[]
    {
        "flaui-mcp is installed: you can see and operate this Windows desktop yourself.",
        "Never ask the user to look at, read, or click inside a desktop app on your behalf, and do not infer UI state indirectly from process lists.",
        "Triggers: what is on screen; is an app running or responding; what a background terminal/console tab shows; clicking, typing or filling a GUI dialog; confirming a change landed in the real app.",
        "Load the tools (one call):",
        LoadLine,
        "If that returns no matches, retry ToolSearch \"desktop window snapshot\" and use ONLY: desktop_list_windows, desktop_open_window, desktop_snapshot, desktop_get_text, desktop_input_status. If one is absent, say so — never substitute a similar name.",
        "Read-only perception needs no lease and cannot disturb the user: desktop_list_windows(includeHandles:true) then desktop_snapshot wN then desktop_get_text wN eN.",
        "Typing, clicking, dragging, or reading a BACKGROUND terminal tab all need a lease — use the driving-flaui-mcp skill for those.",
    });

    public static string ToJson() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = Text }
    });
}
