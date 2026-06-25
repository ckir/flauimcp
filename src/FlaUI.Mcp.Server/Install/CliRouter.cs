namespace FlaUI.Mcp.Server.Install;

/// <summary>Parses the installer CLI verbs and dispatches to the per-agent writers.</summary>
public static class CliRouter
{
    private static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall", "print-config", "--version", "-v", "--help", "-h" };

    public static bool IsInstallerVerb(string[] args) => args.Length > 0 && Verbs.Contains(args[0]);

    public static int Run(string[] args, string exePath, TextWriter outp)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "--help";
        var agent = OptionValue(args, "--agent") ?? "all";
        var configOverride = OptionValue(args, "--config");

        switch (verb)
        {
            case "--version":
            case "-v":
                outp.WriteLine($"flaui-mcp {ThisVersion()}");
                return 0;

            case "print-config":
                outp.WriteLine(new GenericMcpConfigWriter().PrintConfig(exePath));
                return 0;

            case "install":
                foreach (var r in Apply(agent, configOverride, install: true, exePath))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                outp.WriteLine("If you configured agy, restart it to load the new tools.");
                return 0;

            case "uninstall":
                foreach (var r in Apply(agent, configOverride, install: false, exePath))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                return 0;

            default:
                outp.WriteLine("usage: flaui-mcp [install|uninstall|print-config|--version] [--agent agy|generic|claude|all] [--config <path>]");
                return 0;
        }
    }

    private static IEnumerable<AgentResult> Apply(string agent, string? configOverride, bool install, string exePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // agy precedence DEFAULT (see STOP-and-verify below). Split observed on the dev machine.
        var agyServers = configOverride ?? Path.Combine(home, ".gemini", "settings.json");
        var agyPerms   = configOverride ?? Path.Combine(home, ".gemini", "antigravity-cli", "settings.json");
        var genericPath = configOverride ?? Path.Combine(home, ".flaui-mcp", "generic-mcp.json");

        var results = new List<AgentResult>();
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
        {
            var w = new AgyConfigWriter(agyServers, agyPerms);
            results.Add(install ? w.Install(exePath) : w.Uninstall());
        }
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            var w = new ClaudeCodeConfigWriter();
            results.Add(install ? w.Install(exePath) : w.Uninstall());
        }
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
        {
            var w = new GenericMcpConfigWriter();
            results.Add(install ? w.Install(genericPath, exePath) : w.Uninstall(genericPath));
        }
        return results;
    }

    private static string? OptionValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string ThisVersion() =>
        typeof(CliRouter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
