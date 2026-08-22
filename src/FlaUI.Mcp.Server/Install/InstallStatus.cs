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
        sb.AppendLine("  Server instructions: " + DescribeServerInstructions());
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

        // The trailing clause is not decoration. This method can only ever inspect a file WE wrote;
        // it has no way to ask the running client what it loaded. Measured 2026-07-27: Claude Code
        // registers plugin hooks only at client startup, so a freshly installed hook reads "wired"
        // here while being completely inert in every session of the client that is already running.
        // Reporting bare "wired" made this command the source of the false assurance that cost a day.
        //
        // Phrased as a STANDING FACT about the client, never as "restart to activate". A prediction
        // ("loads at the next restart") is unfalsifiable from here: it still prints after the user HAS
        // restarted, so it reads as "your restart did not take" and replaces false assurance with a
        // permanent false negative. Stating the client's rule instead is true in every state.
        return wired
            ? "wired (SessionStart -> flaui-mcp " + ActivationPayload.Verb + ") — Claude Code loads hooks only at client startup"
            : "staged but NOT wired — no SessionStart entry invokes the verb; reinstall to regenerate";
    }

    /// <summary>Whether THIS binary advertises the activation core over the MCP handshake. Reported
    /// because `status` runs the INSTALLED exe, which is not necessarily the one you just built.
    ///
    /// ⚠ THE DISCRIMINATOR IS THE PRESENCE OF THE LINE, NOT ITS TEXT. An older binary does not report
    /// "NOT advertised" — it has no such line at all, because it does not contain this method. An earlier
    /// version of this returned exactly that string when `Core` was empty, described as meaning "this
    /// binary predates the change", which is impossible twice over: `Core` is a compile-time-constant
    /// join of six non-empty literals and can never be empty, and a binary that predated the change could
    /// never have executed the branch. That was dead code asserting a self-contradiction, so it is gone.
    /// Do NOT reintroduce an "absent" branch here to "handle old binaries"; absence of the line IS the
    /// signal, and it is the operator manual that must say so.
    ///
    /// ⚠ Deliberately says "advertised", not "delivered". A client may drop the field silently and the
    /// server cannot tell (spec D5) — agy is MEASURED to do exactly that. Do not reword this into a
    /// claim that the agent received anything.</summary>
    public static string DescribeServerInstructions()
        => $"advertised at connect ({ActivationPayload.Core.Length} chars) — a client may still drop it";

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
            // ⚠ THE WORDING HERE HAS BEEN WRONG IN BOTH DIRECTIONS. Keep it modal.
            //
            // It first said "no longer read; safe to delete" — FALSE and reassuring in exactly the wrong
            // direction: this class's own doc records that Claude Code auto-loads that layout as
            // `flaui-mcp@skills-dir` at user scope, and the note only ever appears when install-time
            // cleanup FAILED, which is precisely when the operator must act.
            //
            // The correction then over-swung to "auto-loads it as a SECOND copy", which is wrong in two
            // reachable states: this note is appended to ALL FOUR status branches below, so it fires
            // alongside "NOT deployed — plugin not registered" (where a survivor is the ONLY copy, not a
            // second one) and alongside CliNotFound (where we just admitted we could not check). And
            // `Remove()` deletes recursively with no ordering guarantee, so a partial failure can drop
            // `.claude-plugin/plugin.json` while a locked SKILL.md survives — leaving a tree Claude will
            // NOT load, which makes any unconditional "it is active" claim false too.
            //
            // So: assert the RISK and the ACTION, never a load state we cannot know here. "delete it"
            // is the correct instruction in every one of those states.
            ? $" (a retired copy from the old skill-directory model still sits at {skillRoot} — Claude can auto-load it as a duplicate driving skill; delete it)"
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
