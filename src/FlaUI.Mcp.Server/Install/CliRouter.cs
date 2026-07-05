namespace FlaUI.Mcp.Server.Install;

/// <summary>Parses the installer CLI verbs and dispatches to the per-agent writers.</summary>
public static class CliRouter
{
    private static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall", "print-config", "unlock", "lock", "overlay", "autosound", "--version", "-v", "--help", "-h" };

    public static bool IsInstallerVerb(string[] args) => args.Length > 0 && Verbs.Contains(args[0]);

    public static int Run(string[] args, string exePath, TextWriter outp)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "--help";
        var agent = OptionValue(args, "--agent") ?? "all";
        var configOverride = OptionValue(args, "--config");
        var purgeData = HasFlag(args, "--purge-data");
        var paths = ResolvePaths(configOverride);

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
                foreach (var r in Apply(agent, paths, install: true, exePath))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                outp.WriteLine("If you configured agy, restart it to load the new tools.");
                outp.WriteLine("Tip: to WATCH the agent act on screen, run  flaui-mcp overlay on  (off by default; " +
                    "flaui-mcp overlay off to disable), then reconnect. See the README's \"Watching & auditing the agent\" section.");
                return 0;

            case "overlay":
            {
                var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                if (mode != "on" && mode != "off")
                {
                    outp.WriteLine("usage: flaui-mcp overlay on|off [--agent agy|claude|generic|all]");
                    return 2;
                }
                var add = mode == "on" ? McpServerEntry.OverlayArgs.ToArray() : System.Array.Empty<string>();
                // Both group members named so the prefix rule (ConfigArgsMerge) drops the whole group:
                // "--overlay" alone would NOT also match "--overlay-ms=800" (different prefix).
                var remove = new[] { "--overlay", "--overlay-ms" };
                foreach (var r in ApplyMerge(agent, paths, exePath, add, remove))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                outp.WriteLine($"Intent overlay {mode.ToUpperInvariant()}. Reconnect the MCP client (/mcp) to apply; restart agy if you use it.");
                return 0;
            }

            case "autosound":
            {
                var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                if (mode != "on" && mode != "off")
                {
                    outp.WriteLine("usage: flaui-mcp autosound on|off [--agent agy|claude|generic|all]");
                    return 2;
                }
                var add = mode == "on" ? new[] { "--autosound" } : System.Array.Empty<string>();
                var remove = new[] { "--autosound" };
                foreach (var r in ApplyMerge(agent, paths, exePath, add, remove))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                outp.WriteLine($"Autosound {mode.ToUpperInvariant()}. Reconnect the MCP client (/mcp) to apply; restart agy if you use it.");
                return 0;
            }

            case "uninstall":
                foreach (var r in Apply(agent, paths, install: false, exePath))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                // Leave no leftovers: delete the timestamped backups we wrote next to each config
                // file we touched. (Backups are a safety net for the live install, not after removal.)
                foreach (var line in SweepBackups(new[] { paths.AgyServers, paths.AgyPerms, paths.GenericPath }))
                    outp.WriteLine(line);
                // --purge-data: also remove our own data dir (the generic config + anything under it).
                if (purgeData)
                {
                    var msg = PurgeDataDir(paths.DataDir);
                    if (msg is not null) outp.WriteLine(msg);
                }
                return 0;

            case "unlock":
            {
                var minutes = int.TryParse(OptionValue(args, "--minutes"), out var m) ? m : 5;
                outp.WriteLine(Lease.LeaseWriter.Grant(minutes, HasFlag(args, "--allow-shells")));
                return 0;
            }
            case "lock":
                outp.WriteLine(Lease.LeaseWriter.Revoke());
                return 0;

            case "--help":
            case "-h":
                PrintHelp(outp);
                return 0;

            default:
                outp.WriteLine("usage: flaui-mcp [install|uninstall [--purge-data]|print-config|unlock [--minutes N] [--allow-shells]|lock|overlay on|off|autosound on|off] [--agent agy|generic|claude|all] [--config <path>]");
                return 0;
        }
    }

    /// <summary>Structured multi-line help for `flaui-mcp --help`/`-h` (and the no-arg default).</summary>
    private static void PrintHelp(TextWriter outp)
    {
        outp.WriteLine("flaui-mcp — an MCP server that gives an AI agent eyes & hands on the Windows desktop.");
        outp.WriteLine();
        outp.WriteLine("Run with NO verb to start the stdio MCP server (how MCP clients launch it). The verbs");
        outp.WriteLine("below configure and manage the installation.");
        outp.WriteLine();
        outp.WriteLine("USAGE:");
        outp.WriteLine("  flaui-mcp <verb> [options]");
        outp.WriteLine();
        outp.WriteLine("VERBS:");
        outp.WriteLine("  install                    Register flaui-mcp in every detected MCP client config.");
        outp.WriteLine("  uninstall [--purge-data]   Remove the registration (and the data dir with --purge-data).");
        outp.WriteLine("  overlay on|off             Enable/disable the intent overlay — a red rectangle drawn on");
        outp.WriteLine("                             the target ~0.5s before each mutative action. Off by default.");
        outp.WriteLine("  autosound on|off           Enable/disable an audible cue on each mutative action. Off by");
        outp.WriteLine("                             default. Coexists with overlay (independent flag groups).");
        outp.WriteLine("  unlock [--minutes N]       Grant a time-bounded synthetic-input lease (default 5 min).");
        outp.WriteLine("          [--allow-shells]   Also permit input into interlocked shells/terminals.");
        outp.WriteLine("  lock                       Revoke the synthetic-input lease immediately.");
        outp.WriteLine("  print-config               Print the generic mcpServers JSON snippet for manual setup.");
        outp.WriteLine("  --version, -v              Print the version.");
        outp.WriteLine("  --help, -h                 Show this help.");
        outp.WriteLine();
        outp.WriteLine("COMMON OPTIONS:");
        outp.WriteLine("  --agent agy|claude|generic|all   Target specific client(s) (default: all). Applies to");
        outp.WriteLine("                                   install / uninstall / overlay / autosound.");
        outp.WriteLine("  --config <path>                  Override the config file path.");
        outp.WriteLine();
        outp.WriteLine("EXAMPLES:");
        outp.WriteLine("  flaui-mcp install");
        outp.WriteLine("  flaui-mcp overlay on               # watch the agent act (red rect before each action)");
        outp.WriteLine("  flaui-mcp unlock --minutes 30      # allow synthetic input for 30 minutes");
        outp.WriteLine("  flaui-mcp uninstall --purge-data");
    }

    private readonly record struct Paths(string AgyServers, string AgyPerms, string GenericPath, string DataDir);

    /// Resolve every config path the installer touches. The data dir (home of the generic config
    /// and the purge target) honors FLAUI_MCP_DATA_DIR so tests never touch the real `~/.flaui-mcp`.
    private static Paths ResolvePaths(string? configOverride)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataDir = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir)) dataDir = Path.Combine(home, ".flaui-mcp");

        // agy precedence: mcpServers in ~/.gemini/settings.json; permissions in the antigravity-cli file.
        var agyServers = configOverride ?? Path.Combine(home, ".gemini", "settings.json");
        var agyPerms   = configOverride ?? Path.Combine(home, ".gemini", "antigravity-cli", "settings.json");
        var genericPath = configOverride ?? Path.Combine(dataDir, "generic-mcp.json");
        return new Paths(agyServers, agyPerms, genericPath, dataDir);
    }

    private static IEnumerable<AgentResult> Apply(string agent, Paths paths, bool install, string exePath, IReadOnlyList<string>? extraArgs = null)
    {
        var results = new List<AgentResult>();
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
        {
            var w = new AgyConfigWriter(paths.AgyServers, paths.AgyPerms);
            results.Add(install ? w.Install(exePath, extraArgs) : w.Uninstall());
        }
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            var w = new ClaudeCodeConfigWriter();
            results.Add(install ? w.Install(exePath, extraArgs) : w.Uninstall());
        }
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
        {
            var w = new GenericMcpConfigWriter();
            results.Add(install ? w.Install(paths.GenericPath, exePath, extraArgs) : w.Uninstall(paths.GenericPath));
        }
        return results;
    }

    /// Non-destructive sibling of <see cref="Apply"/>: dispatches `addArgs`/`removeArgs` to each writer's
    /// merge overload instead of the full-replace `Install(exePath, extraArgs)`, so a flag verb (overlay,
    /// autosound, ...) only touches its own flag group and preserves whatever other verbs already set.
    private static IEnumerable<AgentResult> ApplyMerge(string agent, Paths paths, string exePath, IReadOnlyList<string> addArgs, IReadOnlyList<string> removeArgs)
    {
        var results = new List<AgentResult>();
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
        {
            var w = new AgyConfigWriter(paths.AgyServers, paths.AgyPerms);
            results.Add(w.Install(exePath, addArgs, removeArgs));
        }
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            var w = new ClaudeCodeConfigWriter();
            results.Add(w.Install(exePath, addArgs, removeArgs));
        }
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
        {
            var w = new GenericMcpConfigWriter();
            results.Add(w.Install(paths.GenericPath, exePath, addArgs, removeArgs));
        }
        return results;
    }

    /// Delete the `<config>.bak-<timestamp>` files JsoncFile wrote next to each touched config.
    /// Only our exact pattern is matched, so unrelated user files are never removed.
    private static IEnumerable<string> SweepBackups(IEnumerable<string> configPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in configPaths)
        {
            var dir = Path.GetDirectoryName(cfg);
            var name = Path.GetFileName(cfg);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !Directory.Exists(dir))
                continue;
            int removed = 0;
            foreach (var bak in Directory.GetFiles(dir, name + ".bak-*"))
            {
                if (!seen.Add(bak)) continue;
                try { File.Delete(bak); removed++; } catch { /* best-effort cleanup */ }
            }
            if (removed > 0)
                yield return $"[cleanup] removed {removed} backup file(s) next to {cfg}";
        }
    }

    /// Recursively remove the FlaUI.Mcp data dir (generic config + relocated backups). Best-effort.
    private static string? PurgeDataDir(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return null;
        try { Directory.Delete(dataDir, recursive: true); return $"[cleanup] removed data dir {dataDir}"; }
        catch (Exception e) { return $"[cleanup] could not remove data dir {dataDir}: {e.Message}"; }
    }

    private static string? OptionValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string ThisVersion() =>
        typeof(CliRouter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
