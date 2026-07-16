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

    public static string Describe(string exePath, string agyPluginsDir, string dataDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"flaui-mcp {typeof(InstallStatus).Assembly.GetName().Version}");
        sb.AppendLine($"  exe: {exePath}");
        sb.AppendLine();

        var pluginRoot = Path.Combine(agyPluginsDir, "flaui-mcp");
        sb.AppendLine("Seed driving skill (agy):");
        sb.AppendLine("  " + DescribeSeed(pluginRoot));
        sb.AppendLine();

        var log = Path.Combine(dataDir, LogName);
        sb.AppendLine("Last install/uninstall run:");
        sb.Append(DescribeLog(log));
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
