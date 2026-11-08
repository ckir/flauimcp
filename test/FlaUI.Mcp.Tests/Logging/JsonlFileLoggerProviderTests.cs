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

public sealed class JsonlFileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-log-test-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private JsonlFileLoggerProvider Create(long segmentBytes = 5 * 1024 * 1024, int capacity = 4096,
        Func<int, DateTime, bool>? isAlive = null)
    {
        var opts = new JsonlLogOptions(_dir, Pid: 4242, ProcessStartUtc: Start, MinLevel: LogLevel.Trace)
        { SegmentBytes = segmentBytes, ChannelCapacity = capacity, IsAlive = isAlive ?? ((_, _) => false) };
        var masker = new LogMasker(new ContentHasher(new byte[32]), _ => false);
        var p = JsonlFileLoggerProvider.TryCreate(opts, masker, out var error);
        Assert.Null(error);
        return p!;
    }

    private string[] Lines() => Directory.GetFiles(_dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal)
        .SelectMany(File.ReadAllLines).ToArray();

    [Fact]
    public void Writes_one_line_per_event_to_the_pid_start_segment_file()
    {
        using (var p = Create())
        {
            var log = p.CreateLogger("x");
            LogEvent.Emit(log, LogLevel.Information, "server.start", LogEvent.F("version", "1.1.0"));
            Assert.EndsWith("flaui-mcp-4242-20261107T100000Z-01.jsonl", p.CurrentFilePath);
        }
        var line = Assert.Single(Lines());
        Assert.Equal("server.start", JsonDocument.Parse(line).RootElement.GetProperty("ev").GetString());
    }

    [Fact]
    public void Framework_records_are_not_written()
    {
        using (var p = Create()) p.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("Application started.");
        Assert.Empty(Lines());
    }

    [Fact]
    public void Scope_values_are_captured_when_Log_is_called()
    {
        // The scope is disposed before the writer thread dequeues; the record must still carry its cid.
        using (var p = Create())
        {
            var factory = LoggerFactory.Create(b => b.AddProvider(p));
            var log = factory.CreateLogger("x");
            using (log.BeginScope(new LogScope("c-11111111", "desktop_click", false, false, false)))
                LogEvent.Emit(log, LogLevel.Information, "tool.start");
        }
        Assert.Equal("c-11111111", JsonDocument.Parse(Assert.Single(Lines())).RootElement.GetProperty("cid").GetString());
    }

    [Fact]
    public void Rolls_to_a_new_segment_without_renaming_and_keeps_five()
    {
        using (var p = Create(segmentBytes: 400))
        {
            var log = p.CreateLogger("x");
            for (int i = 0; i < 60; i++) LogEvent.Emit(log, LogLevel.Information, "e", LogEvent.F("pad", new string('x', 100)));
        }
        var files = Directory.GetFiles(_dir, "*.jsonl").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(5, files.Length);
        Assert.All(files, f => Assert.Matches(@"^flaui-mcp-4242-20261107T100000Z-\d{2}\.jsonl$", f!));
        Assert.DoesNotContain("flaui-mcp-4242-20261107T100000Z-01.jsonl", files);   // oldest deleted
    }

    [Fact]
    public void A_segment_held_open_by_a_reader_does_not_stop_writing()
    {
        using var p = Create(segmentBytes: 400);
        var log = p.CreateLogger("x");
        LogEvent.Emit(log, LogLevel.Information, "e", LogEvent.F("pad", new string('x', 100)));
        p.FlushForTest();
        using var reader = new FileStream(p.CurrentFilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        for (int i = 0; i < 60; i++) LogEvent.Emit(log, LogLevel.Information, "e", LogEvent.F("pad", new string('x', 100)));
        p.FlushForTest();
        Assert.True(Directory.GetFiles(_dir, "*.jsonl").Length >= 5);
    }

    [Fact]
    public void Full_channel_drops_counts_and_reports_log_dropped()
    {
        using (var p = Create(capacity: 1))
        {
            p.PauseWriterForTest();
            var log = p.CreateLogger("x");
            for (int i = 0; i < 50; i++) LogEvent.Emit(log, LogLevel.Information, "e");
            Assert.True(p.DroppedTotal >= 40);
            p.ResumeWriterForTest();
            LogEvent.Emit(log, LogLevel.Information, "after");
        }
        Assert.Contains(Lines(), l => l.Contains("\"ev\":\"log.dropped\""));
    }

    [Fact]
    public void Log_dropped_record_carries_the_dropped_count()
    {
        // Code (JsonlFileLoggerProvider.WriterLoop): count = drops SINCE THE LAST REPORT (_dropped - _droppedReported),
        // not DroppedTotal. With every drop made while paused, the first report carries all of them.
        long droppedBeforeResume;
        using (var p = Create(capacity: 1))
        {
            p.PauseWriterForTest();
            var log = p.CreateLogger("x");
            for (int i = 0; i < 50; i++) LogEvent.Emit(log, LogLevel.Information, "e");
            droppedBeforeResume = p.DroppedTotal;
            Assert.True(droppedBeforeResume >= 40);
            p.ResumeWriterForTest();
            p.FlushForTest();
            LogEvent.Emit(log, LogLevel.Information, "after");   // channel is empty: accepted, not dropped
        }
        var counts = Lines().Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("ev").GetString() == "log.dropped")
            .Select(e => e.GetProperty("count").GetInt64()).ToArray();
        Assert.NotEmpty(counts);
        Assert.Equal(droppedBeforeResume, counts[0]);
        Assert.Equal(droppedBeforeResume, counts.Sum());
    }

    [Fact]
    public void Prunes_dead_process_files_keeping_the_newest_twenty_and_never_a_live_one()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 25; i++)
        {
            var f = Path.Combine(_dir, $"flaui-mcp-{1000 + i}-20261101T000000Z-01.jsonl");
            File.WriteAllText(f, "{}\n");
            File.SetLastWriteTimeUtc(f, Start.AddMinutes(-100 + i));
        }
        var live = Path.Combine(_dir, "flaui-mcp-7777-20261101T000000Z-01.jsonl");
        File.WriteAllText(live, "{}\n");
        File.SetLastWriteTimeUtc(live, Start.AddDays(-30));
        JsonlFileLoggerProvider.PruneDead(_dir, keepDeadFiles: 20, isAlive: (pid, _) => pid == 7777);
        var left = Directory.GetFiles(_dir, "*.jsonl").Select(Path.GetFileName).ToArray();
        Assert.Contains("flaui-mcp-7777-20261101T000000Z-01.jsonl", left);
        Assert.Equal(21, left.Length);
        Assert.DoesNotContain("flaui-mcp-1000-20261101T000000Z-01.jsonl", left);   // oldest dead gone
    }

    [Fact]
    public void Shutdown_drains_and_the_stop_record_is_last()
    {
        using (var p = Create())
        {
            var log = p.CreateLogger("x");
            for (int i = 0; i < 200; i++) LogEvent.Emit(log, LogLevel.Information, "e");
            LogEvent.Emit(log, LogLevel.Information, "server.stop");
        }
        var lines = Lines();
        Assert.Equal(201, lines.Length);
        Assert.Contains("\"ev\":\"server.stop\"", lines[^1]);
    }

    [Fact]
    public void TryCreate_reports_an_unusable_directory_instead_of_throwing()
    {
        var file = Path.Combine(Path.GetTempPath(), "flaui-log-notadir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            var opts = new JsonlLogOptions(file, 1, Start, LogLevel.Information);
            var p = JsonlFileLoggerProvider.TryCreate(opts, new LogMasker(new ContentHasher(), _ => false), out var error);
            Assert.Null(p);
            Assert.False(string.IsNullOrEmpty(error));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Reader_rule_skips_only_a_torn_final_line()
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "flaui-mcp-1-20261101T000000Z-01.jsonl");
        File.WriteAllText(f, "{\"ev\":\"a\"}\n{\"ev\":\"b\"}\n{\"ev\":\"c");
        var evs = JsonlLogReader.ReadSegments(new[] { f }).Select(e => e.GetProperty("ev").GetString()).ToArray();
        Assert.Equal(new[] { "a", "b" }, evs);
    }
}
