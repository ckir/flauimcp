// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spec §8 item 1, format pin: the closed event set (§5.2) and the starred UI-text field list.
/// A syntax-only Roslyn sweep of src/ collects every literal event name given to LogEvent.Emit and every
/// literal key given to LogEvent.Ui, and compares them with sets pinned from the spec table (§5.2) and the
/// plan "Spec adjustments" (wait.end value*, selector.resolve automationId*/name*).
/// A new event or a new/changed UI-text field must be added here on purpose.</summary>
[Trait("Category", "SourceSweep")]
public class LogFormatPinTests
{
    // §5.2: the 13 events emitted through LogEvent.Emit ...
    private static readonly string[] EmittedEvents =
    {
        "action.blocked", "capture.decision", "focus.attempt", "input.decision", "ref.resolve",
        "selector.resolve", "server.start", "server.stop", "tool.end", "tool.exception", "tool.start",
        "wait.end", "window.bind",
    };
    // ... plus log.dropped, which the provider writes straight into the channel (JsonlFileLoggerProvider.WriterLoop).
    private const string DroppedEvent = "log.dropped";

    // Starred (UI-text) fields: spec §5.2 stars (title, value, automationId, name, fgTitle) and the text of
    // msg; the plan adds wait.end value*. Pinned per event where the Ui call sits inside the Emit call.
    private static readonly string[] StarredEventFields =
    {
        "action.blocked:fgTitle", "ref.resolve:automationId", "ref.resolve:name",
        "selector.resolve:automationId", "selector.resolve:name", "tool.exception:msg",
        "wait.end:value", "window.bind:title",
    };
    // Every literal key passed to LogEvent.Ui anywhere in src/ (the tool.end msg is added outside its Emit call).
    private static readonly string[] AllUiKeys = { "automationId", "fgTitle", "msg", "name", "title", "value" };

    private sealed record Sweep(List<string> Events, List<string> UiKeys, List<string> EventUi,
        List<string> NonLiteral, List<string> DroppedLiteral, int Files);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FlaUI.Mcp.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        Assert.Fail("FlaUI.Mcp.slnx not found above " + AppContext.BaseDirectory);
        return null!;
    }

    private static bool IsLogEventCall(InvocationExpressionSyntax inv, string method) =>
        inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var n } m && n == method
        && (m.Expression is IdentifierNameSyntax { Identifier.ValueText: "LogEvent" }
            || m.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "LogEvent" });

    private static string? Literal(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression) ? l.Token.ValueText : null;

    private static Sweep RunSweep()
    {
        var repo = RepoRoot();
        var s = new Sweep(new(), new(), new(), new(), new(), 0);
        int files = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Any(p => p.Equals("obj", StringComparison.OrdinalIgnoreCase) || p.Equals("bin", StringComparison.OrdinalIgnoreCase))))
        {
            files++;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            var rel = Path.GetRelativePath(repo, file).Replace('\\', '/');
            var root = tree.GetRoot();
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var where = $"{rel}:{inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                if (IsLogEventCall(inv, "Emit"))
                {
                    var args = inv.ArgumentList.Arguments;
                    var ev = args.Count > 2 ? Literal(args[2].Expression) : null;
                    if (ev is null) { s.NonLiteral.Add($"Emit event name not a string literal at {where}"); continue; }
                    s.Events.Add(ev);
                    foreach (var ui in inv.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => IsLogEventCall(i, "Ui")))
                    {
                        var key = ui.ArgumentList.Arguments.Count > 0 ? Literal(ui.ArgumentList.Arguments[0].Expression) : null;
                        if (key is not null) s.EventUi.Add($"{ev}:{key}");
                    }
                }
                else if (IsLogEventCall(inv, "Ui"))
                {
                    var key = inv.ArgumentList.Arguments.Count > 0 ? Literal(inv.ArgumentList.Arguments[0].Expression) : null;
                    if (key is null) s.NonLiteral.Add($"Ui key not a string literal at {where}");
                    else s.UiKeys.Add(key);
                }
            }
            // log.dropped is built as: new LogRecord(ts, level, "log.dropped", ...)
            foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                         .Where(o => o.Type is IdentifierNameSyntax { Identifier.ValueText: "LogRecord" }))
                if (oc.ArgumentList is { Arguments.Count: > 2 } al && Literal(al.Arguments[2].Expression) is { } lit)
                    s.DroppedLiteral.Add(lit);
        }
        return s with { Files = files };
    }

    [Fact]
    public void The_event_set_is_closed_and_equals_the_spec_5_2_table()
    {
        var s = RunSweep();
        Assert.True(s.Files > 20, $"swept only {s.Files} files");
        Assert.Empty(s.NonLiteral);
        Assert.Equal(EmittedEvents, s.Events.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { DroppedEvent }, s.DroppedLiteral.Distinct().ToArray());
        Assert.Equal(14, EmittedEvents.Length + 1);   // spec §5.2 lists exactly 14 events
    }

    [Fact]
    public void The_starred_ui_text_field_list_is_pinned()
    {
        var s = RunSweep();
        Assert.Empty(s.NonLiteral);
        Assert.Equal(AllUiKeys, s.UiKeys.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(StarredEventFields, s.EventUi.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_masked_value_is_an_object_with_exactly_len_and_sha8()
    {
        var line = JsonlRecordSerializer.Serialize(new LogRecord(new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc),
            LogLevel.Information, "window.bind", null, new[] { LogEvent.F("title", new MaskedValue(5, "0123abcd")) }), 1);
        var title = JsonDocument.Parse(line.AsMemory(0, line.Length - 1)).RootElement.GetProperty("title");
        Assert.Equal(JsonValueKind.Object, title.ValueKind);
        Assert.Equal(new[] { "len", "sha8" }, title.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
