using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Neutralises the v0.14.x marketplace copy of our own plugin, reversibly.
///
/// WHY THIS EXISTS: anyone who followed the README installed `flaui-mcp@flaui-mcp` from the
/// marketplace. Once the installer also bundles `flaui-mcp@skills-dir`, both ship a skill named
/// `driving-flaui-mcp`. MEASURED 2026-07-16: the collision is SILENT — both plugins report loaded and
/// nothing warns — so the user unknowingly runs a drifted v0.14.0 skill against a new binary. Doing
/// nothing is not an option; neither is warning only (Setup is runhidden, so a warning reaches nobody).
///
/// DISABLE, NOT UNINSTALL: both stop the drifted skill loading, but disable is reversible, and
/// uninstall would silently destroy something the user installed deliberately because our own README
/// told them to.
///
/// DETECTION IS BY MUTATION, NOT BY READING `enabled`. MEASURED 2026-07-16 (plan Task 2): `claude
/// plugin list --json` resolves the `enabled` field of a `scope=local` row against the CURRENT working
/// directory, not the row's own projectPath. A single global read from Setup's cwd (which has no
/// `.claude`) reports every local copy as disabled — so branching on `enabled` would silently skip a
/// live local collision, the exact failure this class exists to prevent. Instead we ATTEMPT the
/// disable at each row's own scope+cwd and read the OUTCOME: `disable` reports exit 0 when it actually
/// turned an enabled entry off, and exit 1 ("already disabled") when it was already off. The global
/// list is used only to ENUMERATE rows by id (id/scope/projectPath ARE global and stable); `enabled`
/// is re-read only at a row's own projectPath, and only to disambiguate a failed disable.
/// </summary>
public sealed class ClaudeCollisionRemedy
{
    public const string MarketplaceId = "flaui-mcp@flaui-mcp";

    private readonly Func<string, string[], string?, RunResult> _run;
    private readonly string _stateDir;
    private readonly Func<string, bool> _dirExists;

    /// <param name="dirExists">Injected for testability; defaults to <see cref="Directory.Exists"/>.
    /// A non-user entry whose project directory is gone cannot load the plugin, and `disable` would
    /// have nowhere valid to run from — such entries are skipped.</param>
    public ClaudeCollisionRemedy(Func<string, string[], string?, RunResult> run, string stateDir,
        Func<string, bool>? dirExists = null)
    {
        _run = run;
        _stateDir = stateDir;
        _dirExists = dirExists ?? Directory.Exists;
    }

    /// <summary>Install side: disable each enabled colliding entry and record it. Returns a warning
    /// summary, or null when there was nothing to say.</summary>
    public string? Apply()
    {
        var warnings = new List<string>();

        if (!TryReadInventory(out var entries, out var listWarning))
            return listWarning;

        var matching = ClaudePluginInventory.Matching(entries, MarketplaceId);
        if (matching.Count == 0) return null;

        var recorded = CollisionMarker.Read(_stateDir);
        var justDisabled = new List<DisabledEntry>();

        foreach (var e in matching)
        {
            var entry = new DisabledEntry(e.Id, e.Scope, e.ProjectPath);

            // A non-user entry whose project directory is gone cannot load the plugin (no collision),
            // and `disable` would have nowhere valid to run from. Skip it; a stale marker record for
            // it is handled at restore, not here.
            if (e.ProjectPath is not null && !_dirExists(e.ProjectPath))
            {
                warnings.Add($"a conflicting {e.Id} was listed for {e.ProjectPath}, which no longer exists — skipped it.");
                continue;
            }

            // MUTATION-AS-DETECTOR. We do NOT branch on e.Enabled — it is CWD-resolved and unreliable
            // for local rows (see class summary). `disable` at the row's own cwd IS the detector, and
            // the scope is not a free parameter: `claude plugin disable` cannot target another project,
            // so it must RUN from that entry's projectPath (null = user scope = anywhere).
            var r = _run("claude", new[] { "plugin", "disable", e.Id, "--scope", e.Scope }, e.ProjectPath);
            if (r.Code == 0)
            {
                justDisabled.Add(entry);   // it was enabled; WE transitioned it enabled -> disabled
                continue;
            }
            if (r.Code == ProcessRunner.NotFound || r.Code == ProcessRunner.TimedOut)
            {
                // -1 (NotFound) conflates "claude not on PATH" with "working directory unavailable"
                // (Process.Start throws Win32Exception for both). Enumeration just succeeded, so a -1
                // here is more likely the latter — do not assert the CLI is missing. (agy panel round 3.)
                warnings.Add($"could not disable the conflicting {e.Id} ({Where(entry)}): claude " +
                             (r.Code == ProcessRunner.TimedOut
                                 ? "timed out."
                                 : "could not be run (not on PATH, or its project directory was unavailable).") +
                             " Two copies of the driving skill may be active.");
                continue;
            }

            // Non-zero, non-sentinel: `disable` refuses an already-disabled entry with exit 1, which
            // is ambiguous with a genuine failure (permissions, corrupt settings). The distinguishing
            // text is a human-readable message we must NOT parse — re-read the entry's ACTUAL state
            // from its OWN project directory, the one context where `enabled` is reliable for it.
            var stillEnabled = ReadEnabledAt(entry);
            if (stillEnabled == true)
            {
                warnings.Add($"could not disable the conflicting {e.Id} ({Where(entry)}): claude exited {r.Code}. " +
                             "Two copies of the driving skill may now be active.");
            }
            else if (stillEnabled == false)
            {
                // Already disabled. A record of our own means this is a reinstall over our prior
                // disable — stay silent (R1). No record means the USER disabled it: leave it, and SAY
                // so (R5), because this correct decision is otherwise invisible. Case-insensitive, so a
                // path differing only in casing is the SAME entry and we do not blame the user for our
                // own disable.
                if (!recorded.Concat(justDisabled).Any(m => CollisionMarker.SameEntry(m, entry)))
                    warnings.Add($"{e.Id} ({Where(entry)}) was already disabled and we have no record of disabling it — " +
                                 "assuming you did, and leaving it alone.");
            }
            else
            {
                // disable failed AND the state could not be re-read. Report conservatively; record
                // nothing, because no transition was ever confirmed.
                warnings.Add($"could not disable the conflicting {e.Id} ({Where(entry)}) and could not verify its state. " +
                             "Two copies of the driving skill may be active.");
            }
        }

        // Only entries WE transitioned are recorded; existing records are never rewritten (R1).
        // A marker we cannot write is NOT a silent shrug: we have already disabled the user's plugin,
        // so a lost record means uninstall never puts it back.
        var recordWarning = CollisionMarker.Record(_stateDir, justDisabled);
        if (recordWarning is not null) warnings.Add(recordWarning);

        if (justDisabled.Count > 0)
            // Only PROMISE a restore if we actually recorded it. If Record failed, recordWarning
            // already tells the user it will NOT be re-enabled — promising the opposite in the same
            // concatenated line is worse than saying nothing. (agy panel round 2.)
            warnings.Insert(0, recordWarning is null
                ? $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill " +
                  "(they will be re-enabled if you uninstall flaui-mcp)."
                : $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill.");

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    /// <summary>Uninstall side: put back exactly what we disabled, then consume the marker (R7).</summary>
    public string? Restore()
    {
        try
        {
            var (state, recorded) = CollisionMarker.ReadState(_stateDir);

            if (state == MarkerState.Absent) return null;
            if (state == MarkerState.FutureVersion)
                return $"the restore record at {CollisionMarker.PathIn(_stateDir)} was written by a newer " +
                       "flaui-mcp; it was left in place and not acted on.";
            if (state == MarkerState.Corrupt)
                return $"the restore record at {CollisionMarker.PathIn(_stateDir)} is unreadable, so any " +
                       "conflicting plugin(s) we disabled may still be disabled and could not be re-enabled " +
                       "automatically. To check: run `claude plugin list`, and re-enable " +
                       $"{MarketplaceId} wherever it is disabled.";
            if (recorded.Count == 0) return null;   // Present, but every entry was dropped as malformed

            var warnings = new List<string>();

            // If we cannot read the inventory at all (e.g. the claude CLI is gone), we cannot restore —
            // and we must NOT consume the marker. It is still an ACCURATE record of a plugin we disabled
            // and have not put back; deleting it here would strand the user's plugin disabled with no
            // record anywhere that we were the ones who did it. R7's stale-marker hazard is about a
            // marker surviving a SUCCESSFUL consume, which this is not.
            if (!TryReadInventory(out var entries, out var listWarning))
            {
                // Manual recourse, but only for entries we could actually act on: an entry whose project
                // directory is gone is moot AND a `cd` into a deleted path is impossible, so listing it
                // would resurrect the same bad-recourse defect the in-loop guard below fixes. (agy panel
                // round 2 — the early return bypassed that guard.)
                var recoverable = recorded.Where(e => e.ProjectPath is null || _dirExists(e.ProjectPath)).ToList();
                var recourse = recoverable.Count == 0
                    ? "No manual action is possible (the recorded projects no longer exist)."
                    : "To restore manually: " + string.Join("; ", recoverable.Select(e =>
                        $"claude plugin enable {e.Id} --scope {e.Scope}" +
                        (e.ProjectPath is null ? "" : $" (run from {e.ProjectPath})")));
                return $"{listWarning} Your conflicting plugin(s) are still disabled and were NOT re-enabled. " +
                       "The record is kept at " + CollisionMarker.PathIn(_stateDir) + ". " + recourse;
            }

            var present = ClaudePluginInventory.Matching(entries, MarketplaceId);

            foreach (var e in recorded)
            {
                // Symmetric to Apply's guard: a project deleted AFTER we disabled the copy but BEFORE
                // uninstall cannot load the plugin (the collision is moot) and `enable` has nowhere valid
                // to run from. MEASURED: Process.Start with a missing working directory throws
                // Win32Exception, which ProcessRunner surfaces as a failed run — so without this guard we
                // would fall through to the failure branch below and print an impossible "run it from
                // <deleted path>" recourse. The `present` check does NOT catch this: the inventory can
                // still LIST a stale row for a deleted project (measured).
                if (e.ProjectPath is not null && !_dirExists(e.ProjectPath))
                {
                    warnings.Add($"{e.Id} ({Where(e)}) — its project directory no longer exists, so there was nothing to re-enable.");
                    continue;
                }

                // R2: the user may have uninstalled it themselves after we disabled it. Enabling a
                // plugin that no longer exists writes a phantom {id:true} (measured: enable succeeds for a
                // nonexistent id) — check the id is still installed first.
                if (!present.Any(p => Same(p, e)))
                {
                    warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, so it was not re-enabled.");
                    continue;
                }

                var r = _run("claude", new[] { "plugin", "enable", e.Id, "--scope", e.Scope }, e.ProjectPath);
                if (r.Code != 0)
                    warnings.Add($"could not re-enable {e.Id} ({Where(e)}): claude {DescribeExit(r.Code)}. " +
                                 $"To restore it yourself: claude plugin enable {e.Id} --scope {e.Scope}" +
                                 (e.ProjectPath is null ? "" : $" (run it from {e.ProjectPath})"));
            }

            // R7: deleting the marker is part of consuming it. Delete even when a restore failed — a
            // surviving marker would later re-enable a plugin the user had deliberately disabled, which
            // is the exact outcome R1 exists to prevent. The failure is reported instead.
            var deleteWarning = CollisionMarker.Delete(_stateDir);
            if (deleteWarning is not null) warnings.Add(deleteWarning);

            return warnings.Count == 0 ? null : string.Join(" ", warnings);
        }
        finally
        {
            CollisionMarker.SweepBackups(_stateDir);
        }
    }

    /// <summary>Re-read one entry's ACTUAL enabled state from its OWN project directory — the only
    /// context in which `list --json`'s CWD-resolved `enabled` is correct for a local-scope row. Null
    /// when it cannot be read back. Used ONLY to disambiguate a failed disable, never as the primary
    /// detector.</summary>
    private bool? ReadEnabledAt(DisabledEntry e)
    {
        var r = _run("claude", new[] { "plugin", "list", "--json" }, e.ProjectPath);
        if (r.Code != 0) return null;
        var row = ClaudePluginInventory.Parse(r.Output)
            .FirstOrDefault(p => CollisionMarker.SameEntry(new DisabledEntry(p.Id, p.Scope, p.ProjectPath), e));
        return row?.Enabled;
    }

    private bool TryReadInventory(out IReadOnlyList<ClaudePluginEntry> entries, out string? warning)
    {
        var r = _run("claude", new[] { "plugin", "list", "--json" }, null);   // enumeration is global
        if (r.Code != 0)
        {
            entries = System.Array.Empty<ClaudePluginEntry>();
            // Translate the sentinels rather than leaking "-1"/"-2" (internal, not OS exit codes) to a
            // human. (agy panel round 2.)
            warning = r.Code switch
            {
                ProcessRunner.NotFound => "claude CLI not on PATH — did not check for a conflicting marketplace plugin.",
                ProcessRunner.TimedOut => "`claude plugin list --json` timed out — did not check for a conflicting marketplace plugin.",
                _ => $"`claude plugin list --json` exited {r.Code} — did not check for a conflicting marketplace plugin.",
            };
            return false;
        }
        entries = ClaudePluginInventory.Parse(r.Output);
        if (entries.Count == 0 && !LooksLikeEmptyList(r.Output))
        {
            warning = "could not read `claude plugin list --json` output — did not check for a conflicting marketplace plugin.";
            return false;
        }
        warning = null;
        return true;
    }

    // Genuinely empty ONLY when the output parses to an empty JSON array. A non-empty array that
    // yielded zero entries means the schema drifted (e.g. a renamed `id` field) — that must surface as
    // a warning, NOT be read as "no collisions" and silently skipped. A bare StartsWith("[") could not
    // tell those apart. Whitespace variants like "[ ]" parse to Count 0, so they are handled too.
    // (agy panel round 2 — mechanism corrected: extra fields do NOT break Parse; a renamed field does.)
    private static bool LooksLikeEmptyList(string output)
    {
        try { return JsonNode.Parse(output) is JsonArray { Count: 0 }; }
        catch { return false; }
    }

    private static bool Same(ClaudePluginEntry p, DisabledEntry e) =>
        CollisionMarker.SameEntry(new DisabledEntry(p.Id, p.Scope, p.ProjectPath), e);

    private static string Where(DisabledEntry e) => e.ProjectPath is null ? $"scope {e.Scope}" : $"scope {e.Scope} in {e.ProjectPath}";

    // The internal -1/-2 sentinels (ProcessRunner.NotFound/TimedOut) are NOT OS exit codes; translate
    // them to plain words rather than leak them to a human, matching TryReadInventory's mapping.
    private static string DescribeExit(int code) => code switch
    {
        ProcessRunner.NotFound => "could not be run",
        ProcessRunner.TimedOut => "timed out",
        _ => $"exited {code}",
    };
}
