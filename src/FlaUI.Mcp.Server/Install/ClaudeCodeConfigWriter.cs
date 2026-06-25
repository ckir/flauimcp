using System.Diagnostics;

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

    public AgentResult Install(string exePath)
    {
        var code = _run("claude", new[] { "mcp", "add", McpServerEntry.ServerName, "--", exePath });
        return code switch
        {
            0  => new AgentResult("claude", AgentChange.Created, "claude mcp add"),
            -1 => new AgentResult("claude", AgentChange.NotFound, "claude CLI not on PATH"),
            _  => new AgentResult("claude", AgentChange.Unchanged, $"claude mcp add exit {code}")
        };
    }

    public AgentResult Uninstall()
    {
        var code = _run("claude", new[] { "mcp", "remove", McpServerEntry.ServerName });
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
