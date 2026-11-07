// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Spec §4.2: one `tool.start` and one `tool.end` per call, inside a scope carrying the cid.</summary>
public sealed class ToolCallLogFilter
{
    private readonly ILogger _log;
    private readonly ToolParameterCatalog _catalog;
    private readonly ICallSensitivity _sensitivity;
    private readonly ContentHasher _hasher;
    private long _calls;

    public ToolCallLogFilter(ILogger log, ToolParameterCatalog catalog, ICallSensitivity sensitivity, ContentHasher hasher)
    { _log = log; _catalog = catalog; _sensitivity = sensitivity; _hasher = hasher; }

    public long Calls => Interlocked.Read(ref _calls);

    public async ValueTask<CallToolResult> RunAsync(string toolName, IDictionary<string, JsonElement>? args,
        Func<ValueTask<CallToolResult>> next)
    {
        LogScope? scope = null;
        System.Text.Json.Nodes.JsonObject? logged = null;
        try
        {
            var tool = _catalog.Find(toolName);
            var (sensitive, targetless) = _sensitivity.Decide(tool, args);
            var (a, content) = ArgumentClassifier.Classify(tool, args, sensitive, _hasher);
            logged = a;
            scope = new LogScope("c-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
                toolName, sensitive, content, targetless);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; scope = null; }
        if (scope is null) return await next();

        Interlocked.Increment(ref _calls);
        var previous = LogContext.Current;
        LogContext.Current = scope;
        try
        {
            using var _ = SafeScope(scope);
            LogEvent.Emit(_log, LogLevel.Information, "tool.start", LogEvent.F("args", logged));
            var sw = Stopwatch.StartNew();
            CallToolResult result;
            try { result = await next(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                End(scope, "INTERNAL", ex.Message, sw.ElapsedMilliseconds);
                throw;
            }
            var (outcome, message) = SafeOutcome(result);
            End(scope, outcome, message, sw.ElapsedMilliseconds);
            return result;
        }
        finally { LogContext.Current = previous; }
    }

    private IDisposable? SafeScope(LogScope scope)
    {
        try { return _log.BeginScope(scope); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return null; }
    }

    private void End(LogScope scope, string outcome, string? message, long ms)
    {
        var fields = new List<LogField> { LogEvent.F("outcome", outcome), LogEvent.F("ms", ms) };
        bool omitMsg = scope.Targetless && outcome == "AmbiguousMatch";   // spec §6.3 exception
        if (outcome != "ok" && message is not null && !omitMsg) fields.Add(LogEvent.Ui("msg", message));
        LogEvent.Emit(_log, outcome == "ok" ? LogLevel.Information : LogLevel.Warning, "tool.end", fields.ToArray());
    }

    /// <summary>ToolResponse writes errors as a text block that STARTS with `{"error":` (its anonymous DTO
    /// puts `error` first). Only those are parsed, so an ok snapshot of megabytes is never re-parsed.</summary>
    private static (string Outcome, string? Message) SafeOutcome(CallToolResult result)
    {
        try
        {
            foreach (var block in result.Content ?? (IList<ContentBlock>)Array.Empty<ContentBlock>())
            {
                if (block is not TextContentBlock { Text: { } text } || !text.StartsWith("{\"error\":", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(text);
                var code = doc.RootElement.GetProperty("error").GetString() ?? "INTERNAL";
                var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (code, msg);
            }
            return result.IsError == true ? ("INTERNAL", null) : ("ok", null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return ("ok", null); }
    }
}
