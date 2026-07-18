using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>Regression guard: an MCP stdio server MUST keep stdout as pure JSON-RPC. The .NET generic
/// host's default console logger writes to stdout; that corrupted the protocol stream and made strict
/// clients (agy / Antigravity) refuse to load the server, while lenient clients tolerated it (so it
/// shipped unnoticed). Program.cs now routes all host/framework logs to stderr. This test spawns the
/// real built exe, performs the MCP `initialize` handshake, and asserts the first stdout line is a
/// JSON-RPC message — not an "info:" log line. Console-run (spawns a process); Category=Desktop.</summary>
[Trait("Category", "Desktop")]
public class StdioProtocolCleanlinessTests
{
    [Fact]
    public async Task Server_stdout_carries_only_jsonrpc_never_framework_logs()
    {
        var exe = LocateServerExe();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new Xunit.Sdk.XunitException($"could not start {exe}");
        try
        {
            // Send a valid initialize and KEEP stdin open so the server flushes its response before EOF.
            await p.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":" +
                "\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"0\"}}}");
            await p.StandardInput.FlushAsync();

            // Read the first NON-EMPTY stdout line within a timeout.
            string? line = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (true)
                {
                    var l = await p.StandardOutput.ReadLineAsync(cts.Token);
                    if (l is null) break;                 // stdout closed
                    if (l.Trim().Length == 0) continue;   // skip blank lines
                    line = l;
                    break;
                }
            }
            catch (OperationCanceledException) { }

            Assert.False(string.IsNullOrWhiteSpace(line),
                "the server produced no stdout line within the timeout — expected a JSON-RPC initialize response.");

            // THE REGRESSION: pre-fix, the first stdout line was a framework log ("info: ..."). stdout
            // must instead be pure JSON-RPC.
            Assert.DoesNotContain("info:", line);
            Assert.StartsWith("{", line!.TrimStart());
            Assert.Contains("\"jsonrpc\"", line);
        }
        finally
        {
            try { p.StandardInput.Close(); } catch { }
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static string LocateServerExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var config = baseDir.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlaUI.Mcp.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repo root (FlaUI.Mcp.slnx) from the test assembly.");
        var exe = Path.Combine(dir!.FullName, "src", "FlaUI.Mcp.Server", "bin", config,
            "net10.0-windows10.0.19041.0", "win-x64", "flaui-mcp.exe");
        Assert.True(File.Exists(exe), $"server exe not found at {exe} — build FlaUI.Mcp.Server first.");
        return exe;
    }
}
