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

    /// <summary>The client-agnostic half — capability, prohibition, triggers, and the lease boundary.
    /// This is what `Program.cs` serves as `McpServerOptions.ServerInstructions`, so it must never name a
    /// mechanism only one client has.
    ///
    /// ⚠ MEASURED 2026-08-22: agy RECEIVES `InitializeResult.instructions` and DOES NOT surface it (a
    /// sentinel probe with a live-connection control came back `NONE-VISIBLE`). So this half reaches
    /// Claude Code and is inert on agy — agy's channel is the driving skill's frontmatter, pinned by
    /// SkillLoadLineTests. Do not "simplify" by assuming every client reads this.</summary>
    public static readonly string Core = string.Join("\n", new[]
    {
        "flaui-mcp is installed: you can see and operate this Windows desktop yourself.",
        "Never ask the user to look at, read, or click inside a desktop app on your behalf, and do not infer UI state indirectly from process lists.",
        "Triggers: what is on screen; is an app running or responding; what a background terminal/console tab shows; clicking, typing or filling a GUI dialog; confirming a change landed in the real app.",
        // D2 — the "addendum orphan" guard. Mechanism-free ON PURPOSE: a client that is not Claude Code
        // has no ToolSearch, and naming one here would be noise at best and a wrong instruction at worst.
        "These tools may need loading before they can be called - use your client's tool-discovery mechanism.",
        "Read-only perception needs no lease and cannot disturb the user: desktop_list_windows(includeHandles:true) then desktop_snapshot wN then desktop_get_text wN eN.",
        // The lease BOUNDARY is a core safety rule. The pointer to the skill that implements it is
        // client-specific and lives in the Addendum (spec D1) - so the original single line is SPLIT.
        //
        // ⚠ MEASURED 2026-08-22, and this wording is a CORRECTION - do not "restore" the older, tidier
        // sentence that grouped the tab read in with the leased actions. Reading a background terminal
        // tab does NOT need a lease: ToolResponse.GuardWrite (ToolResponse.cs:60-68) tests
        // options.ReadOnly and nothing else, and InputGuard - which raises InputNotLeased - is absent
        // from the whole ContentTools.DesktopReadTerminalTab -> PerceptionManager.ReadTerminalTabAsync
        // path, whose pattern actions are lease-exempt by design (spec S3.1). Verified live: the read
        // succeeded while desktop_input_status reported leaseStatus=locked, secondsRemaining=0.
        // The overstatement was not harmless - it twice caused a driver to decline reading a background
        // tab whose contents it needed. But the tab read DOES select the tab, so it is not free either,
        // and the preceding line has just promised that lease-free perception "cannot disturb the user".
        // Hence: state the true lease boundary AND keep the disturbance warning. Both halves are load-
        // bearing, and every wording that keeps them measured 1101/1100 except this one (1098/1100).
        "Typing, clicking and dragging need a lease; reading a BACKGROUND terminal tab does not, but switches tabs.",
    });

    /// <summary>The Claude-Code-specific half: the concrete deferred-tool load call, its fallback, AND
    /// the pointer to the driving-flaui-mcp skill - all three name mechanisms a generic MCP client does
    /// not have (spec D1). Stays in the SessionStart hook and is NOT served over MCP.</summary>
    public static readonly string Addendum = string.Join("\n", new[]
    {
        // FIRST, deliberately: in the recomposed Text this line lands immediately after the core's
        // "...but switches tabs." line, so "those" keeps its referent - it points at the four
        // user-disturbing operations that line names, all of which the skill covers. Moving it to the END of the
        // addendum silently re-points "those" at the read-only tools named in the fallback line - tools
        // the core has just said need NO lease. Order is load-bearing here, not cosmetic.
        "For those, use the driving-flaui-mcp skill.",
        "Load the tools (one call):",
        LoadLine,
        "If that returns no matches, retry ToolSearch \"desktop window snapshot\" and use ONLY: desktop_list_windows, desktop_open_window, desktop_snapshot, desktop_get_text, desktop_input_status. If one is absent, say so — never substitute a similar name.",
    });

    /// <summary>The SessionStart payload: both halves, composed. Composing rather than hand-ordering is
    /// what makes drift between Core and Text impossible.</summary>
    public static readonly string Text = Core + "\n" + Addendum;

    public static string ToJson() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = Text }
    });
}
