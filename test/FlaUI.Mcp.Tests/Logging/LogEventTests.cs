// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Linq;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class LogEventTests
{
    [Fact]
    public void Emit_records_event_name_and_fields_in_order()
    {
        var log = new CapturingLogger();
        LogEvent.Emit(log, LogLevel.Information, "window.bind",
            LogEvent.F("handle", "w3"), LogEvent.Ui("title", "Untitled - Notepad"));
        var s = log.Single("window.bind");
        Assert.Equal(new[] { "handle", "title" }, s.Fields.Select(f => f.Key));
        Assert.False(s.Fields[0].UiText);
        Assert.True(s.Fields[1].UiText);
    }

    [Fact]
    public void Emit_is_a_no_op_for_a_null_or_disabled_logger()
    {
        LogEvent.Emit(null, LogLevel.Information, "x");
        var log = new CapturingLogger { MinLevel = LogLevel.Warning };
        LogEvent.Emit(log, LogLevel.Information, "x");
        Assert.Empty(log.Events);
    }

    [Fact]
    public void ToString_never_prints_ui_text()
    {
        // The console (stderr) provider renders the state with ToString; UI text must not reach it.
        var s = new LogEventState("e", new[] { LogEvent.F("a", 1), LogEvent.Ui("title", "SECRET-TITLE") });
        Assert.DoesNotContain("SECRET-TITLE", s.ToString());
        Assert.Contains("a=1", s.ToString());
    }

    [Fact]
    public void A_throwing_logger_never_escapes_Emit()
    {
        LogEvent.Emit(new ThrowingLogger(), LogLevel.Information, "x");
    }

    private sealed class ThrowingLogger : ILogger
    {
        public System.IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, System.Exception? x,
            System.Func<TState, System.Exception?, string> f) => throw new System.InvalidOperationException("boom");
    }
}
