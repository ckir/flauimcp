using System;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// The single home of the CLI launch orchestration. `agy`/`claude` are often npm .cmd shims that
/// CreateProcess cannot launch by bare name, so every call goes through `cmd /C <cli> <args…>`
/// (cmd resolves the shim on PATH). Args are passed as DISCRETE elements — never one concatenated
/// string (a single quoted blob would be treated as the exe name). ProcessRunner already captures
/// stdout/stderr via pipes, so NO `> tmp 2>&1` shell redirect is used. Absent CLI => NotFound.
public sealed class CliInvoker
{
    private readonly Func<string, string[], string?, RunResult> _run;
    private readonly Func<string, string?> _resolve;

    public CliInvoker(Func<string, string[], string?, RunResult> run, Func<string, string?> resolve)
    {
        _run = run;
        _resolve = resolve;
    }

    /// True when the CLI resolves on PATH (used to gate skip-vs-run at the caller).
    public bool IsPresent(string cli) => _resolve(cli) is not null;

    public RunResult Invoke(string cli, params string[] args)
    {
        var resolved = _resolve(cli);
        if (resolved is null) return new RunResult(ProcessRunner.NotFound, "");
        // Bare `cli` after /C (NOT the quoted full path): a quoted first token triggers cmd's two-quote-pair
        // stripping when an arg also has spaces (e.g. a profile path under a username with a space). Instead set
        // the child working directory to the resolved CLI's dir — cmd searches cwd BEFORE PATH, so bare `cli`
        // resolves even when the CLI lives only in a fallback dir not on the inherited PATH. (plan-review R2.)
        var cliDir = System.IO.Path.GetDirectoryName(resolved);
        var full = new[] { "/C", cli }.Concat(args).ToArray();
        return _run("cmd.exe", full, cliDir);
    }
}
