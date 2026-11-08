// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spec §6.3 (sensitive UI text) and §6.4 (a thrown exception that quotes content), on disk.</summary>
public sealed class SensitiveAndExceptionOnDiskTests
{
    private const string Sentinel = "ZQX-sentinel-9f3a";
    private static readonly byte[] SentinelBytes = Encoding.UTF8.GetBytes(Sentinel);

    private sealed class Fixed(bool sensitive) : ICallSensitivity
    {
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) => (sensitive, false);
    }

    private static ToolCallLogFilter Filter(OnDiskHarness h, bool sensitive) =>
        new(h.Factory.CreateLogger("t"), ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly), new Fixed(sensitive), h.Hasher);

    private static ValueTask<CallToolResult> NoMatch() => new(new CallToolResult
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text =
            $$"""{"error":"SelectorNoMatch","message":"no element named {{Sentinel}}","suggestedRecovery":null}""" } },
    });

    private const string FindText = """{"window":"w1","query":"ZQX-sentinel-9f3a"}""";

    [Fact]
    public async Task Sensitive_call_ui_text_never_reaches_the_file()
    {
        using var h = new OnDiskHarness(_ => true);
        await Filter(h, sensitive: true).RunAsync("desktop_find_text", OnDiskHarness.Args(FindText), NoMatch);
        var bytes = h.CloseAndReadAll();
        Assert.NotEmpty(bytes);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(SentinelBytes));
    }

    [Fact]
    public async Task Control_a_non_sensitive_call_does_write_the_raw_ui_text()
    {
        using var h = new OnDiskHarness(_ => false);
        await Filter(h, sensitive: false).RunAsync("desktop_find_text", OnDiskHarness.Args(FindText), NoMatch);
        var bytes = h.CloseAndReadAll();
        Assert.NotEqual(-1, bytes.AsSpan().IndexOf(SentinelBytes));
    }

    [Fact]
    public async Task A_thrown_exception_quoting_content_is_masked_in_tool_end()
    {
        using var h = new OnDiskHarness(_ => false);
        var filter = Filter(h, sensitive: false);
        var args = OnDiskHarness.Args("""{"window":"w1","text":"ZQX-sentinel-9f3a"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await filter.RunAsync("desktop_type", args, () => throw new InvalidOperationException("rejected " + Sentinel)));
        var bytes = h.CloseAndReadAll();
        Assert.NotEmpty(bytes);
        Assert.Contains("\"INTERNAL\"", Encoding.UTF8.GetString(bytes));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(SentinelBytes));
    }

    [Fact]
    public async Task ToolResponse_Guard_tool_exception_quoting_content_is_masked_inside_a_filter_scope()
    {
        using var h = new OnDiskHarness(_ => false);
        var filter = Filter(h, sensitive: false);
        var args = OnDiskHarness.Args("""{"window":"w1","text":"ZQX-sentinel-9f3a"}""");
        using (ToolLog.Override(h.Factory.CreateLogger("tool"), () => null))
        {
            // Guard swallows the exception and logs tool.exception; the call itself then returns ok, so
            // tool.end carries no msg and the only place the sentinel could land is tool.exception.
            await filter.RunAsync("desktop_type", args, async () =>
            {
                await ToolResponse.Guard(() => throw new InvalidOperationException("rejected " + Sentinel));
                return new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"ok\":true}" } } };
            });
        }
        var bytes = h.CloseAndReadAll();
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("tool.exception", text);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(SentinelBytes));
    }
}
