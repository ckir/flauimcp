using System;
using System.Globalization;
using System.Linq;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The out-of-band time-lease. Default-closed: synthetic input is OFF unless an unexpired
/// lease exists. Granted by the human via `flaui-mcp unlock`; never writable by an MCP-only agent.
/// Line format: <c>expiryUtc=&lt;ISO-8601 UTC&gt;;sid=&lt;granting SID&gt;;caps=&lt;comma-list&gt;</c>.</summary>
public sealed record InputLease(DateTime ExpiryUtc, string Sid, string[] Caps)
{
    public bool HasCapability(string cap) => Caps.Contains(cap, StringComparer.OrdinalIgnoreCase);

    public bool IsValidNow(DateTime utcNow, string currentSid) =>
        ExpiryUtc > utcNow
        && !string.IsNullOrWhiteSpace(currentSid)
        && !string.Equals(currentSid, "unknown", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Sid, "unknown", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Sid, currentSid, StringComparison.OrdinalIgnoreCase);

    public static string Format(DateTime expiryUtc, string sid, string[] caps) =>
        $"expiryUtc={expiryUtc.ToUniversalTime():O};sid={sid};caps={string.Join(",", caps)}";

    public static bool TryParse(string? line, out InputLease lease)
    {
        lease = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var kv = line.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!kv.TryGetValue("expiryUtc", out var exp) ||
            !DateTime.TryParse(exp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry))
            return false;
        if (!kv.TryGetValue("sid", out var sid) || string.IsNullOrWhiteSpace(sid)) return false;
        var caps = kv.TryGetValue("caps", out var c) && !string.IsNullOrWhiteSpace(c)
            ? c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        lease = new InputLease(expiry.ToUniversalTime(), sid, caps);
        return true;
    }
}
