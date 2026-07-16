using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>One row of `claude plugin list --json`.</summary>
/// <param name="ProjectPath">Where the entry lives, for non-user scopes. NOT decoration: `claude
/// plugin disable` has no flag to target another project, so this is the working directory the
/// disable must RUN from, or it silently disables nothing.</param>
public sealed record ClaudePluginEntry(string Id, string Scope, bool Enabled, string? ProjectPath);

/// <summary>
/// Reads Claude Code's plugin inventory from `claude plugin list --json` — a documented
/// machine-readable contract, which is why we ask the host for its effective state instead of
/// re-implementing its scope resolution over its config files.
///
/// MEASURED 2026-07-16: the ROW SET is global — the same ids/scopes/projectPaths from any working
/// directory, backed by `~/.claude/plugins/installed_plugins.json`. But the `enabled` field of a
/// `scope=local` row is resolved against the CURRENT working directory, NOT the row's own projectPath:
/// run from a project whose settings enable the plugin and the row reads enabled; run from anywhere
/// else (including a hidden installer's cwd) and the SAME row reads disabled. This parser faithfully
/// transcribes whatever `claude` emitted — so a caller must treat `Enabled` on a non-user row as valid
/// ONLY when the list was read with the working directory set to that row's projectPath. See
/// ClaudeCollisionRemedy, which never trusts a global `Enabled` and uses `disable` itself as detector.
///
/// Never throws. It parses foreign output inside a hidden installer; "I know nothing" is a usable
/// answer, an exception is not.
/// </summary>
public static class ClaudePluginInventory
{
    public static IReadOnlyList<ClaudePluginEntry> Parse(string json)
    {
        var list = new List<ClaudePluginEntry>();
        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr) return list;
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var id = (string?)o["id"];
                var scope = (string?)o["scope"];
                // No id or no scope => nothing we could act on; drop it rather than guess.
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(scope)) continue;
                var enabled = o["enabled"] is null || ((bool?)o["enabled"] ?? true);
                list.Add(new ClaudePluginEntry(id, scope, enabled, (string?)o["projectPath"]));
            }
        }
        catch { return new List<ClaudePluginEntry>(); }
        return list;
    }

    public static IReadOnlyList<ClaudePluginEntry> Matching(IReadOnlyList<ClaudePluginEntry> entries, string id) =>
        entries.Where(e => string.Equals(e.Id, id, System.StringComparison.OrdinalIgnoreCase)).ToList();
}
