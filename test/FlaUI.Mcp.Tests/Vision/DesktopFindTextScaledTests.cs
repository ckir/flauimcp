// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Tests.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

/// <summary>The SCALED-DISPLAY half of the find_text coordinate guarantee, which DesktopFindTextTests cannot check.
///
/// DesktopFindTextTests runs the OCR pipeline in-process, and the xUnit test host is NOT DPI-aware. At 100% that does
/// not matter, and it passes on the Desktop gate. At any other scale the host is DPI-virtualized, so its UIA bounds
/// and the physical screen it captures disagree (MEASURED at 125%: the OCR match landed ~92 px below the button) —
/// a test-host artifact, not a product defect, because the production server is PerMonitorV2 DPI-aware
/// (src/FlaUI.Mcp.Server/app.manifest).
///
/// So this test checks the PRODUCTION process: it starts the built flaui-mcp server exe over stdio, exactly as an
/// MCP client would, and asks it for desktop_find_text "Rebuild Items" and for RebuildItemsButton's bounds. The match
/// centre must lie inside the button.
///
/// It FAILS — never skips — when the display is at 100%: a pass there would prove nothing that DesktopFindTextTests
/// does not already prove, and the Desktop gate counts skips as failures. It therefore carries the ScaledDisplay
/// trait, which both gate filters exclude (scripts/desktop-suite.ps1, DevelopersCockpit.ps1 K). Run it on a scaled
/// session (RDP at 125% on the reference machine) when cutting a release: see docs/building.md.</summary>
[Trait("Category", "Desktop")]
[Trait("Category", "ScaledDisplay")]
public class DesktopFindTextScaledTests
{
    [Fact]
    public async Task Find_text_match_centre_lands_inside_the_control_on_a_scaled_display()
    {
        using var app = new TestAppFixture();
        await using var server = await McpStdioServer.StartAsync(StdioProtocolCleanlinessTests.LocateServerExe());

        var open = await server.CallToolAsync("desktop_open_window",
            new JsonObject { ["by"] = "pid", ["value"] = app.Process.Id.ToString() });
        var window = open["handle"]!.GetValue<string>();

        var find = await server.CallToolAsync("desktop_find",
            new JsonObject { ["window"] = window, ["automationId"] = "RebuildItemsButton" });
        var match = Assert.Single(find["matches"]!.AsArray());
        var buttonRef = match!["ref"]!.GetValue<string>();

        var bounds = await server.CallToolAsync("desktop_get_bounds",
            new JsonObject { ["window"] = window, ["ref"] = buttonRef });
        double dpiScale = bounds["dpiScale"]!.GetValue<double>();
        Assert.True(dpiScale > 1.0,
            $"this test needs a SCALED display, but the server reports dpiScale={dpiScale} for the button. At 100% " +
            "it would prove nothing beyond DesktopFindTextTests. Run it on a scaled session (e.g. RDP at 125%), " +
            "see docs/building.md 'Cutting a release'.");
        var b = bounds["bounds"]!;
        int bx = b["x"]!.GetValue<int>(), by = b["y"]!.GetValue<int>(),
            bw = b["w"]!.GetValue<int>(), bh = b["h"]!.GetValue<int>();

        // find_text SCRAPES the screen, so the TestApp must be on top when it captures.
        await ForegroundTaker.TakeAsync(app.Process.MainWindowHandle);
        var text = await server.CallToolAsync("desktop_find_text",
            new JsonObject { ["query"] = "Rebuild Items", ["window"] = window });
        var matches = text["matches"]!.AsArray();
        Assert.True(matches.Count > 0, $"expected at least one OCR match for 'Rebuild Items', got: {text.ToJsonString()}");
        var centre = matches[0]!["center"]!.AsArray();
        int cx = centre[0]!.GetValue<int>(), cy = centre[1]!.GetValue<int>();

        Assert.True(cx >= bx && cx <= bx + bw && cy >= by && cy <= by + bh,
            $"OCR match centre ({cx},{cy}) is not inside RebuildItemsButton's bounds x={bx} y={by} w={bw} h={bh} " +
            $"at dpiScale={dpiScale}; match={matches[0]!.ToJsonString()}");
    }

    /// <summary>A minimal MCP stdio client: newline-delimited JSON-RPC, one request at a time.</summary>
    private sealed class McpStdioServer : IAsyncDisposable
    {
        private readonly Process _p;
        private int _nextId = 1;

        private McpStdioServer(Process p) => _p = p;

        public static async Task<McpStdioServer> StartAsync(string exe)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            var p = Process.Start(psi) ?? throw new Xunit.Sdk.XunitException($"could not start {exe}");
            p.ErrorDataReceived += (_, _) => { };   // drain stderr so the server never blocks on a full pipe
            p.BeginErrorReadLine();
            var s = new McpStdioServer(p);
            await s.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "DesktopFindTextScaledTests", ["version"] = "0" },
            });
            await s.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return s;
        }

        /// <summary>Calls a tool and returns its JSON payload. A tool-level error ({"error":...}) fails the test.</summary>
        public async Task<JsonNode> CallToolAsync(string name, JsonObject args)
        {
            var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args });
            var textBlock = result["content"]!.AsArray().First(c => c!["type"]!.GetValue<string>() == "text");
            var payload = JsonNode.Parse(textBlock!["text"]!.GetValue<string>())!;
            if (payload is JsonObject o && o.ContainsKey("error"))
                throw new Xunit.Sdk.XunitException($"{name} returned an error: {payload.ToJsonString()}");
            return payload;
        }

        private async Task<JsonNode> RequestAsync(string method, JsonObject @params)
        {
            int id = _nextId++;
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var line = await _p.StandardOutput.ReadLineAsync(cts.Token)
                           ?? throw new Xunit.Sdk.XunitException($"the server closed stdout while awaiting {method}");
                if (line.Trim().Length == 0) continue;
                var msg = JsonNode.Parse(line)!;
                if (msg["id"] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || v.GetValue<int>() != id)
                    continue;   // a notification or an unrelated message
                if (msg["error"] is { } err)
                    throw new Xunit.Sdk.XunitException($"{method} failed at the protocol level: {err.ToJsonString()}");
                return msg["result"]!;
            }
        }

        private async Task WriteAsync(JsonObject message)
        {
            await _p.StandardInput.WriteLineAsync(message.ToJsonString());
            await _p.StandardInput.FlushAsync();
        }

        public ValueTask DisposeAsync()
        {
            try { _p.StandardInput.Close(); } catch (IOException) { }
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _p.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
