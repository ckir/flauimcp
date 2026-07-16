using System.Diagnostics;
using System.Text;

namespace FlaUI.Mcp.Server.Install;

/// <summary>Outcome of a child process: its exit code plus whatever it wrote to stdout.</summary>
public readonly record struct RunResult(int Code, string Output);

/// <summary>
/// Runs a child process with a BOUNDED wait. Setup launches us `runhidden`
/// (installer/flaui-mcp.iss), so a child that blocks on stdin has no window, no console, and no
/// human — it would hang Setup forever and the user would never get the product. Nothing may wait
/// on a hidden child without a deadline.
/// </summary>
public static class ProcessRunner
{
    /// <summary>The executable was not found on PATH.</summary>
    public const int NotFound = -1;
    /// <summary>The child exceeded its deadline and was killed.</summary>
    public const int TimedOut = -2;

    /// <summary>Generous enough for a slow cold start, short enough that Setup always finishes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static RunResult Run(string file, string[] args, string? workingDirectory, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Redirected explicitly (not just left to inherit) so the child always gets a real,
                // open pipe. Setup launches US hidden, so OUR OWN stdin may already be closed/EOF;
                // without this, a child that inherits that closed handle sees immediate EOF and
                // returns instantly instead of blocking — masking exactly the hang this class exists
                // to guard against. We never write to it, so anything that reads stdin blocks until
                // the timeout kills it.
                RedirectStandardInput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;

            using var p = Process.Start(psi);
            if (p is null) return new RunResult(NotFound, "");

            // Read asynchronously: a child that fills the stdout pipe while we block on WaitForExit
            // would deadlock, which is the same hang by another route. Both streams are drained for
            // that reason. stderr is KEPT rather than discarded: when `claude` fails, its reason is
            // usually on stderr, and our warning would otherwise say only "exited 1".
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new RunResult(TimedOut, stdout.ToString());
            }
            p.WaitForExit();   // flush the async output handlers (see WaitForExit(int) remarks)

            // On success the caller wants clean stdout (it may be JSON). On failure it wants the
            // reason, which lives on stderr.
            return p.ExitCode == 0
                ? new RunResult(0, stdout.ToString())
                : new RunResult(p.ExitCode, (stdout.ToString() + stderr.ToString()).Trim());
        }
        catch (System.ComponentModel.Win32Exception) { return new RunResult(NotFound, ""); }
    }
}
