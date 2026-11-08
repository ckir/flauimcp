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

/// <summary>Spec §5.1 truncation order, steps 2 and 3 (step 1 and the fallbacks are pinned in
/// JsonlRecordSerializerTests). Each test fails if its own loop in JsonlRecordSerializer.Serialize is removed.</summary>
public class JsonlTruncationOrderTests
{
    private static readonly DateTime Ts = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
    private static readonly UTF8Encoding Strict = new(false, true);

    private static JsonElement ParseStrict(byte[] line)
    {
        Assert.Equal((byte)'\n', line[^1]);
        Assert.Single(line, b => b == (byte)'\n');
        return JsonDocument.Parse(Strict.GetString(line, 0, line.Length - 1)).RootElement;
    }

    [Fact]
    public void Step2_shortens_the_longest_args_leaf_and_keeps_args_and_msg()
    {
        var args = new JsonObject { ["text"] = new string('a', 20000), ["short"] = "keep" };
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "tool.start", null,
            new[] { LogEvent.F("args", args), LogEvent.F("msg", "short message") }), 1);

        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes, $"length {line.Length}");
        var r = ParseStrict(line);
        Assert.True(r.GetProperty("trunc").GetBoolean());
        var a = r.GetProperty("args");
        Assert.False(a.TryGetProperty("omitted", out _), "args must not be replaced by omitted");
        var text = a.GetProperty("text").GetString()!;
        Assert.True(text.Length < 20000 && text.Length > 16);
        Assert.EndsWith("...", text);
        Assert.StartsWith("aaaa", text);
        Assert.Equal("keep", a.GetProperty("short").GetString());
        Assert.Equal("short message", r.GetProperty("msg").GetString());
    }

    [Fact]
    public void Step3_halves_msg_with_an_ellipsis_when_there_is_nothing_else_to_cut()
    {
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Warning, "tool.end", null,
            new[] { LogEvent.F("msg", new string('m', 20000)) }), 1);

        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes, $"length {line.Length}");
        var r = ParseStrict(line);
        Assert.True(r.GetProperty("trunc").GetBoolean());
        Assert.False(r.TryGetProperty("args", out _));
        var msg = r.GetProperty("msg").GetString()!;
        Assert.True(msg.Length < 20000 && msg.Length > 16);
        Assert.EndsWith("...", msg);
        Assert.StartsWith("mmmm", msg);
    }

    [Theory]
    [InlineData("\u00e9")]          // 2 UTF-8 bytes per char
    [InlineData("\u4e2d")]          // CJK, 3 bytes per char
    public void Non_ascii_msg_is_cut_to_the_byte_cap_and_stays_valid_utf8(string unit)
    {
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Warning, "tool.end", null,
            new[] { LogEvent.F("msg", string.Concat(Enumerable.Repeat(unit, 20000))) }), 1);

        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes, $"length {line.Length}");
        var r = ParseStrict(line);   // strict UTF-8 decode + JSON parse
        Assert.True(r.GetProperty("trunc").GetBoolean());
        var msg = r.GetProperty("msg").GetString()!;
        Assert.EndsWith("...", msg);
        Assert.StartsWith(unit + unit, msg);
    }

    [Fact]
    public void Astral_msg_is_cut_to_the_byte_cap_and_stays_valid_utf8()
    {
        // "x" + surrogate pairs: the halving index lands between the two halves of a pair.
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Warning, "tool.end", null,
            new[] { LogEvent.F("msg", "x" + string.Concat(Enumerable.Repeat("\U0001F600", 10000))) }), 1);

        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes, $"length {line.Length}");
        Assert.True(ParseStrict(line).GetProperty("trunc").GetBoolean());
    }

    [Fact]
    public void Non_ascii_args_leaf_is_cut_to_the_byte_cap_and_stays_valid_utf8()
    {
        var args = new JsonObject { ["text"] = string.Concat(Enumerable.Repeat("\u4e2d", 20000)) };
        var line = JsonlRecordSerializer.Serialize(new LogRecord(Ts, LogLevel.Information, "tool.start", null,
            new[] { LogEvent.F("args", args) }), 1);

        Assert.True(line.Length <= JsonlRecordSerializer.MaxRecordBytes, $"length {line.Length}");
        var r = ParseStrict(line);
        Assert.False(r.GetProperty("args").TryGetProperty("omitted", out _));
        Assert.EndsWith("...", r.GetProperty("args").GetProperty("text").GetString());
    }
}
