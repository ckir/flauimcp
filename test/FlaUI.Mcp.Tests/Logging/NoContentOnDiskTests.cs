// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public sealed class NoContentOnDiskTests : IDisposable
{
    private const string Sentinel = "ZQX-sentinel-9f3a";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-nocontent-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class NotSensitive : ICallSensitivity
    {
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) => (false, false);
    }

    [Theory]
    [InlineData("desktop_type", """{"window":"w1","text":"ZQX-sentinel-9f3a"}""")]
    [InlineData("desktop_paste_text", """{"window":"w1","text":"ZQX-sentinel-9f3a"}""")]
    [InlineData("desktop_set_value", """{"window":"w1","value":"ZQX-sentinel-9f3a","ref":"e1"}""")]
    [InlineData("desktop_clipboard_set", """{"text":"ZQX-sentinel-9f3a"}""")]
    public async Task The_sentinel_bytes_never_reach_the_log_file(string tool, string json)
    {
        var hasher = new ContentHasher();
        var provider = JsonlFileLoggerProvider.TryCreate(new JsonlLogOptions(_dir, 1, DateTime.UtcNow, LogLevel.Trace),
            new LogMasker(hasher, _ => false), out _)!;
        using (var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)))
        {
            var filter = new ToolCallLogFilter(factory.CreateLogger("t"),
                ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly), new NotSensitive(), hasher);
            var args = JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            // The tool "fails" with a message that quotes the content, the worst case (spec §6.4).
            await filter.RunAsync(tool, args, () => new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text =
                    $$"""{"error":"INTERNAL","message":"rejected {{Sentinel}}","suggestedRecovery":null}""" } },
            }));
        }
        // An instance passed to AddProvider is not owned by the factory: dispose it to drain and close the file.
        provider.Dispose();
        var bytes = Directory.GetFiles(_dir).SelectMany(File.ReadAllBytes).ToArray();
        Assert.NotEmpty(bytes);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Sentinel)));
    }
}
