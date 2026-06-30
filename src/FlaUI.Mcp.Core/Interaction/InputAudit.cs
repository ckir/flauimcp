using System;
using System.IO;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Event-only synthetic-input audit. Logs WHEN + WHICH target + WHAT KIND + payload LENGTH —
/// never the typed text (a payload can BE the secret; keeps Phase-2's no-secrets-on-disk stance).</summary>
public sealed class InputAudit
{
    private readonly TextWriter _sink;
    public InputAudit(TextWriter sink) => _sink = sink;

    public void Record(nint window, int pid, string? process, string action, int payloadLength) =>
        _sink.WriteLine($"[flaui-mcp][input-audit] ts={DateTime.UtcNow:O} window={window} pid={pid} " +
                        $"process={process ?? "?"} action={action} len={payloadLength}");
}
