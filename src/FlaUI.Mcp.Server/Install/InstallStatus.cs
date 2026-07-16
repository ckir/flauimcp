using System.Text;
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

    public static string Describe(string exePath, string agyPluginsDir, string dataDir, string claudeConfigDir, string stateDir)
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
        sb.AppendLine("  " + DescribeClaudeSkill(new ClaudeSkillDeployer(claudeConfigDir).SkillRoot));
        sb.AppendLine();

        var collisions = DescribeCollisions(stateDir);
        if (collisions is not null) { sb.AppendLine(collisions); sb.AppendLine(); }

        var log = Path.Combine(dataDir, LogName);
        sb.AppendLine("Last install/uninstall run:");
        sb.Append(DescribeLog(log));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Same shape as the agy seed report, but a different tree and a different manifest
    /// location — Claude Code's manifest lives in `.claude-plugin/`.</summary>
    private static string DescribeClaudeSkill(string skillRoot)
    {
        var skill = Path.Combine(skillRoot, "skills", "driving-flaui-mcp", "SKILL.md");
        if (!File.Exists(skill))
            return $"NOT deployed — nothing at {skillRoot}. If you installed Claude Code after " +
                   "flaui-mcp, run: flaui-mcp install --agent claude";
        // ReadDeployedVersion ALREADY EXISTS in this class (InstallStatus.cs:51) and takes the
        // manifest path as a parameter, so it is location-agnostic — reuse it, do not write a second
        // JSON parser. (A panel seat called this an undefined symbol; it is defined at :51.)
        var deployed = ReadDeployedVersion(Path.Combine(skillRoot, ".claude-plugin", "plugin.json"));
        return $"deployed ({deployed}) at {skillRoot}";
    }

    /// <summary>The R5 channel: a plugin we disabled on the user's behalf is otherwise invisible —
    /// Setup ran hidden, so this is where they can find out.</summary>
    private static string? DescribeCollisions(string stateDir)
    {
        var recorded = CollisionMarker.Read(stateDir);
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
