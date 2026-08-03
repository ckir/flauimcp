using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using FlaUI.Mcp.Core.Perception;

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
            outp.WriteLine(
                $"Cannot determine: a running instance wrote state version {skewed.StateVersion}, but this build " +
                $"understands version {ServerStateFile.CurrentStateVersion.ToString(CultureInfo.InvariantCulture)}. " +
                "Upgrade the CLI and the server to matching versions, then re-run.");
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
