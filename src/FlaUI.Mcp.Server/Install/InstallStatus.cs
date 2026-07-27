using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Answers "what is actually deployed right now?".
///
/// Setup runs the installer `runhidden` (installer/flaui-mcp.iss), so the per-agent outcomes printed
/// during `install` are discarded the moment they are written. This is the read side of that: it
/// reports live on-disk state plus the record `install` left behind, so a partial failure is
/// discoverable after the fact instead of only showing up as a feature that mysteriously isn't there.
///
/// Deliberately NOT folded into `print-config`, whose output is pure JSON meant to be pasted or piped
/// into a client config — prose there would corrupt it.
/// </summary>
public static class InstallStatus
{
    public const string LogName = "install.log";

    public static string Describe(string exePath, string agyPluginsDir, string dataDir, string claudeConfigDir, string stateDir, ClaudePluginStatus claudePluginStatus)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"flaui-mcp {typeof(InstallStatus).Assembly.GetName().Version}");
        sb.AppendLine($"  exe: {exePath}");
        sb.AppendLine();

        var pluginRoot = Path.Combine(agyPluginsDir, "flaui-mcp");
        sb.AppendLine("Seed driving skill (agy):");
        sb.AppendLine("  " + DescribeSeed(pluginRoot));
        sb.AppendLine();

        sb.AppendLine("Driving skill (Claude Code):");
        sb.AppendLine("  " + DescribeClaudeSkill(new ClaudeSkillDeployer(claudeConfigDir).SkillRoot, claudePluginStatus));
        sb.AppendLine("  Activation hook: " + DescribeActivationHook(PluginIds.StagingDir(exePath)));
        sb.AppendLine();

        var collisions = DescribeCollisions(stateDir);
        if (collisions is not null) { sb.AppendLine(collisions); sb.AppendLine(); }

        var log = Path.Combine(dataDir, LogName);
        sb.AppendLine("Last install/uninstall run:");
        sb.Append(DescribeLog(log));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Answers "why is the activation hint not appearing?" without reading hook source.
    /// Reads the STAGED hooks.json — the artifact the client actually loads — not the repo tree,
    /// which is registered nowhere.
    ///
    /// Parses rather than substring-matching: a bare Contains() over the file text reports "wired"
    /// when the verb appears inside some UNRELATED hook, which is a false green on the one signal a
    /// user consults when the activation hint fails to show up.</summary>
    public static string DescribeActivationHook(string pluginStagingDir)
    {
        var hooks = Path.Combine(pluginStagingDir, "hooks", "hooks.json");
        if (!File.Exists(hooks))
            return "not staged — run `flaui-mcp install --agent claude` to (re)generate the plugin";

        // Broad catch, matching this file's own convention (DescribeLog, DescribeVersion): a locked or
        // permission-denied file must not crash the command a user runs BECAUSE something is wrong.
        string raw;
        try { raw = File.ReadAllText(hooks); }
        catch (Exception e) { return $"staged but UNREADABLE — {e.Message}"; }

        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (JsonException) { return "staged but UNREADABLE — hooks.json is not valid JSON; reinstall to regenerate"; }

        // Distinguished from "NOT wired" on purpose: a structurally broken file and a structurally
        // sound one that simply lacks the hook need different actions from the operator, and reporting
        // the same sentence for both hides the corruption behind a routine-looking message.
        if (root is not JsonObject rootObj || rootObj["hooks"] is not JsonObject hookMap)
            return "staged but MALFORMED — hooks.json has no top-level \"hooks\" object; reinstall to regenerate";

        // Every step is a TYPE TEST, never a cast or a string indexer on an unknown node.
        // JsonNode's string indexer throws InvalidOperationException when the node is not a JsonObject,
        // and GetValue<string>() throws when the value is not a string — so `{"hooks":42}` or
        // `{"command":42}` would crash the one command a user runs to diagnose a broken install.
        // Valid-JSON-but-wrong-shape must degrade to "NOT wired", not to a stack trace.
        var wired = SessionStartEntries(hookMap["SessionStart"]).Any(entry =>
                        entry is JsonObject entryObj
                        && entryObj["hooks"] is JsonArray commands
                        && commands.Any(c => c is JsonObject cmd
                            && cmd["command"] is JsonValue v
                            && v.TryGetValue<string>(out var s)
                            && s.Contains(ActivationPayload.Verb, StringComparison.Ordinal)));

        return wired
            ? "wired (SessionStart -> flaui-mcp " + ActivationPayload.Verb + ")"
            : "staged but NOT wired — no SessionStart entry invokes the verb; reinstall to regenerate";
    }

    /// Normalizes the two shapes a hooks.json `SessionStart` key can legitimately take — an array of
    /// entries, or a single entry object — into one sequence. Kept in step with
    /// PluginArtifactWriter.MergeActivationHook, which must PRESERVE whichever shape it finds; if these
    /// two disagreed, `status` would report "NOT wired" for a hook that fires perfectly well.
    private static IEnumerable<JsonNode?> SessionStartEntries(JsonNode? sessionStart) => sessionStart switch
    {
        JsonArray a  => a,
        JsonObject o => new JsonNode?[] { o },
        _            => Array.Empty<JsonNode?>(),
    };

    /// <summary>
    /// Post-installer-rework, the Claude driving skill ships INSIDE the `flaui-mcp@flaui-mcp-marketplace`
    /// plugin — `install` no longer copies a skill dir to <paramref name="skillRoot"/>. So the PRIMARY
    /// signal here is the already-computed plugin registration state (threaded in by the caller, which
    /// built the `CliInvoker`/`ClaudePluginRegistrar` the same way `install` does — this class does not
    /// shell out itself, so its existing hermetic unit tests stay hermetic). A leftover copy at
    /// <paramref name="skillRoot"/> from the old model is mentioned only as a retired aside, never as
    /// the primary deployed/not-deployed signal — probing it as ground truth is exactly the false-negative
    /// bug this replaced (a plugin-installed machine reported "NOT deployed").
    /// </summary>
    private static string DescribeClaudeSkill(string skillRoot, ClaudePluginStatus claudePluginStatus)
    {
        var legacySkill = Path.Combine(skillRoot, "skills", "driving-flaui-mcp", "SKILL.md");
        var legacyNote = File.Exists(legacySkill)
            ? $" (a retired copy from the old skill-directory model also sits at {skillRoot} — no longer read; safe to delete)"
            : "";

        return claudePluginStatus switch
        {
            ClaudePluginStatus.CliNotFound =>
                "claude CLI not found on PATH (can't check plugin registration)" + legacyNote,
            ClaudePluginStatus.Active =>
                $"deployed as plugin ({PluginIds.InstallTarget})" + legacyNote,
            ClaudePluginStatus.NotRegistered =>
                "NOT deployed — plugin not registered. If you installed Claude Code after flaui-mcp, " +
                "run: flaui-mcp install --agent claude" + legacyNote,
            _ => "unknown claude plugin status" + legacyNote,
        };
    }

    /// <summary>The R5 channel: a plugin we disabled on the user's behalf is otherwise invisible —
    /// Setup ran hidden, so this is where they can find out. A corrupt or future-version record is
    /// surfaced rather than masked as "nothing disabled" (goal 7).</summary>
    private static string? DescribeCollisions(string stateDir)
    {
        var (state, recorded) = CollisionMarker.ReadState(stateDir);
        var path = CollisionMarker.PathIn(stateDir);
        // The path is named for parity with the sibling Record/Restore messages, so a user can locate or
        // remove the file. The `_` arm keeps this SAFE for any future/unknown MarkerState — it says nothing
        // rather than mislabelling it as Present, which the previous `default: // Present` would have done.
        // (A compile-time guard on a new member isn't possible here: an arm-less enum switch expression warns
        // CS8524 over the unnamed-value domain, which the repo's 0-warnings gate forbids.)
        return state switch
        {
            MarkerState.Absent => null,
            MarkerState.Corrupt =>
                $"Conflicting-plugin record: the restore record at {path} exists but is unreadable — re-enable " +
                $"{ClaudeCollisionRemedy.MarketplaceId} manually if a driving-skill copy is still disabled.",
            MarkerState.FutureVersion =>
                $"Conflicting-plugin record: a restore record written by a newer flaui-mcp is present at {path}.",
            MarkerState.Present => FormatPresentCollisions(recorded),
            _ => null,
        };
    }

    /// <summary>The Present-state collision listing — byte-identical to the pre-switch-expression output.</summary>
    private static string? FormatPresentCollisions(IReadOnlyList<DisabledEntry> recorded)
    {
        if (recorded.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("Conflicting plugins we disabled (they will be re-enabled if you uninstall flaui-mcp):");
        foreach (var e in recorded)
            sb.AppendLine($"  {e.Id} — scope {e.Scope}{(e.ProjectPath is null ? "" : $" in {e.ProjectPath}")}");
        return sb.ToString().TrimEnd();
    }

    private static string DescribeSeed(string pluginRoot)
    {
        var skill = Path.Combine(pluginRoot, "skills", "driving-flaui-mcp", "SKILL.md");
        if (!File.Exists(skill))
            return $"NOT deployed — nothing at {pluginRoot}";

        // The deployed version is what the agent will actually read; it can lag this exe if an
        // install partially failed, which is exactly the case worth surfacing.
        var deployed = ReadDeployedVersion(Path.Combine(pluginRoot, "plugin.json"));
        return $"deployed ({deployed}) at {pluginRoot}";
    }

    private static string ReadDeployedVersion(string pluginJson)
    {
        try
        {
            if (!File.Exists(pluginJson)) return "version unknown — no plugin.json";
            var v = (string?)(JsonNode.Parse(File.ReadAllText(pluginJson)) as JsonObject)?["version"];
            return string.IsNullOrWhiteSpace(v) ? "version unknown" : $"v{v}";
        }
        catch { return "version unreadable"; }
    }

    private static string DescribeLog(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
                return $"  no record yet ({logPath})" + Environment.NewLine;
            var sb = new StringBuilder();
            foreach (var line in File.ReadAllLines(logPath))
                sb.AppendLine("  " + line);
            return sb.ToString();
        }
        catch (Exception e) { return $"  could not read {logPath}: {e.Message}" + Environment.NewLine; }
    }
}
