// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Core.Logging;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>One log record, fields already masked.</summary>
public sealed record LogRecord(DateTime Ts, LogLevel Level, string Ev, LogScope? Scope, IReadOnlyList<LogField> Fields);

/// <summary>Serialises a record to one UTF-8 JSON line of at most MaxRecordBytes (spec §5, §5.1).
/// System.Text.Json with its default encoder only, so control characters are escaped (no forged lines).</summary>
public static class JsonlRecordSerializer
{
    public const int MaxRecordBytes = 8192;
    private static readonly string[] Fixed = { "ts", "lvl", "pid", "ev", "cid", "tool", "outcome", "ms" };

    public static byte[] Serialize(LogRecord record, int pid)
    {
        var o = new JsonObject
        {
            ["ts"] = record.Ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["lvl"] = LevelName(record.Level),
            ["pid"] = pid,
            ["ev"] = record.Ev,
        };
        if (record.Scope is { } s) { o["cid"] = s.Cid; o["tool"] = s.Tool; }
        foreach (var f in record.Fields) o[f.Key] = ToNode(f.Value);

        var bytes = Bytes(o);
        if (bytes.Length <= MaxRecordBytes) return bytes;
        o["trunc"] = true;

        // (1) stack: keep the first frames
        if (o["stack"] is JsonValue sv && sv.TryGetValue<string>(out var stack))
        {
            var lines = stack.Split('\n');
            for (int keep = Math.Min(lines.Length, 32); keep >= 1 && (bytes = Bytes(o)).Length > MaxRecordBytes; keep /= 2)
                o["stack"] = string.Join('\n', lines.Take(keep)) + (keep < lines.Length ? "\n..." : "");
        }
        // (2) the longest raw string leaves in args and event fields (not msg, not fixed)
        while ((bytes = Bytes(o)).Length > MaxRecordBytes && LongestCuttable(o) is { } leaf && leaf.Len > 16)
            leaf.Set(leaf.Value[..(leaf.Len / 2)] + "...");
        // (3) msg, last because it carries the failure explanation
        while ((bytes = Bytes(o)).Length > MaxRecordBytes && o["msg"] is JsonValue mv && mv.TryGetValue<string>(out var msg) && msg.Length > 16)
            o["msg"] = msg[..(msg.Length / 2)] + "...";
        // (4) args replaced by its size
        if ((bytes = Bytes(o)).Length > MaxRecordBytes && o["args"] is JsonNode argsNode)
            o["args"] = new JsonObject { ["omitted"] = Encoding.UTF8.GetByteCount(argsNode.ToJsonString()) };
        // (5) last resort: only the fixed fields survive
        if ((bytes = Bytes(o)).Length > MaxRecordBytes)
        {
            foreach (var key in o.Select(p => p.Key).ToArray())
                if (Array.IndexOf(Fixed, key) < 0 && key != "trunc") o.Remove(key);
            bytes = Bytes(o);
        }
        return bytes;
    }

    private static byte[] Bytes(JsonObject o)
    {
        var json = o.ToJsonString();          // default encoder: escapes \n, \r, and other control chars
        var b = new byte[Encoding.UTF8.GetByteCount(json) + 1];
        Encoding.UTF8.GetBytes(json, 0, json.Length, b, 0);
        b[^1] = (byte)'\n';
        return b;
    }

    private static string LevelName(LogLevel l) => l switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warning",
        _ => "error",
    };

    private static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        MaskedValue m => new JsonObject { ["len"] = m.Len, ["sha8"] = m.Sha8 },
        IntPtr p => JsonValue.Create(p.ToInt64()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        IEnumerable<string> list => new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        _ => JsonValue.Create(v.ToString()),
    };

    private sealed record Leaf(string Value, Action<string> Set) { public int Len => Value.Length; }

    private static Leaf? LongestCuttable(JsonObject o)
    {
        Leaf? best = null;
        void Consider(string value, Action<string> set)
        { if (best is null || value.Length > best.Len) best = new Leaf(value, set); }
        void Walk(JsonNode? node, Action<string> set)
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<string>(out var s): Consider(s, set); break;
                case JsonObject obj:
                    if (obj.Count == 2 && obj.ContainsKey("len") && obj.ContainsKey("sha8")) break; // masked: never cut
                    foreach (var key in obj.Select(p => p.Key).ToArray()) { var k = key; Walk(obj[k], x => obj[k] = x); }
                    break;
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++) { var j = i; Walk(arr[j], x => arr[j] = x); }
                    break;
            }
        }
        foreach (var key in o.Select(p => p.Key).ToArray())
        {
            if (Array.IndexOf(Fixed, key) >= 0 || key is "msg" or "stack" or "trunc") continue;
            var k = key; Walk(o[k], x => o[k] = x);
        }
        return best;
    }
}
