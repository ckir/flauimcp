// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Server.Logging;

public sealed record JsonlLogOptions(string Directory, int Pid, DateTime ProcessStartUtc, LogLevel MinLevel)
{
    public long SegmentBytes { get; init; } = 5 * 1024 * 1024;
    public int KeepSegments { get; init; } = 5;
    public int ChannelCapacity { get; init; } = 4096;
    public Func<int, DateTime, bool> IsAlive { get; init; } = JsonlFileLoggerProvider.DefaultIsAlive;

    /// <summary>`FLAUI_MCP_LOG_DIR`, else `%LOCALAPPDATA%\flaui-mcp\logs`.</summary>
    public static string DefaultDirectory() =>
        Environment.GetEnvironmentVariable("FLAUI_MCP_LOG_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flaui-mcp", "logs");
}

/// <summary>Writes LogEventState records as JSON Lines (spec §4.1). Producers never block; one background
/// thread owns the file. Never writes to stdout.</summary>
public sealed class JsonlFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private static readonly Regex FileName = new(@"^flaui-mcp-(?<pid>\d+)-(?<start>\d{8}T\d{6}Z)-(?<seg>\d{2,})\.jsonl$");   // two digits minimum; a long run passes 99

    private readonly JsonlLogOptions _o;
    private readonly LogMasker _masker;
    private readonly Channel<LogRecord> _channel;
    private readonly Thread _writer;
    private readonly string _prefix;
    private IExternalScopeProvider? _scopes;
    private FileStream? _stream;
    private int _segment;
    private long _bytes;
    private long _dropped, _droppedReported, _droppedTotal;
    private DateTime _reopenAfter;
    private volatile string? _currentPath;
    private readonly ManualResetEventSlim _paused = new(true);
    private readonly ManualResetEventSlim _idle = new(true);

    private JsonlFileLoggerProvider(JsonlLogOptions o, LogMasker masker, FileStream first)
    {
        _o = o; _masker = masker; _stream = first; _segment = 1;
        _prefix = $"flaui-mcp-{o.Pid}-{o.ProcessStartUtc.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}-";
        _currentPath = Path.GetFullPath(SegmentPath(1)); // not FileStream.Name: the redaction source sweep flags every .Name read
        _bytes = first.Length;
        _channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(o.ChannelCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _writer = new Thread(WriterLoop) { Name = "flaui-log-writer", IsBackground = true };
        _writer.Start();
    }

    public string? CurrentFilePath => _currentPath;
    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    /// <summary>Creates the directory and opens segment 01. Returns null with an error message instead of
    /// throwing: a server whose log cannot be opened still starts (spec §7).</summary>
    public static JsonlFileLoggerProvider? TryCreate(JsonlLogOptions o, LogMasker masker, out string? error)
    {
        try
        {
            System.IO.Directory.CreateDirectory(o.Directory);
            var prefix = $"flaui-mcp-{o.Pid}-{o.ProcessStartUtc.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}-";
            var first = Open(Path.Combine(o.Directory, prefix + "01.jsonl"));
            error = null;
            return new JsonlFileLoggerProvider(o, masker, first);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"flaui-mcp: the log file could not be opened in '{o.Directory}': {ex.Message}. " +
                    "The server runs without a log file.";
            return null;
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    private void Enqueue(LogLevel level, LogEventState state)
    {
        if (level < _o.MinLevel) return;
        // Scope captured HERE, on the producer thread (spec §4.1): the writer thread does not share the
        // caller's AsyncLocal state, and the scope may be disposed before the record is dequeued.
        var scope = CapturedScope(_scopes);
        var fields = _masker.Apply(state.Event, state.Fields, scope);
        var record = new LogRecord(DateTime.UtcNow, level, state.Event, scope, fields);
        if (!_channel.Writer.TryWrite(record)) { Interlocked.Increment(ref _dropped); Interlocked.Increment(ref _droppedTotal); }
    }

    // ForEachScope runs outermost-first, so the innermost LogScope wins. A one-slot array is the state
    // object because the callback is static (no closure allocation per record).
    private static LogScope? CapturedScope(IExternalScopeProvider? scopes)
    {
        if (scopes is null) return null;
        var box = new LogScope?[1];
        scopes.ForEachScope(static (s, b) => { if (s is LogScope ls) b[0] = ls; }, box);
        return box[0];
    }

    private void WriterLoop()
    {
        var reader = _channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            _paused.Wait();
            _idle.Reset();
            while (reader.TryRead(out var record))
            {
                Write(JsonlRecordSerializer.Serialize(record, _o.Pid));
                var pending = Interlocked.Read(ref _dropped) - _droppedReported;
                if (pending > 0 && _stream is not null)
                {
                    _droppedReported += pending;
                    Write(JsonlRecordSerializer.Serialize(new LogRecord(DateTime.UtcNow, LogLevel.Warning, "log.dropped", null,
                        new[] { LogEvent.F("count", pending) }), _o.Pid));
                }
            }
            try { _stream?.Flush(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CloseAfterError(); }
            _idle.Set();
        }
        try { _stream?.Flush(); _stream?.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        _idle.Set();
    }

    private void Write(byte[] line)
    {
        if (_stream is null)
        {
            if (DateTime.UtcNow < _reopenAfter) { Interlocked.Increment(ref _droppedTotal); return; }
            try { _stream = Open(SegmentPath(_segment)); _bytes = _stream.Length; _currentPath = Path.GetFullPath(SegmentPath(_segment)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _reopenAfter = DateTime.UtcNow.AddSeconds(30); Interlocked.Increment(ref _droppedTotal); return; }
        }
        if (_bytes > 0 && _bytes + line.Length > _o.SegmentBytes) Roll();
        try { _stream!.Write(line, 0, line.Length); _bytes += line.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CloseAfterError(); Interlocked.Increment(ref _droppedTotal); }
    }

    private void Roll()
    {
        FileStream next;
        try { next = Open(SegmentPath(_segment + 1)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // keep appending; retry next write
        try { _stream!.Flush(); _stream.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        _stream = next; _segment++; _bytes = 0; _currentPath = Path.GetFullPath(SegmentPath(_segment));
        for (int s = _segment - _o.KeepSegments; s >= 1; s--)
        {
            var old = SegmentPath(s);
            if (!File.Exists(old)) continue;   // a gap (deleted earlier) must not stop retrying an older held-open one
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; } // held open: retry next roll
        }
    }

    private string SegmentPath(int seg) => Path.Combine(_o.Directory, _prefix + seg.ToString("00", CultureInfo.InvariantCulture) + ".jsonl");

    private void CloseAfterError()
    {
        try { _stream?.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        _stream = null; _reopenAfter = DateTime.UtcNow.AddSeconds(30);
    }

    /// <summary>Drains for up to 1 s, then closes. The caller logs `server.stop` BEFORE disposing, so it is
    /// already in the channel and is the last record written (spec §7).</summary>
    public void Dispose()
    {
        _paused.Set();
        _channel.Writer.TryComplete();
        _writer.Join(TimeSpan.FromSeconds(1));
    }

    /// <summary>Delete files of dead processes, keeping the newest `keepDeadFiles` of them (spec §4.1).</summary>
    public static void PruneDead(string dir, int keepDeadFiles, Func<int, DateTime, bool> isAlive)
    {
        if (!System.IO.Directory.Exists(dir)) return;
        var dead = new List<FileInfo>();
        var alive = new Dictionary<(int, DateTime), bool>();
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "flaui-mcp-*.jsonl"))
        {
            var m = FileName.Match(Path.GetFileName(path));
            if (!m.Success) continue;
            var pid = int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture);
            var start = DateTime.ParseExact(m.Groups["start"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (!alive.TryGetValue((pid, start), out var a)) alive[(pid, start)] = a = isAlive(pid, start);
            if (!a) dead.Add(new FileInfo(path));
        }
        foreach (var f in dead.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keepDeadFiles))
            try { f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
    }

    /// <summary>Alive = a process with that pid exists AND started within 2 s of the recorded time (pids are
    /// reused). Unknown (access denied) counts as alive: never delete what we cannot prove is dead.</summary>
    public static bool DefaultIsAlive(int pid, DateTime startUtc)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return true; }
        using (p)
        {
            try { return Math.Abs((p.StartTime.ToUniversalTime() - startUtc).TotalSeconds) < 2; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return true; }
        }
    }

    // ---- test hooks ----
    internal void PauseWriterForTest() => _paused.Reset();
    internal void ResumeWriterForTest() => _paused.Set();
    internal void FlushForTest()
    {
        for (int i = 0; i < 200 && (_channel.Reader.Count > 0 || !_idle.IsSet); i++) Thread.Sleep(10);
    }

    private sealed class Logger(JsonlFileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; // scopes come via ISupportExternalScope
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._o.MinLevel && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is LogEventState s) owner.Enqueue(logLevel, s);
        }
    }
}

/// <summary>Implements the reader rule (spec §5.3): segments in order, a torn FINAL line of the newest
/// segment is skipped. Used by tests and documented for agents.</summary>
public static class JsonlLogReader
{
    public static IEnumerable<JsonElement> ReadSegments(IReadOnlyList<string> segmentsInOrder)
    {
        for (int s = 0; s < segmentsInOrder.Count; s++)
        {
            string[] lines;
            using (var fs = new FileStream(segmentsInOrder[s], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) lines = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                JsonElement e;
                try { e = JsonDocument.Parse(lines[i]).RootElement.Clone(); }
                catch (JsonException) when (s == segmentsInOrder.Count - 1 && i == lines.Length - 1) { yield break; }
                yield return e;
            }
        }
    }
}
