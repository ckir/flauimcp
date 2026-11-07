// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class ToolResponseLoggingTests
{
    [Fact]
    public async Task Internal_failure_logs_type_and_stack_and_the_error_carries_log()
    {
        var log = new CapturingLogger();
        using (ToolLog.Override(log, () => "c-12345678 in C:\\logs\\f.jsonl"))
        {
            var json = await ToolResponse.Guard(() => throw new InvalidOperationException("boom"));
            var root = JsonDocument.Parse(json).RootElement;
            Assert.Equal("INTERNAL", root.GetProperty("error").GetString());
            Assert.Equal("c-12345678 in C:\\logs\\f.jsonl", root.GetProperty("log").GetString());
        }
        var ev = log.Single("tool.exception");
        Assert.Equal("System.InvalidOperationException", ev.Fields.Single(f => f.Key == "exType").Value);
        Assert.False(string.IsNullOrEmpty(ev.Fields.Single(f => f.Key == "stack").Value as string));
        Assert.True(ev.Fields.Single(f => f.Key == "msg").UiText);
    }

    [Fact]
    public async Task ToolException_logs_no_exception_record_but_still_carries_log()
    {
        var log = new CapturingLogger();
        using (ToolLog.Override(log, () => "c-1 in x"))
        {
            var json = await ToolResponse.Guard(() => throw new ToolException(ToolErrorCode.SelectorNoMatch, "none", "retry"));
            Assert.Equal("c-1 in x", JsonDocument.Parse(json).RootElement.GetProperty("log").GetString());
        }
        Assert.Empty(log.Events);
    }

    [Fact]
    public async Task Without_a_pointer_there_is_no_log_key_at_all()
    {
        using (ToolLog.Override(null, () => null))
        {
            var json = await ToolResponse.Guard(() => throw new ToolException(ToolErrorCode.SelectorNoMatch, "none", "retry"));
            Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("log", out _));
            Assert.Equal("""{"error":"SelectorNoMatch","message":"none","suggestedRecovery":"retry"}""", json);
        }
    }

    [Fact]
    public async Task GuardImage_error_carries_log()
    {
        using (ToolLog.Override(new CapturingLogger(), () => "c-2 in y"))
        {
            var r = await ToolResponse.GuardImage(() => throw new InvalidOperationException("x"));
            var text = ((ModelContextProtocol.Protocol.TextContentBlock)r.Content.Single()).Text;
            Assert.Equal("c-2 in y", JsonDocument.Parse(text).RootElement.GetProperty("log").GetString());
        }
    }

    [Fact]
    public async Task OutOfMemory_still_propagates()
    {
        using (ToolLog.Override(new CapturingLogger(), () => "c in x"))
            await Assert.ThrowsAsync<OutOfMemoryException>(() => ToolResponse.Guard(() => throw new OutOfMemoryException()));
    }
}
