// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Tests.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spawns the real exe (harness: StdioProtocolCleanlinessTests), makes one failing call, and
/// checks stdout stays JSON-RPC and the log tells the story. Desktop: it spawns a process.</summary>
[Trait("Category", "Desktop")]
public sealed class ServerLogProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-proc-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task<string[]> RunAsync(params string[] serverArgs)
    {
        var psi = new ProcessStartInfo(StdioProtocolCleanlinessTests.LocateServerExe())
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in serverArgs) psi.ArgumentList.Add(a);
        psi.Environment["FLAUI_MCP_LOG_DIR"] = _dir;
        using var p = Process.Start(psi)!;
        var stdout = new System.Collections.Generic.List<string>();
        try
        {
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""");
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"desktop_focus_window","arguments":{"window":"w999"}}}""");
            await p.StandardInput.FlushAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (stdout.Count(l => l.Contains("\"id\":")) < 2)
            {
                var l = await p.StandardOutput.ReadLineAsync(cts.Token);
                if (l is null) break;
                if (l.Trim().Length > 0) stdout.Add(l);
            }
            p.StandardInput.Close();                     // clean shutdown -> server.stop
            await p.WaitForExitAsync(cts.Token);
        }
        finally { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        Assert.All(stdout, l => Assert.StartsWith("{", l.TrimStart()));
        return stdout.ToArray();
    }

    [Fact]
    public async Task One_failing_call_is_fully_correlated_in_the_log()
    {
        var stdout = await RunAsync();
        var callReply = stdout.Single(l => l.Contains("\"id\":2"));
        var segments = Directory.GetFiles(_dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var recs = JsonlLogReader.ReadSegments(segments).ToArray();
        Assert.Equal("server.start", recs.First().GetProperty("ev").GetString());
        Assert.Equal("server.stop", recs.Last().GetProperty("ev").GetString());
        var start = recs.Single(r => r.GetProperty("ev").GetString() == "tool.start");
        var end = recs.Single(r => r.GetProperty("ev").GetString() == "tool.end");
        var cid = start.GetProperty("cid").GetString()!;
        Assert.Equal(cid, end.GetProperty("cid").GetString());
        Assert.Equal("desktop_focus_window", start.GetProperty("tool").GetString());
        Assert.Equal("w999", start.GetProperty("args").GetProperty("window").GetString());  // catalog matched the SDK name
        Assert.NotEqual("ok", end.GetProperty("outcome").GetString());
        Assert.Contains($"{cid} in ", callReply);          // the error's `log` pointer names this cid
    }

    [Fact]
    public async Task No_log_flag_writes_no_file_and_no_pointer()
    {
        var stdout = await RunAsync("--no-log");
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
        Assert.DoesNotContain("\\\"log\\\"", stdout.Single(l => l.Contains("\"id\":2")));
    }
}
