using System;
using System.Collections.Generic;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// <summary>Non-destructive merge of the server's launch-arg flags (spec §4.4 ops fold). The CLI now manages
/// several independent flag groups (--overlay[/-ms], --autosound, --presence[/thresholds]); a verb must
/// inject/remove ONLY its own group and preserve the others. `removeAnyOf` matches a flag OR any flag that
/// starts with `flag` + "=" (so removing "--overlay" also drops "--overlay-ms=800" — same group by prefix).</summary>
public static class ConfigArgsMerge
{
    public static string[] Apply(IReadOnlyList<string>? existing, IReadOnlyList<string> add, IReadOnlyList<string> removeAnyOf)
    {
        var result = new List<string>();
        foreach (var a in existing ?? Array.Empty<string>())
            if (!removeAnyOf.Any(r => a == r || a.StartsWith(r + "=", StringComparison.Ordinal)) && !add.Contains(a))
                result.Add(a);
        result.AddRange(add);
        return result.ToArray();
    }
}
