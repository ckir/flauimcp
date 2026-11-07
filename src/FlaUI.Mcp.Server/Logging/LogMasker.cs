// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Logging;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Applies spec §5.2 (starred fields), §6.3 and §6.4 to one record's fields. Pure.</summary>
public sealed class LogMasker
{
    private readonly ContentHasher _hasher;
    private readonly Func<string?, bool> _isSensitiveProcess;

    public LogMasker(ContentHasher hasher, Func<string?, bool> isSensitiveProcess)
    { _hasher = hasher; _isSensitiveProcess = isSensitiveProcess; }

    public ContentHasher Hasher => _hasher;

    public IReadOnlyList<LogField> Apply(string ev, IReadOnlyList<LogField> fields, LogScope? scope)
    {
        // An event naming its OWN process (window.bind, action.blocked) is classified by it.
        string? ownProcess = null; bool hasOwn = false;
        string? outcome = null;
        foreach (var f in fields)
        {
            if (f.Key is "process" or "fgProcess") { hasOwn = true; ownProcess = f.Value as string; }
            if (f.Key == "outcome") outcome = f.Value as string;
        }
        bool sensitive = scope is null
            ? (!hasOwn || _isSensitiveProcess(ownProcess))       // no call scope, no own process: fail closed
            : scope.Sensitive || (hasOwn && _isSensitiveProcess(ownProcess));

        // §6.4: a message the server did not write, on a call that carried content.
        bool maskMsgForContent = scope is { HasContentArgs: true }
            && (ev == "tool.exception" || (ev == "tool.end" && outcome == "INTERNAL"));

        var result = new LogField[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            bool mask = f.UiText && f.Value is string && (sensitive || (f.Key == "msg" && maskMsgForContent));
            result[i] = mask ? f with { Value = _hasher.Mask((string)f.Value!) } : f;
        }
        return result;
    }
}
