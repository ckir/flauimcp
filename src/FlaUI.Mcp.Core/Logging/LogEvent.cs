// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Collections;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Core.Logging;

/// <summary>One field of a structured event. `UiText` marks a value that comes from the UI; the file
/// provider masks it when the call is sensitive (spec §5.2, the starred fields).</summary>
public readonly record struct LogField(string Key, object? Value, bool UiText = false);

/// <summary>The state object of every structured event. The file provider writes ONLY records whose state
/// is a LogEventState; anything else (framework logs) stays on stderr.</summary>
public sealed class LogEventState : IReadOnlyList<KeyValuePair<string, object?>>
{
    public LogEventState(string ev, IReadOnlyList<LogField> fields) { Event = ev; Fields = fields; }

    public string Event { get; }
    public IReadOnlyList<LogField> Fields { get; }

    public int Count => Fields.Count + 1;
    public KeyValuePair<string, object?> this[int index] =>
        index == 0 ? new("ev", Event) : new(Fields[index - 1].Key, Fields[index - 1].UiText ? "<ui>" : Fields[index - 1].Value);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>What the console (stderr) provider prints. UI text is never rendered.</summary>
    public override string ToString() =>
        Event + " " + string.Join(" ", Fields.Select(f => $"{f.Key}={(f.UiText ? "<ui>" : f.Value)}"));
}

public static class LogEvent
{
    public static LogField F(string key, object? value) => new(key, value);
    public static LogField Ui(string key, string? value) => new(key, value, UiText: true);

    /// <summary>Log one structured event. Never throws: a broken logger must not change a tool's outcome
    /// (spec §7).</summary>
    public static void Emit(ILogger? logger, LogLevel level, string ev, params LogField[] fields)
    {
        if (logger is null) return;
        try
        {
            if (!logger.IsEnabled(level)) return;
            logger.Log(level, default, new LogEventState(ev, fields), null, static (s, _) => s.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; }
    }
}
