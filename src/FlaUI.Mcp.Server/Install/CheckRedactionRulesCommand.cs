using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Install;

/// <summary>The `check-redaction-rules` dry-run verb (spec §5.5): validates a redaction-rule file on
/// disk and reports whether a running server is actually enforcing it. Read-only diagnostic — it
/// reads a FILE, never the desktop, and it deliberately does NOT call <see cref="ServerStateFile.PruneDead"/>:
/// a diagnostic that mutates the state it is diagnosing is the exact defect that constraint exists to
/// prevent. Pruning stale instance files is the SERVER's job, done at its own boot.</summary>
public static class CheckRedactionRulesCommand
{
    public static int Run(string[] args, TextWriter outp, string? instancesDir = null)
    {
        var path = ResolveRulesPath(args);
        if (path is null)
        {
            outp.WriteLine("usage: flaui-mcp check-redaction-rules <path> | --rules <path>");
            return 1;
        }
        if (!File.Exists(path))
        {
            outp.WriteLine($"Redaction rule file not found: '{path}'.");
            return 1;
        }

        byte[] bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        RedactionRule[] rules;
        try
        {
            rules = RedactionRuleFile.Parse(bytes, path);
        }
        catch (RedactionConfigException ex)
        {
            PrintBanner(outp);
            outp.WriteLine(ex.Message);
            return 1;
        }

        PrintBanner(outp);
        PrintResults(outp, path, rules);

        // The LIVE half of the dry-run (spec §5.5). Both are opt-in flags: the default invocation stays a
        // pure file check that never acquires UIA, so it remains usable in CI and over a pipe.
        // A malformed live-half argument fails the command (exit 1, the usage/validation class) rather than
        // letting the server-state evaluation below decide the exit code — see RunLive's note (finding S5).
        if (!RunLive(outp, args, rules)) return 1;

        var instances = ServerStateFile.ReadAll(instancesDir ?? ServerStateFile.DefaultDirectory);

        var live = instances.Where(i => i.Liveness == ServerLiveness.Live).ToList();
        if (live.Any(i => string.Equals(i.RulesSha256, sha, StringComparison.OrdinalIgnoreCase)))
            return 0;

        if (live.Count > 0)
        {
            var other = live.FirstOrDefault(i => i.RulesPath is not null);
            var loaded = other?.RulesPath is not null ? $" (it loaded '{other.RulesPath}')" : "";
            outp.WriteLine($"A running server is enforcing a DIFFERENT redaction rule file than the one checked{loaded}.");
            return 3;
        }

        var skewed = instances.FirstOrDefault(i => i.StateVersion != ServerStateFile.CurrentStateVersion);
        if (skewed is not null)
        {
            // ⚠ CAPSTONE ROUND 2 (finding Q-C, the "downgrade brick"). Exit 4 STAYS — it is the honest
            // answer while a state file exists that this build cannot interpret. The tempting fix, ignoring
            // a skewed file whose pid looks dead, would break acceptance constraint (b): if that pid is not
            // really a process id and the newer server IS live, we would report "no server running" while a
            // server is enforcing rules, which is the one thing this command must never say.
            //
            // What WAS wrong is the remedy text. A skewed file outlives its process (PruneDead cannot
            // safely delete a NEWER version's file), so after a downgrade this message repeats forever
            // while telling the operator to UPGRADE — advice that is backwards for the case that produces
            // it most often, and that never names the file blocking the diagnostic.
            int current = ServerStateFile.CurrentStateVersion;
            string direction = skewed.StateVersion > current
                ? "written by a NEWER server than this build"
                : "left behind by an OLDER server";
            outp.WriteLine(
                $"Cannot determine: an instance state file is {direction} — it declares state version " +
                $"{skewed.StateVersion.ToString(CultureInfo.InvariantCulture)}, this build understands " +
                $"{current.ToString(CultureInfo.InvariantCulture)}.");
            outp.WriteLine($"  file: {skewed.FilePath}");
            // ⚠ The NEWER branch must keep the word "upgrade": a newer state file means a newer server, and
            // upgrading this CLI really is the remedy. That is pinned by
            // CheckRedactionRulesCliTests.Version_skew_instance_exits_4_and_mentions_upgrading, whose
            // fixture is version 999 — it caught this wording change, and it was right to. Only the OLDER
            // branch, which that pin does not cover, drops the upgrade advice, because telling someone who
            // deliberately downgraded to upgrade is backwards.
            outp.WriteLine(skewed.StateVersion > current
                ? "  Upgrade this CLI to the matching (newer) version, or delete that file if its server is no longer running."
                : "  Delete that file if its server is no longer running, then re-run.");
            return 4;
        }

        if (instances.Any(i => i.Liveness == ServerLiveness.Unknown))
        {
            outp.WriteLine(
                "Cannot determine: a running instance's state could not be read (access denied — the server may be " +
                "running elevated). Re-run this command elevated to confirm what it loaded.");
            return 4;
        }

        outp.WriteLine("No running server was found.");
        return 5;
    }

    // Rule-matched output shows the RAW rule fields (an operator debugging their own regex needs to
    // see them, not a redacted view) — but this command reads a FILE, never the desktop, so it must
    // never print an element VALUE. RedactionRule's public surface (Name/ProcessName/Global/AutomationId)
    // is exactly what is safe and available to print without acquiring UIA or touching a live element.
    private static void PrintResults(TextWriter outp, string path, RedactionRule[] rules)
    {
        outp.WriteLine($"'{path}' is valid: {rules.Length} rule(s).");
        foreach (var r in rules)
        {
            var scope = r.Global ? "global" : $"processName={r.ProcessName}";
            var aid = r.AutomationId is not null ? $" automationId={r.AutomationId}" : "";
            outp.WriteLine($"  rule '{r.Name}': {scope}{aid}");
        }
    }

    /// <summary>The live half: `--list-windows` and `--window &lt;pid&gt;`. Silent and UIA-free when neither
    /// flag is present.
    ///
    /// ⚠ ONE dispatcher and ONE WindowManager for both operations, deliberately. Window handles ("w1") are
    /// minted PER WindowManager INSTANCE, so a handle printed by one instance is meaningless to another —
    /// two managers in one invocation would print handles that the very next flag could not resolve.
    ///
    /// ⚠ And that is also why `--window` takes a **PID**, not a "w1" handle. This is a CLI: the operator
    /// runs it once to list and again to walk, in two separate PROCESSES. A per-instance handle cannot
    /// survive that boundary, so offering one would be an interface that works only in the single-invocation
    /// case and fails confusingly in the normal one. A PID is stable across processes and is what
    /// `--list-windows` prints.</summary>
    /// <summary>Returns FALSE when an argument was malformed, so the caller can fail the whole command.
    ///
    /// ⚠ CAPSTONE ROUND 3 (finding S5). This used to return void: a malformed `--window` printed its
    /// complaint and then fell through to the server-state evaluation, so `check-redaction-rules rules.json
    /// --window abc` could print "expects a PID" and still EXIT 0 whenever a server happened to be running
    /// with a matching hash. A command that rejects an argument must never report success — a script
    /// checking only the exit code would read that as a clean pass.</summary>
    private static bool RunLive(TextWriter outp, string[] args, RedactionRule[] rules)
    {
        bool list = HasFlag(args, "--list-windows");
        string? pidArg = OptionValue(args, "--window");
        if (!list && pidArg is null) return true;

        using var dispatcher = new FlaUI.Mcp.Core.Threading.AutomationDispatcher();
        using var windows = new WindowManager(dispatcher);

        if (list)
        {
            // Reuses ListWindowsAsync deliberately, NOT a raw UIA walk: it is pure Win32
            // (WindowManager.cs:74-76 — a UIA Title/ProcessId read blocks with no timeout on any
            // momentarily-unresponsive window), so listing candidates can never hang this diagnostic on
            // some unrelated frozen app.
            var found = windows.ListWindowsAsync(includeBounds: false, includeHandles: true)
                               .GetAwaiter().GetResult();
            outp.WriteLine($"windows ({found.Count}):");
            foreach (var w in found)
                outp.WriteLine($"  pid={w.Pid} [{w.ProcessName}] {w.Title}");
        }

        if (pidArg is null) return true;
        if (!int.TryParse(pidArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
        {
            outp.WriteLine($"--window expects a PID (as printed by --list-windows); got '{pidArg}'.");
            return false;
        }
        MatchWindow(outp, windows, pid, rules);
        return true;
    }

    /// <summary>Walks ONE window and reports which elements these rules would actually withhold — the
    /// question a dry-run exists to answer, which validating the file alone cannot.
    ///
    /// ⚠ OUTPUT POLICY (spec §5.5), and the two halves pull in opposite directions ON PURPOSE:
    /// a RULE-matched element is printed with its RAW name, because the operator is a local human debugging
    /// their own regex against their own screen and a redacted view makes rule refinement impossible; but an
    /// OS-password element stays "[REDACTED]" regardless, because that secret is not the operator's rule to
    /// debug. NO element VALUE is read on any path — only the name — so a password's contents cannot reach
    /// this output even by accident.
    ///
    /// The raw name comes from <see cref="ElementContent.Read.RawForIdentity"/> rather than a direct
    /// el.Name read, so this stays inside the closed egress-accessor set (spec §7.2) and needs no new
    /// exemption from the source sweep.</summary>
    private static void MatchWindow(TextWriter outp, WindowManager windows, int pid, RedactionRule[] rules)
    {
        var classifier = SensitivityClassifier.ForRules(rules);
        int matched = 0, scanned = 0;
        try
        {
            var handle = windows.OpenByPidAsync(pid).GetAwaiter().GetResult();
            windows.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            {
                string? proc = SafeProcName(win);
                foreach (var el in win.FindAllDescendants())
                {
                    scanned++;
                    var read = ElementContent.Name(el, classifier, proc);
                    if (!read.Sensitivity.Redact) continue;
                    matched++;
                    bool os = read.Sensitivity.Source == RedactionSource.Os;
                    string shown = os ? ElementContent.RedactedToken : read.RawForIdentity;
                    string why = os ? "os-password" : $"rule:{read.Sensitivity.RuleName}";
                    string aid = SafeAid(el);
                    outp.WriteLine($"  [{why}] name={shown}{(aid.Length > 0 ? $" automationId={aid}" : "")}");
                }
                return 0;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            outp.WriteLine($"Could not walk the window of pid {pid}: {ex.Message}");
            return;
        }
        outp.WriteLine($"would withhold {matched} of {scanned} element(s) in pid {pid}.");
    }

    private static string? SafeProcName(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        try { return System.Diagnostics.Process.GetProcessById(el.Properties.ProcessId.ValueOrDefault).ProcessName; }
        catch { return null; }
    }

    private static string SafeAid(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        try { return el.Properties.AutomationId.ValueOrDefault ?? string.Empty; } catch { return string.Empty; }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? OptionValue(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void PrintBanner(TextWriter outp)
    {
        outp.WriteLine("NOTE: these results reflect the RULE FILE on disk. A running server enforces the rules it loaded at");
        outp.WriteLine("boot; restart it to apply changes. Exit code 0 means the running server is already enforcing this file.");
    }

    private static string? ResolveRulesPath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--rules", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }
        // The token after the verb itself, if not an option.
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "check-redaction-rules", StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        }
        return null;
    }
}
