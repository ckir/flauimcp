using System;
using System.Globalization;
using System.IO;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Event-only synthetic-input audit. Logs WHEN + WHICH target + WHAT KIND + payload LENGTH —
/// never the typed text (a payload can BE the secret; keeps Phase-2's no-secrets-on-disk stance).
/// v0.10.1 (T8): optionally appends the resolved element's ALLOW-LISTED identity
/// (RuntimeId/AutomationId/ClassName/ControlType/Bounds — nothing else). Appended fields are strictly
/// OMITTED when no element resolved, so the line is byte-for-byte identical to the pre-T8 output for the
/// window/coordinate case (INV-T8-1).</summary>
public sealed class InputAudit
{
    private readonly TextWriter _sink;
    public InputAudit(TextWriter sink) => _sink = sink;

    public void Record(nint window, int pid, string? process, string action, int payloadLength,
        ElementIdentity? element = null)
    {
        var head = $"[flaui-mcp][input-audit] ts={DateTime.UtcNow:O} window={window} pid={pid} " +
                   $"process={process ?? "?"} action={action} len={payloadLength}";
        if (element is not { } e) { _sink.WriteLine(head); return; }
        var b = e.Bounds;
        var tail =
            $" rid={AuditQuote(e.RuntimeId)} aid={AuditQuote(e.AutomationId)} " +
            $"class={AuditQuote(e.ClassName)} ctype={AuditQuote(e.ControlType)} " +
            string.Format(CultureInfo.InvariantCulture, "bounds={0},{1},{2},{3}", b.L, b.T, b.W, b.H);
        _sink.WriteLine(head + tail);
    }

    /// <summary>Wrap an appended string field in double-quotes, escaping backslash, quote, CR and LF so a
    /// value containing spaces/quotes/newlines can never break the space-delimited key=value line. null or
    /// empty renders as "".</summary>
    public static string AuditQuote(string? value)
    {
        var s = value ?? string.Empty;
        s = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        return "\"" + s + "\"";
    }
}
