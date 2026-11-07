// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Records every LogEventState logged through it. Thread-safe.</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, LogEventState State)> _events = new();
    public LogLevel MinLevel { get; init; } = LogLevel.Trace;

    public IReadOnlyList<(LogLevel Level, LogEventState State)> Events
    { get { lock (_gate) return _events.ToArray(); } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is LogEventState s) lock (_gate) _events.Add((logLevel, s));
    }

    public LogEventState Single(string ev)
    {
        LogEventState? found = null;
        foreach (var (_, s) in Events)
            if (s.Event == ev) { Xunit.Assert.Null(found); found = s; }
        Xunit.Assert.NotNull(found);
        return found!;
    }
}

public sealed class CapturingLogger<T> : ILogger<T>
{
    public CapturingLogger Inner { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f)
        => Inner.Log(l, e, s, x, f);
}
