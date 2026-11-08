// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spec §7 "Roll fails (next segment cannot be created)": keep appending to the current segment
/// past the cap; never stop writing over a roll; nothing throws.</summary>
public sealed class JsonlProviderFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-logfail-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
    private const string Prefix = "flaui-mcp-4242-20261107T100000Z-";

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    [Fact]
    public void A_failed_roll_keeps_appending_to_the_current_segment_and_loses_nothing()
    {
        var opts = new JsonlLogOptions(_dir, Pid: 4242, ProcessStartUtc: Start, MinLevel: LogLevel.Trace)
        { SegmentBytes = 400, IsAlive = (_, _) => false };
        var p = JsonlFileLoggerProvider.TryCreate(opts, new LogMasker(new ContentHasher(new byte[32]), _ => false), out var error);
        Assert.Null(error);
        Assert.NotNull(p);

        // Squat on the next segment's path with a DIRECTORY: opening it as a file throws.
        var squat = Path.Combine(_dir, Prefix + "02.jsonl");
        Directory.CreateDirectory(squat);

        var log = p!.CreateLogger("x");
        var thrown = Record.Exception(() =>
        {
            for (int i = 0; i < 10; i++) LogEvent.Emit(log, LogLevel.Information, "e", LogEvent.F("pad", new string('x', 100)));
            LogEvent.Emit(log, LogLevel.Information, "final.distinct");
            p.Dispose();
        });
        Assert.Null(thrown);

        var files = Directory.GetFiles(_dir, "*.jsonl");               // files only; the squatting directory is not listed
        var first = Assert.Single(files);
        Assert.EndsWith(Prefix + "01.jsonl", first);
        Assert.True(Directory.Exists(squat));
        Assert.True(new FileInfo(first).Length > 400, "the segment must grow past the cap when the roll fails");

        var lines = File.ReadAllLines(first);
        Assert.Equal(11, lines.Length);                                 // nothing lost, nothing dropped
        Assert.Equal("final.distinct", JsonDocument.Parse(lines[^1]).RootElement.GetProperty("ev").GetString());
        Assert.Equal(0, p.DroppedTotal);
        Assert.DoesNotContain(lines, l => l.Contains("log.dropped"));
    }
}
