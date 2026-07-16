using System.Globalization;

namespace FlaUI.Mcp.Server.Install;

/// <summary>Parses the installer CLI verbs and dispatches to the per-agent writers.</summary>
public static class CliRouter
{
    private static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall", "print-config", "status", "unlock", "lock", "overlay", "autosound", "presence", "--version", "-v", "--help", "-h" };

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

            // Pure JSON, meant to be piped/pasted into a client config — keep prose OUT of it.
            // `status` is where human-readable deployment state lives.
            case "print-config":
                outp.WriteLine(new GenericMcpConfigWriter().PrintConfig(exePath));
                return 0;

            case "status":
                outp.WriteLine(InstallStatus.Describe(exePath, paths.AgyPluginsDir, paths.DataDir));
                return 0;

            case "install":
                Report(Apply(agent, paths, install: true, exePath), "install", paths.DataDir, outp);
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

            case "presence":
            {
                var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                if (mode != "on" && mode != "off")
                { outp.WriteLine("usage: flaui-mcp presence on|off [--nearby-secs N] [--away-secs N] [--agent agy|claude|generic|all]"); return 2; }
                int nearby = int.TryParse(OptionValue(args, "--nearby-secs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nn) ? nn : 60;
                int away = int.TryParse(OptionValue(args, "--away-secs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var aa) ? aa : 300;
                if (mode == "on" && !FlaUI.Mcp.Core.Presence.IdleActivity.IsValidThresholds(nearby, away))
                { outp.WriteLine($"invalid thresholds: away-secs ({away}) must be greater than nearby-secs ({nearby})."); return 2; }

                // 1) Non-destructive config merge (default via the --presence launch flag group).
                var add = mode == "on"
                    ? new[] { "--presence", $"--nearby-secs={nearby}", $"--away-secs={away}" }
                    : System.Array.Empty<string>();
                var remove = new[] { "--presence", "--nearby-secs", "--away-secs" };
                foreach (var r in ApplyMerge(agent, paths, exePath, add, remove))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");

                // 2) Live state file — makes `off` revoke NOW (no reconnect) and `on` active immediately.
                outp.WriteLine(FlaUI.Mcp.Server.Presence.PresenceStateWriter.Set(mode == "on", nearby, away));
                outp.WriteLine($"Presence {mode.ToUpperInvariant()}. The live change is immediate; the launch default applies after the next /mcp reconnect.");
                return 0;
            }

            case "uninstall":
                Report(Apply(agent, paths, install: false, exePath), "uninstall", paths.DataDir, outp);
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
                bool acceptFlag = HasFlag(args, "--accept-risk") || HasFlag(args, "--i-understand");
                bool interactive = !Console.IsInputRedirected; // a real TTY; redirected stdin (CI) is non-interactive
                switch (Lease.LeaseWarning.Decide(minutes, acceptFlag, interactive))
                {
                    case Lease.LeaseWarningDecision.RefuseNeedsAck:
                        outp.WriteLine(Lease.LeaseWarning.Text(minutes));
                        outp.WriteLine("Refusing a long lease without acknowledgment. Re-run with --accept-risk (non-interactive) or from a terminal.");
                        return 2;
                    case Lease.LeaseWarningDecision.ProceedWithLoggedWarning:
                        outp.WriteLine(Lease.LeaseWarning.Text(minutes)); // on record in the log
                        if (interactive && !acceptFlag)
                        {
                            var line = Console.ReadLine();
                            if (!string.Equals(line?.Trim(), "I understand", StringComparison.Ordinal))
                            { outp.WriteLine("Not acknowledged; lease not granted."); return 2; }
                        }
                        break;
                    case Lease.LeaseWarningDecision.NoWarning: break;
                }
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
                outp.WriteLine("usage: flaui-mcp [install|uninstall [--purge-data]|print-config|status|unlock [--minutes N] [--allow-shells] [--accept-risk]|lock|overlay on|off|autosound on|off|presence on|off] [--agent agy|generic|claude|all] [--config <path>]");
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
        outp.WriteLine("  autosound on|off           Enable/disable a spoken cue when a target window needs your");
        outp.WriteLine("                             attention (it's not in the foreground). Off by default; flash");
        outp.WriteLine("                             is always on. Coexists with overlay (independent flag groups).");
        outp.WriteLine("  presence on|off            Enable/disable a coarse presence sensor (active/nearby/away) for");
        outp.WriteLine("                             desktop_user_state. Off by default; human-only; never exposes raw");
        outp.WriteLine("                             idle time. [--nearby-secs N] [--away-secs N] (away must exceed nearby).");
        outp.WriteLine("  unlock [--minutes N]       Grant a time-bounded synthetic-input lease (default 5 min).");
        outp.WriteLine("          [--allow-shells]   Also permit input into interlocked shells/terminals.");
        outp.WriteLine("          [--accept-risk]    Required (non-interactive) to grant leases over 60 minutes;");
        outp.WriteLine("                             interactive terminals get an 'I understand' prompt instead.");
        outp.WriteLine("  lock                       Revoke the synthetic-input lease immediately.");
        outp.WriteLine("  print-config               Print the generic mcpServers JSON snippet for manual setup.");
        outp.WriteLine("  status                     Report what is actually deployed: whether the agy seed");
        outp.WriteLine("                             driving skill landed, and the outcome of the last install");
        outp.WriteLine("                             (Setup runs the install hidden, so nothing is shown then).");
        outp.WriteLine("  --version, -v              Print the version.");
        outp.WriteLine("  --help, -h                 Show this help.");
        outp.WriteLine();
        outp.WriteLine("COMMON OPTIONS:");
        outp.WriteLine("  --agent agy|claude|generic|all   Target specific client(s) (default: all). Applies to");
        outp.WriteLine("                                   install / uninstall / overlay / autosound / presence.");
        outp.WriteLine("  --config <path>                  Override the config file path.");
        outp.WriteLine();
        outp.WriteLine("EXAMPLES:");
        outp.WriteLine("  flaui-mcp install");
        outp.WriteLine("  flaui-mcp overlay on               # watch the agent act (red rect before each action)");
        outp.WriteLine("  flaui-mcp unlock --minutes 30      # allow synthetic input for 30 minutes");
        outp.WriteLine("  flaui-mcp uninstall --purge-data");
    }

    private readonly record struct Paths(string AgyServers, string AgyPerms, string GenericPath, string DataDir, string AgyPluginsDir);

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
        var agyPlugins = Environment.GetEnvironmentVariable("FLAUI_MCP_AGY_PLUGINS_DIR")
                         ?? Path.Combine(home, ".gemini", "config", "plugins");
        return new Paths(agyServers, agyPerms, genericPath, dataDir, agyPlugins);
    }

    private static IEnumerable<AgentResult> Apply(string agent, Paths paths, bool install, string exePath, IReadOnlyList<string>? extraArgs = null)
    {
        var results = new List<AgentResult>();
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("agy", () =>
            {
                var w = new AgyConfigWriter(paths.AgyServers, paths.AgyPerms, paths.AgyPluginsDir);
                return install ? w.Install(exePath, extraArgs) : w.Uninstall();
            }));
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("claude", () =>
            {
                var w = new ClaudeCodeConfigWriter();
                return install ? w.Install(exePath, extraArgs) : w.Uninstall();
            }));
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("generic", () =>
            {
                var w = new GenericMcpConfigWriter();
                return install ? w.Install(paths.GenericPath, exePath, extraArgs) : w.Uninstall(paths.GenericPath);
            }));
        return results;
    }

    /// The agents are independent targets, so one agent's fault must not deny the others: a throw
    /// becomes that agent's own <see cref="AgentChange.Failed"/> result and the loop carries on.
    /// (Without this, `--agent all` on a machine with a corrupt agy config silently configured
    /// nothing at all — agy is attempted first.)
    private static AgentResult Isolate(string agent, Func<AgentResult> configure)
    {
        try { return configure(); }
        catch (Exception e) { return new AgentResult(agent, AgentChange.Failed, e.Message); }
    }

    /// Print each agent's outcome, persist the same lines, and point at the log when something went
    /// wrong. We never exit non-zero for a partial failure: Setup would roll back and delete the exe,
    /// leaving the user with nothing over one agent they may not even use.
    private static void Report(IEnumerable<AgentResult> results, string verb, string dataDir, TextWriter outp)
    {
        var lines = new List<string>();
        bool trouble = false;
        foreach (var r in results)
        {
            lines.Add($"[{r.Agent}] {r.Change}: {r.Detail}");
            if (r.Warning is not null) lines.Add($"[{r.Agent}] WARNING: {r.Warning}");
            if (r.Change == AgentChange.Failed || r.Warning is not null) trouble = true;
        }
        foreach (var line in lines) outp.WriteLine(line);

        var log = WriteLog(dataDir, verb, lines);
        if (trouble && log is not null)
            outp.WriteLine($"Some targets did not complete — see {log}");
    }

    /// Best-effort durable record of the last install/uninstall. Setup runs us `runhidden`
    /// (installer/flaui-mcp.iss), so stdout goes nowhere and an exit code could not say WHICH target
    /// failed anyway — this file is the only channel that outlives the run. Overwritten each time:
    /// the current state is what matters, and it must not grow without bound.
    private static string? WriteLog(string dataDir, string verb, IReadOnlyList<string> lines)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var path = Path.Combine(dataDir, InstallStatus.LogName);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var header = $"# flaui-mcp {ThisVersion()} — {verb} at {stamp}";
            File.WriteAllLines(path, new[] { header }.Concat(lines));
            return path;
        }
        catch { return null; }   // the reporter must never itself become the failure
    }

    /// Non-destructive sibling of <see cref="Apply"/>: dispatches `addArgs`/`removeArgs` to each writer's
    /// merge overload instead of the full-replace `Install(exePath, extraArgs)`, so a flag verb (overlay,
    /// autosound, ...) only touches its own flag group and preserves whatever other verbs already set.
    private static IEnumerable<AgentResult> ApplyMerge(string agent, Paths paths, string exePath, IReadOnlyList<string> addArgs, IReadOnlyList<string> removeArgs)
    {
        var results = new List<AgentResult>();
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("agy", () =>
            {
                var w = new AgyConfigWriter(paths.AgyServers, paths.AgyPerms, paths.AgyPluginsDir);
                return w.Install(exePath, addArgs, removeArgs);
            }));
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("claude", () =>
            {
                var w = new ClaudeCodeConfigWriter();
                return w.Install(exePath, addArgs, removeArgs);
            }));
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("generic", () =>
            {
                var w = new GenericMcpConfigWriter();
                return w.Install(paths.GenericPath, exePath, addArgs, removeArgs);
            }));
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
