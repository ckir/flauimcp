// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class ToolCallLogFilterTests
{
    private static readonly ToolParameterCatalog Catalog = ToolParameterCatalog.FromTypes(new[] { typeof(FakeTools) });

    private sealed class FixedSensitivity(bool sensitive, bool targetless = false) : ICallSensitivity
    {
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) => (sensitive, targetless);
    }

    private static (ToolCallLogFilter Filter, CapturingLogger Log) Make(bool sensitive = false, bool targetless = false)
    {
        var log = new CapturingLogger();
        return (new ToolCallLogFilter(log, Catalog, new FixedSensitivity(sensitive, targetless), new ContentHasher(new byte[32])), log);
    }

    private static IDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static CallToolResult Text(string s) => new() { Content = new List<ContentBlock> { new TextContentBlock { Text = s } } };

    [Fact]
    public async Task Emits_start_and_end_sharing_a_cid_and_returns_the_result_unchanged()
    {
        var (f, log) = Make();
        var expected = Text("""{"ok":true}""");
        LogScope? seen = null;
        var r = await f.RunAsync("desktop_fake_type", Args("""{"window":"w1","text":"abc"}"""),
            () => { seen = LogContext.Current; return new ValueTask<CallToolResult>(expected); });
        Assert.Same(expected, r);
        var start = log.Single("tool.start");
        var end = log.Single("tool.end");
        Assert.Equal("ok", end.Fields.Single(x => x.Key == "outcome").Value);
        Assert.NotNull(seen);
        Assert.Matches("^c-[0-9a-f]{8}$", seen!.Cid);
        Assert.True(seen.HasContentArgs);
        Assert.Null(LogContext.Current);              // restored after the call
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task Error_json_result_is_read_as_the_outcome_with_its_message()
    {
        var (f, log) = Make();
        await f.RunAsync("desktop_fake_find", Args("""{"window":"w1"}"""),
            () => new ValueTask<CallToolResult>(Text("""{"error":"SelectorNoMatch","message":"no element","suggestedRecovery":null}""")));
        var end = log.Single("tool.end");
        Assert.Equal("SelectorNoMatch", end.Fields.Single(x => x.Key == "outcome").Value);
        var msg = end.Fields.Single(x => x.Key == "msg");
        Assert.Equal("no element", msg.Value);
        Assert.True(msg.UiText);
    }

    [Fact]
    public async Task Ambiguous_match_on_a_targetless_call_omits_msg()
    {
        var (f, log) = Make(targetless: true);
        await f.RunAsync("desktop_fake_find", Args("{}"),
            () => new ValueTask<CallToolResult>(Text("""{"error":"AmbiguousMatch","message":"2 windows titled 'KeePass': ...","suggestedRecovery":"x"}""")));
        Assert.DoesNotContain(log.Single("tool.end").Fields, x => x.Key == "msg");
    }

    [Fact]
    public async Task A_throwing_next_logs_INTERNAL_and_rethrows()
    {
        var (f, log) = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await f.RunAsync("desktop_fake_find", Args("{}"), () => throw new InvalidOperationException("x")));
        Assert.Equal("INTERNAL", log.Single("tool.end").Fields.Single(x => x.Key == "outcome").Value);
    }

    [Fact]
    public async Task A_failing_classifier_never_blocks_the_call()
    {
        var log = new CapturingLogger();
        var f = new ToolCallLogFilter(log, Catalog, new ThrowingSensitivity(), new ContentHasher());
        var expected = Text("{}");
        Assert.Same(expected, await f.RunAsync("desktop_fake_find", Args("{}"), () => new ValueTask<CallToolResult>(expected)));
    }

    private sealed class ThrowingSensitivity : ICallSensitivity
    {
        // Tuple element names must match the interface's (CS8141).
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) =>
            throw new InvalidOperationException();
    }
}
