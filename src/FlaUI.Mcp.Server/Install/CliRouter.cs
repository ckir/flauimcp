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
                outp.WriteLine(InstallStatus.Describe(exePath, paths.AgyPluginsDir, paths.DataDir, paths.ClaudeConfigDir, paths.StateDir));
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
                var uninstallResults = Apply(agent, paths, install: false, exePath).ToList();
                Report(uninstallResults, "uninstall", paths.DataDir, outp);
                // The exe is about to be deleted and install.log may be purged, so a warning written
                // now is destroyed as it is written. Park it where the Inno uninstaller can find it.
                UninstallWarnings.Write(paths.StateDir,
                    uninstallResults.Where(r => r.Warning is not null).Select(r => $"[{r.Agent}] {r.Warning}").ToList());

                // Canonical UNINSTALL: the staging dir is SHARED — Claude's plugin registration points its
                // marketplace entry straight at it, so it is still live-mounted by any agent NOT targeted by
                // this run. Only delete it when (a) deregister fully succeeded — a failed deregister may
                // still hold a live reference we must not yank out from under it — AND (b) this is a full
                // (`--agent all`) uninstall, never a targeted `--agent agy`/`--agent claude` one. On failure,
                // leave the dir and write a durable warning instead of guessing. (plan-review R3.)
                var stagingDirForUninstall = Environment.GetEnvironmentVariable("FLAUI_MCP_STAGING_DIR")
                                 ?? Path.Combine(Path.GetDirectoryName(exePath)!, "plugin");
                var deregisterFailed = uninstallResults.Any(r => r.Change == AgentChange.Failed);
                bool fullUninstall = agent.Equals("all", StringComparison.OrdinalIgnoreCase);
                if (deregisterFailed)
                {
                    WriteUninstallWarning(paths.StateDir, stagingDirForUninstall, paths.AgyPluginsDir);
                }
                else if (fullUninstall)
                {
                    try { if (Directory.Exists(stagingDirForUninstall)) Directory.Delete(stagingDirForUninstall, true); } catch { /* leave on error */ }
                }

                // Leave no leftovers: delete the timestamped backups we wrote next to each config
                // file we touched. (Backups are a safety net for the live install, not after removal.)
                foreach (var line in SweepBackups(PathsForAgent(agent, paths)))
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

    private readonly record struct Paths(string AgyServers, string AgyPerms, string GenericPath, string DataDir, string AgyPluginsDir, string ClaudeConfigDir, string StateDir);

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

        // Claude Code honors CLAUDE_CONFIG_DIR (measured: with it pointed at an empty dir,
        // `claude plugin list` reports "No plugins installed"). A hardcoded ~/.claude would write
        // where the host is not reading, for any user who sets it. FLAUI_MCP_* wins so tests never
        // touch the real profile.
        var claudeConfigDir = Environment.GetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR")
                              ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                              ?? Path.Combine(home, ".claude");

        // NOT under dataDir: --purge-data deletes that, and it is not agent-scoped (see :18), so an
        // `uninstall --agent agy --purge-data` would take the Claude restore marker with it.
        // NOT under {app}: Inno deletes that on both uninstall branches.
        var stateDir = Environment.GetEnvironmentVariable("FLAUI_MCP_STATE_DIR")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlaUI.Mcp", "state");
        return new Paths(agyServers, agyPerms, genericPath, dataDir, agyPlugins, claudeConfigDir, stateDir);
    }

    private static IEnumerable<AgentResult> Apply(string agent, Paths paths, bool install, string exePath, IReadOnlyList<string>? extraArgs = null)
    {
        var results = new List<AgentResult>();
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);

        // The isolated staging dir the plugin artifacts are generated into. Default {app}\plugin (next
        // to the exe); FLAUI_MCP_STAGING_DIR redirects it so tests never touch a real install tree.
        var stagingDir = Environment.GetEnvironmentVariable("FLAUI_MCP_STAGING_DIR")
                         ?? Path.Combine(Path.GetDirectoryName(exePath)!, "plugin");

        // agy branch — canonical INSTALL: generate the plugin dir, sweep any retired hand-written
        // config, then register via `agy plugin install`. No hand-written agy config anymore.
        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("agy", () =>
            {
                if (install)
                {
                    new PluginArtifactWriter(stagingDir).Generate(exePath, ThisVersion());
                    // Legacy cleanup (migration sweep) — reuse the retired writer's Uninstall(); swallow.
                    new AgyConfigWriter(paths.AgyServers, paths.AgyPerms, paths.AgyPluginsDir).Uninstall();
                    RemoveStrayAgyPluginMcpJson(paths.AgyPluginsDir);
                    return new AgyPluginRegistrar(AgyInvoker()).Register(stagingDir);
                }
                return new AgyPluginRegistrar(AgyInvoker()).Unregister();
            }));

        // claude branch — canonical INSTALL: generate, sweep the retired `claude mcp` server + legacy
        // skills dir, disable any colliding legacy copy, then register the local marketplace plugin.
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("claude", () =>
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew(); // preserve the live budget clock (was :255-256)
                var remedy = new ClaudeCollisionRemedy(ClaudeRunner(() => sw2.Elapsed, ClaudeBudget), paths.StateDir);
                if (install)
                {
                    new PluginArtifactWriter(stagingDir).Generate(exePath, ThisVersion()); // idempotent if agy already ran
                    new ClaudeCodeConfigWriter(ClaudeRunner(() => sw2.Elapsed, ClaudeBudget)).Uninstall(); // sweep retired `claude mcp` server
                    new ClaudeSkillDeployer(paths.ClaudeConfigDir).Remove(); // drop legacy ~/.claude/skills/flaui-mcp (skill now ships in the plugin)
                    var collisionWarning = remedy.Apply();
                    var reg = new ClaudePluginRegistrar(ClaudeInvoker()).Register(stagingDir);
                    return FoldWarning(reg, collisionWarning);
                }
                var unreg = new ClaudePluginRegistrar(ClaudeInvoker()).Unregister();
                var restoreWarning = remedy.Restore();
                return FoldWarning(unreg, restoreWarning);
            }));
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
            results.Add(Isolate("generic", () =>
            {
                var w = new GenericMcpConfigWriter();
                return install ? w.Install(paths.GenericPath, exePath, extraArgs) : w.Uninstall(paths.GenericPath);
            }));
        return results;
    }

    /// Total wall-clock a single install/uninstall claude-branch pass may spend. The per-call 30s
    /// DefaultTimeout already bounds ONE hang; this caps the (1 + 2N)*30s pile-up when several hang.
    /// 120s = 4x DefaultTimeout — a full call of grace over the realistic worst case (N<=3 colliding
    /// copies), so overhead on hung predecessors never squeezes a legitimately slow later call.
    internal static readonly TimeSpan ClaudeBudget = TimeSpan.FromSeconds(120);

    /// The timeout to hand the next claude call given the elapsed pass time, or null when the budget is
    /// spent (the caller short-circuits to TimedOut without launching a process). Pure — unit-tested.
    internal static TimeSpan? BudgetedTimeout(TimeSpan budget, TimeSpan elapsed)
    {
        var remaining = budget - elapsed;
        if (remaining <= TimeSpan.Zero) return null;
        return remaining < ProcessRunner.DefaultTimeout ? remaining : ProcessRunner.DefaultTimeout;
    }

    /// The claude runner, with a test seam for the "CLI absent" case — the branch that gates the
    /// skill deploy, and the one a machine without Claude Code actually takes.
    private static Func<string, string[], string?, RunResult> ClaudeRunner(Func<TimeSpan> elapsed, TimeSpan budget)
    {
        if (Environment.GetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING") == "1")
            return (_, _, _) => new RunResult(ProcessRunner.NotFound, "");

        // Test seam: simulate a Claude Code reporting ONE enabled colliding plugin, so the router's
        // collision path is exercisable without the real CLI or a live marketplace. `plugin list --json`
        // reports the id as enabled; `disable`/`enable`/`mcp` all succeed (exit 0).
        var collision = Environment.GetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_COLLISION");
        if (!string.IsNullOrEmpty(collision))
            return (_, args, _) => args.Contains("list")
                ? new RunResult(0, $"[{{\"id\":\"{collision}\",\"scope\":\"user\",\"enabled\":true}}]")
                : new RunResult(0, "");

        // Test seam: simulate Claude Code PRESENT with NO colliding plugin — every command succeeds
        // (exit 0) and `plugin list --json` reports an empty array. This lets the skill-deploy path run on
        // a host WITHOUT the real CLI (e.g. a headless CI runner); without it, a skill-deploy test would
        // silently depend on `claude` being on the runner's PATH. Both seams above are checked BEFORE
        // this one, so a test can still force "absent" (MISSING wins) or a specific collision (COLLISION wins).
        if (Environment.GetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_PRESENT") == "1")
            return (_, args, _) => args.Contains("list")
                ? new RunResult(0, "[]")
                : new RunResult(0, "");

        return (file, args, cwd) =>
        {
            var timeout = BudgetedTimeout(budget, elapsed());
            if (timeout is null) return new RunResult(ProcessRunner.TimedOut, "");
            return ProcessRunner.Run(file, args, cwd, timeout.Value);
        };
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

    /// The backup paths to sweep for a given `--agent`, mirroring the Apply() dispatch. Claude has no
    /// local config file (opaque `claude mcp` CLI) → no paths → the sweep is a no-op for it. "all" (also
    /// the default when --agent is omitted, CliRouter.cs:16) is the union — a bare/full uninstall still
    /// sweeps. Keep this in lockstep with the agent branches in Apply().
    private static IEnumerable<string> PathsForAgent(string agent, Paths paths)
    {
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);
        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
        { yield return paths.AgyServers; yield return paths.AgyPerms; }
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
            yield return paths.GenericPath;
        // claude → nothing; unknown → nothing (Apply configures nothing either).
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

    // 3-part semver: the plugin manifests (PluginArtifactWriter/ClaudeSkillDeployer) want Major.Minor.Build,
    // and the user-facing `--version` / log header read cleaner without the trailing revision.
    private static string ThisVersion() =>
        typeof(CliRouter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // Merge an extra warning (e.g. ClaudeCollisionRemedy.Apply/Restore) into a registrar's AgentResult
    // so the operator still sees the disable/restore detail. Mirrors the old fold at the retired :291.
    private static AgentResult FoldWarning(AgentResult r, string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return r;
        var combined = string.IsNullOrEmpty(r.Warning) ? extra : $"{r.Warning} {extra}";
        return r with { Warning = combined };
    }

    // Build a CliInvoker for agy/claude. `run` honors the FAKE_*_PRESENT/MISSING env seams like ClaudeRunner;
    // `resolve` honors the presence seams so e2e tests need no real CLI.
    private static CliInvoker AgyInvoker()    => BuildInvoker("agy",    "FLAUI_MCP_FAKE_AGY_PRESENT",    "FLAUI_MCP_FAKE_AGY_MISSING");
    private static CliInvoker ClaudeInvoker() => BuildInvoker("claude", "FLAUI_MCP_FAKE_CLAUDE_PRESENT", "FLAUI_MCP_FAKE_CLAUDE_MISSING");

    private static CliInvoker BuildInvoker(string cli, string presentVar, string missingVar)
    {
        var forcedPresent = Environment.GetEnvironmentVariable(presentVar) == "1";
        var forcedMissing = Environment.GetEnvironmentVariable(missingVar) == "1";
        // Test seam: force Unregister()/Register() to observe a non-zero exit (e.g. `agy plugin uninstall`
        // failing) without a real CLI — exercises the uninstall fail-open warning path. Checked in `run`
        // only: `resolve`/IsPresent is untouched, so with PRESENT still set, Unregister() proceeds past the
        // IsPresent guard into Invoke, which then returns this forced failure.
        var forcedFail = Environment.GetEnvironmentVariable(cli == "agy" ? "FLAUI_MCP_FAKE_AGY_FAIL" : "FLAUI_MCP_FAKE_CLAUDE_FAIL") == "1";
        Func<string, string?> resolve = c =>
            forcedMissing ? null : forcedPresent ? $"<fake:{c}>" : CliResolver.Resolve(c);
        Func<string, string[], string?, RunResult> run = (file, args, cwd) =>
            forcedFail    ? new RunResult(1, "forced") :
            forcedPresent ? new RunResult(0, FakeCliOutput(args)) :
                            ProcessRunner.Run(file, args, cwd, ProcessRunner.DefaultTimeout);
        return new CliInvoker(run, resolve);
    }

    // Fail-open warning for a failed deregister — the staging dir is left in place (see the "uninstall"
    // case's delete gating) because it may still be referenced, so this tells the operator how to finish
    // the job safely instead of guessing. Appended (not overwritten): a prior UninstallWarnings.Write call
    // in the same run may already have written collision-restore warnings to this same file.
    private static void WriteUninstallWarning(string stateDir, string stagingDir, string agyPluginsDir)
    {
        var managed = Path.Combine(agyPluginsDir, "flaui-mcp");
        var msg =
            "flaui-mcp could not be fully removed from one or more agents. The plugin may still load.\n" +
            "Run these BEFORE manually deleting any directory (deleting a still-referenced dir breaks agent startup):\n" +
            "  claude plugin marketplace remove flaui-mcp-marketplace\n" +
            "  agy plugin uninstall flaui-mcp\n" +
            "Locations, if manual cleanup is still needed afterwards:\n" +
            $"  agy managed copy:   {managed}\n" +
            "  (agy stores its copy under ~/.gemini/config/plugins/flaui-mcp)\n" +
            $"  staged build dir:   {stagingDir}\n";
        try
        {
            Directory.CreateDirectory(stateDir);
            File.AppendAllText(UninstallWarnings.PathIn(stateDir), msg);
        }
        catch { /* best-effort: the warning channel itself must never throw and abort uninstall */ }
    }

    // Under the PRESENT seam, `plugin list` must report our marketplace id as active so the read-back passes.
    private static string FakeCliOutput(string[] args) =>
        System.Array.IndexOf(args, "list") >= 0 ? $"{PluginIds.InstallTarget}  enabled" : "";

    // Best-effort migration cleanup: delete a stray {AgyPluginsDir}/flaui-mcp/.mcp.json a prior build left.
    private static void RemoveStrayAgyPluginMcpJson(string agyPluginsDir)
    {
        try
        {
            var stray = Path.Combine(agyPluginsDir, "flaui-mcp", ".mcp.json");
            if (File.Exists(stray)) File.Delete(stray);
        }
        catch { /* best-effort migration cleanup */ }
    }
}
