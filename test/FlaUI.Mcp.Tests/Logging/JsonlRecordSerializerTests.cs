// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class JsonlRecordSerializerTests
{
    private static readonly DateTime Ts = new(2026, 11, 7, 14, 3, 11, 412, DateTimeKind.Utc);
    private static readonly LogScope Call = new("c-3f9a1c2e", "desktop_click", false, false, false);

    private static JsonElement Parse(byte[] line)
    {
        Assert.Equal((byte)'\n', line[^1]);
        Assert.DoesNotContain((byte)'\n', line[..^1]);
        return JsonDocument.Parse(line.AsMemory(0, line.Length - 1)).RootElement;
    }

    [Fact]
    public void Fixed_fields_come_first_in_contract_order()
    {
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Warning, "tool.end", Call,
            new[] { LogEvent.F("outcome", "SelectorNoMatch"), LogEvent.F("ms", 2140) }), pid: 1234);
        var names = Parse(line).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "ts", "lvl", "pid", "ev", "cid", "tool", "outcome", "ms" }, names);
        var r = Parse(line);
        Assert.Equal("2026-11-07T14:03:11.412Z", r.GetProperty("ts").GetString());
        Assert.Equal("warning", r.GetProperty("lvl").GetString());
        Assert.Equal(1234, r.GetProperty("pid").GetInt32());
    }

    [Fact]
    public void No_scope_means_no_cid_or_tool()
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "server.start", null,
            Array.Empty<LogField>()), 1));
        Assert.False(r.TryGetProperty("cid", out _));
        Assert.False(r.TryGetProperty("tool", out _));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "debug")] [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")] [InlineData(LogLevel.Warning, "warning")]
    [InlineData(LogLevel.Error, "error")] [InlineData(LogLevel.Critical, "error")]
    public void Levels_map_to_four_names(LogLevel level, string expected)
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, level, "e", null, Array.Empty<LogField>()), 1));
        Assert.Equal(expected, r.GetProperty("lvl").GetString());
    }

    [Fact]
    public void Masked_value_serializes_as_len_sha8_object_and_IntPtr_as_number()
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "window.bind", null,
            new[] { LogEvent.F("hwnd", (IntPtr)0x2004C), LogEvent.F("title", new MaskedValue(12, "9b1e04aa")) }), 1));
        Assert.Equal(0x2004C, r.GetProperty("hwnd").GetInt64());
        Assert.Equal(12, r.GetProperty("title").GetProperty("len").GetInt32());
        Assert.Equal("9b1e04aa", r.GetProperty("title").GetProperty("sha8").GetString());
    }

    [Fact]
    public void A_newline_in_a_value_cannot_forge_a_record()
    {
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "e", null,
            new[] { LogEvent.F("x", "a\n{\"ev\":\"forged\"}") }), 1);
        Assert.Single(line, b => b == (byte)'\n');
    }

    [Fact]
    public void Oversized_stack_is_cut_first_and_msg_survives()
    {
        var stack = string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"   at Frame{i}()"));
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Error, "tool.exception", Call,
            new[] { LogEvent.F("exType", "System.Runtime.InteropServices.COMException"),
                    LogEvent.F("stack", stack), LogEvent.F("msg", "The remote procedure call failed.") }), 1);
        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes);
        var r = Parse(line);
        Assert.True(r.GetProperty("trunc").GetBoolean());
        Assert.Equal("The remote procedure call failed.", r.GetProperty("msg").GetString());
        Assert.StartsWith("   at Frame0()", r.GetProperty("stack").GetString());
    }

    [Fact]
    public void Oversized_args_are_omitted_last_and_fixed_fields_survive()
    {
        var args = new JsonObject();
        for (int i = 0; i < 3000; i++) args["k" + i] = i;   // many small numbers: no string to shorten
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "tool.start", Call,
            new[] { LogEvent.F("args", args) }), 1);
        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes);
        var r = Parse(line);
        Assert.True(r.GetProperty("args").GetProperty("omitted").GetInt32() > JsonlRecordSerializer.MaxRecordBytes);
        Assert.Equal("c-3f9a1c2e", r.GetProperty("cid").GetString());
    }

    [Fact]
    public void A_record_under_the_cap_is_not_marked_truncated()
    {
        var r = Parse(JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "e", null,
            new[] { LogEvent.F("a", "b") }), 1));
        Assert.False(r.TryGetProperty("trunc", out _));
    }
}
