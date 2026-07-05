using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Configures Claude Code via its stable `claude mcp add/remove` CLI. The runner returns the
/// process exit code, or -1 if the `claude` CLI is not found. Injected for testability.
/// </summary>
public sealed class ClaudeCodeConfigWriter
{
    private readonly Func<string, string[], int> _run;

    public ClaudeCodeConfigWriter(Func<string, string[], int>? runner = null)
        => _run = runner ?? DefaultRunner;

    public AgentResult Install(string exePath, IReadOnlyList<string>? args = null)
    {
        // --scope user: register globally for the user, NOT at the default "local" scope (which
        // binds to the installer's working dir, making the server invisible in every other project).
        // Remove-then-add so re-registration is idempotent and can change the args of an existing entry
        // (`claude mcp add` fails on a duplicate name). The remove is best-effort (no-op if absent).
        _run("claude", new[] { "mcp", "remove", "--scope", "user", McpServerEntry.ServerName });
        var addArgs = new List<string> { "mcp", "add", "--scope", "user", McpServerEntry.ServerName, "--", exePath };
        if (args is not null) addArgs.AddRange(args);
        var code = _run("claude", addArgs.ToArray());
        return code switch
        {
            0  => new AgentResult("claude", AgentChange.Created, "claude mcp add"),
            -1 => new AgentResult("claude", AgentChange.NotFound, "claude CLI not on PATH"),
            _  => new AgentResult("claude", AgentChange.Unchanged, $"claude mcp add exit {code}")
        };
    }

    /// <summary>Non-destructive-merge SHAPE, but NOT non-destructive BEHAVIOR: unlike AgyConfigWriter/
    /// GenericMcpConfigWriter, this writer has no locally-readable config — the registered args live inside
    /// the opaque `claude` CLI, and the injected runner returns only an exit code (no stdout), so there is no
    /// way to read back the previously-registered args to merge against. `removeArgs` is therefore unused;
    /// `addArgs` is passed through to the existing full-replace `Install(exePath, args)` verbatim, which
    /// matches this writer's PRE-EXISTING behavior for every verb (no regression — it never merged).</summary>
    public AgentResult Install(string exePath, IReadOnlyList<string> addArgs, IReadOnlyList<string> removeArgs) =>
        Install(exePath, addArgs);

    public AgentResult Uninstall()
    {
        // Must match the install scope (--scope user), else the user-scope entry is left orphaned.
        var code = _run("claude", new[] { "mcp", "remove", "--scope", "user", McpServerEntry.ServerName });
        return code == -1
            ? new AgentResult("claude", AgentChange.NotFound, "claude CLI not on PATH")
            : new AgentResult("claude", AgentChange.Removed, $"claude mcp remove exit {code}");
    }

    private static int DefaultRunner(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; } // executable not found
    }
}
