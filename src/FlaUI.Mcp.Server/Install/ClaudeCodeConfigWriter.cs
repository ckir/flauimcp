using System.Collections.Generic;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Configures Claude Code via its stable `claude mcp add/remove` CLI. The runner returns the process
/// exit code plus stdout, and takes a working directory (null = inherit) because `claude plugin`
/// scopes bind to the CWD — see ClaudeCollisionRemedy. Injected for testability.
/// </summary>
public sealed class ClaudeCodeConfigWriter
{
    private readonly Func<string, string[], string?, RunResult> _run;

    public ClaudeCodeConfigWriter(Func<string, string[], string?, RunResult>? runner = null)
        => _run = runner ?? DefaultRunner;

    public AgentResult Install(string exePath, IReadOnlyList<string>? args = null)
    {
        // --scope user: register globally for the user, NOT at the default "local" scope (which
        // binds to the installer's working dir, making the server invisible in every other project).
        // Remove-then-add so re-registration is idempotent and can change the args of an existing entry
        // (`claude mcp add` fails on a duplicate name). The remove is best-effort (no-op if absent).
        _run("claude", new[] { "mcp", "remove", "--scope", "user", McpServerEntry.ServerName }, null);
        var addArgs = new List<string> { "mcp", "add", "--scope", "user", McpServerEntry.ServerName, "--", exePath };
        if (args is not null) addArgs.AddRange(args);
        var (code, output) = _run("claude", addArgs.ToArray(), null);
        return code switch
        {
            0 => new AgentResult("claude", AgentChange.Created, "claude mcp add"),
            ProcessRunner.NotFound => new AgentResult("claude", AgentChange.NotFound, "claude CLI not on PATH"),
            ProcessRunner.TimedOut => new AgentResult("claude", AgentChange.Failed, "claude mcp add timed out and was killed"),
            _ => new AgentResult("claude", AgentChange.Failed, Detail("claude mcp add", code, output))
        };
    }

    /// <summary>Non-destructive-merge SHAPE, but NOT non-destructive BEHAVIOR: unlike AgyConfigWriter/
    /// GenericMcpConfigWriter, this writer has no locally-readable config — the registered args live inside
    /// the opaque `claude` CLI. The runner CAN now capture stdout, but `claude mcp` exposes no read-back of a
    /// registered server's args, so there is still nothing to merge against. `removeArgs` is therefore unused;
    /// `addArgs` is passed through to the existing full-replace `Install(exePath, args)` verbatim, which
    /// matches this writer's PRE-EXISTING behavior for every verb (no regression — it never merged).</summary>
    public AgentResult Install(string exePath, IReadOnlyList<string> addArgs, IReadOnlyList<string> removeArgs) =>
        Install(exePath, addArgs);

    public AgentResult Uninstall()
    {
        // Must match the install scope (--scope user), else the user-scope entry is left orphaned.
        var (code, output) = _run("claude", new[] { "mcp", "remove", "--scope", "user", McpServerEntry.ServerName }, null);
        return code switch
        {
            0 => new AgentResult("claude", AgentChange.Removed, "claude mcp remove"),
            ProcessRunner.NotFound => new AgentResult("claude", AgentChange.NotFound, "claude CLI not on PATH"),
            ProcessRunner.TimedOut => new AgentResult("claude", AgentChange.Failed, "claude mcp remove timed out and was killed"),
            _ => new AgentResult("claude", AgentChange.Failed, Detail("claude mcp remove", code, output))
        };
    }

    /// The runner keeps stderr on failure precisely so the reason survives; a detail line of just
    /// "exit 1" is useless to the user who has to fix it. Trimmed and single-lined so one failure
    /// stays one log line (CliRouter.Report writes one line per result).
    private static string Detail(string what, int code, string output)
    {
        var reason = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return reason.Length == 0 ? $"{what} exit {code}" : $"{what} exit {code}: {reason}";
    }

    private static RunResult DefaultRunner(string file, string[] args, string? workingDirectory) =>
        ProcessRunner.Run(file, args, workingDirectory, ProcessRunner.DefaultTimeout);
}
