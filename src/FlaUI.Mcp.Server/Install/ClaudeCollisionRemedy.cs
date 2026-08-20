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
    private readonly string? _claudeConfigDir;
    private readonly Func<string, string[], string?, RunResult> _longRun;

    /// <param name="dirExists">Injected for testability; defaults to <see cref="Directory.Exists"/>.
    /// A non-user entry whose project directory is gone cannot load the plugin, and `disable` would
    /// have nowhere valid to run from — such entries are skipped.</param>
    /// <param name="claudeConfigDir">Where `plugins/known_marketplaces.json` lives, so a disable can
    /// RECORD the source it came from and a restore can compare it against the live one. Null means
    /// "no registry" — every read then reads as FileAbsent, which is the correct answer for a caller
    /// that has no config dir to offer.</param>
    /// <param name="longRun">Runner for the ONE call that touches the network (`marketplace add` does a
    /// git clone). Defaults to <paramref name="run"/>, which keeps every existing caller and test
    /// unchanged; CliRouter passes a runner with a longer per-call bound.</param>
    public ClaudeCollisionRemedy(Func<string, string[], string?, RunResult> run, string stateDir,
        Func<string, bool>? dirExists = null, string? claudeConfigDir = null,
        Func<string, string[], string?, RunResult>? longRun = null)
    {
        _run = run;
        _stateDir = stateDir;
        _dirExists = dirExists ?? Directory.Exists;
        _claudeConfigDir = claudeConfigDir;
        _longRun = longRun ?? run;
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

        // Read the marketplace registry ONCE per pass, not per entry: it is the same file for every
        // entry and re-reading it inside the loop would multiply the failure surface for no gain.
        var marketplaces = KnownMarketplaces.Read(_claudeConfigDir);

        foreach (var e in matching)
        {
            var entry = new DisabledEntry(e.Id, e.Scope, e.ProjectPath, SourceFor(e.Id, marketplaces));

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
        {
            // Only PROMISE a restore if we actually recorded it. If Record failed, recordWarning
            // already tells the user it will NOT be re-enabled — promising the opposite in the same
            // concatenated line is worse than saying nothing. (agy panel round 2.)
            //
            // ⚠ RULE 1, D1: the promise must not overstate what a two-layer restore can do. We can only
            // REINSTALL an evicted copy for entries whose marketplace source we actually captured; for
            // the rest the honest promise is a re-enable. The whole defect began as a message that
            // promised more than the code delivered, and a richer restore makes that easier to repeat.
            var restorable = justDisabled.Count(d => d.Marketplace is not null);
            warnings.Insert(0, recordWarning is not null
                ? $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill."
                : restorable == justDisabled.Count
                    ? $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill " +
                      "(they will be re-enabled if you uninstall flaui-mcp, and reinstalled first if anything " +
                      "has removed them)."
                    : $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill " +
                      "(they will be re-enabled if you uninstall flaui-mcp).");
        }

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
                // ⚠ THIS MESSAGE IS LOAD-BEARING, and the v2 marker bump is what made it so. Before that
                // it was nearly unreachable; now any user who downgrades hits it during UNINSTALL — the
                // worst moment, because they have just removed the tool and their plugin is still
                // disabled. Explaining WHAT happened without saying what to DO is the same failure class
                // as the warning that started this defect: the tool describing its own state instead of
                // the user's problem. So name the ids and the exact command for each.
                return $"the restore record at {CollisionMarker.PathIn(_stateDir)} was written by a newer " +
                       // ⚠ "any plugin(s) we disabled", NOT "the plugin(s) below": this prefix is
                       // concatenated with a recourse that legitimately lists NOTHING in two of its three
                       // forms (nothing readable in the record, or every recorded project gone). Promising
                       // a list and then not producing one is the same defect class that started this
                       // whole subproject — a message claiming more than the code delivers.
                       "flaui-mcp, so it was left in place and not acted on — any plugin(s) we disabled are still " +
                       "DISABLED. " + ManualEnableRecourse(recorded) +
                       " Reinstalling the newer flaui-mcp and uninstalling it again would also do this.";
            if (state == MarkerState.Corrupt)
                return $"the restore record at {CollisionMarker.PathIn(_stateDir)} is unreadable, so any " +
                       "conflicting plugin(s) we disabled may still be disabled and could not be re-enabled " +
                       "automatically. To check: run `claude plugin list`, and re-enable " +
                       $"{MarketplaceId} wherever it is disabled.";
            if (recorded.Count == 0)
            {
                // Present, but every entry was dropped as malformed (only reachable via an external edit —
                // Record never writes such a marker). Nothing to restore, and it is useless litter that
                // would survive every future uninstall, so consume it. REPORT a Delete failure (e.g. a
                // transient lock) rather than swallowing it — same convention as the R7 consume below, so
                // the failure reaches the install log instead of vanishing. (SweepBackups in finally only
                // touches .bak-*/.tmp, never the marker itself, so it is NOT a net for this delete.)
                return CollisionMarker.Delete(_stateDir);   // null on success; the failure reason otherwise
            }

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
                var recourse = ManualEnableRecourse(recorded);
                return $"{listWarning} Your conflicting plugin(s) are still disabled and were NOT re-enabled. " +
                       "The record is kept at " + CollisionMarker.PathIn(_stateDir) + ". " + recourse;
            }

            var present = ClaudePluginInventory.Matching(entries, MarketplaceId);

            // Read ONCE per pass. Every entry consults the same file, and re-reading it per entry would
            // multiply the failure surface of a read that runs inside uninstall for no gain.
            var marketplaces = KnownMarketplaces.Read(_claudeConfigDir);

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
                    // LAYER (b): something evicted the copy. Rebuild it from the marketplace we recorded
                    // rather than reporting a dead end — but only what we RECORDED, never a guess.
                    if (!TryReinstall(e, marketplaces, warnings)) continue;
                    // Reinstalled: fall through to the enable below, which is what actually undoes our
                    // disable. `install` does not imply the entry is enabled at our recorded scope.
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

    /// <summary>The marketplace an installed plugin came from, or null when we cannot know it.
    ///
    /// A plugin id is `&lt;plugin&gt;@&lt;marketplace-alias&gt;`, so the alias is DERIVED from the id rather
    /// than assumed. Null when the id has no alias segment, when the registry could not be read, or when
    /// the alias's source kind is one we do not recognise. ⚠ NEVER GUESSED: an entry with no source
    /// degrades to re-enable-only, and Apply's promise narrows to match (rule 1). Guessing would let a
    /// restore install something the user did not ask for.</summary>
    private static MarketplaceSource? SourceFor(string id, MarketplacesSnapshot marketplaces)
    {
        var at = id.LastIndexOf('@');
        if (at < 0 || at == id.Length - 1) return null;
        var alias = id[(at + 1)..];
        return marketplaces.ByName.TryGetValue(alias, out var src) ? src : null;
    }

    /// <summary>Rebuild an evicted copy from its recorded marketplace: re-add the marketplace if the
    /// alias is absent, then reinstall. Returns true when the caller should go on to ENABLE it, false
    /// when this entry is done (a warning has been recorded either way).
    ///
    /// ⚠ EVERY false return is a `continue` at the call site, NEVER a `return`/`break`. One entry's
    /// problem is never another entry's — reading "stop" as `return` would strand every LATER entry
    /// because one alias mismatched.
    ///
    /// ⚠ NOTHING HERE MAY THROW. It runs inside `uninstall`, which Inno invokes with
    /// `waituntilterminated` (installer/flaui-mcp.iss:44-47). KnownMarketplaces.Read never throws and
    /// every CLI call goes through a bounded runner, so a hang or a hostile file becomes a warning.</summary>
    private bool TryReinstall(DisabledEntry e, MarketplacesSnapshot marketplaces, List<string> warnings)
    {
        var manual = $"claude plugin install {e.Id} --scope {e.Scope}" +
                     (e.ProjectPath is null ? "" : $" (run it from {e.ProjectPath})");

        if (e.Marketplace is not { } mkt)
        {
            // ⚠ AN HONEST DEAD END, not a graceful degrade. The plugin is GONE and this path cannot
            // bring it back, so the message must read as one. Guessing a source would install something
            // the user did not ask for, which is strictly worse. The honest failure is the feature.
            warnings.Add($"{e.Id} ({Where(e)}) is no longer installed and no marketplace source was " +
                         "recorded for it, so it could NOT be restored. To put it back yourself, re-add " +
                         $"the marketplace it came from and run: {manual}.");
            return false;
        }

        if (marketplaces.State == MarketplacesState.Unreadable)
        {
            // "Cannot read" is NOT "not there". The live source is genuinely UNKNOWN, and re-adding
            // blind could overwrite a marketplace the user has repointed.
            warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, and the marketplace registry " +
                         $"({KnownMarketplaces.FileName}) could not be read, so we did not risk changing " +
                         $"it. To restore it yourself: claude plugin marketplace add {mkt.Source} then {manual}.");
            return false;
        }

        // FileAbsent means there are NO registered marketplaces, so the alias is DEFINITIVELY absent and
        // re-adding is both safe and necessary. Only a successfully READ file can say an alias is live.
        var live = marketplaces.State == MarketplacesState.Read
                   && marketplaces.ByName.TryGetValue(mkt.Name, out var l) ? l : null;

        if (live is not null && !KnownMarketplaces.SameSource(live, mkt))
        {
            // ⚠ "PRESENT BY ALIAS" IS NOT "IS THE THING WE RECORDED". The alias is a local name the USER
            // controls. If they repointed it, installing would silently deliver whatever now sits behind
            // that name. The user's own configuration outranks our restore — for THIS entry.
            warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, and the marketplace `{mkt.Name}` " +
                         $"now points at {live.Kind} `{live.Source}` instead of the {mkt.Kind} " +
                         $"`{mkt.Source}` we recorded — so it was NOT reinstalled and your marketplace " +
                         $"was NOT changed. To restore it yourself once `{mkt.Name}` points where you " +
                         $"expect: {manual}.");
            return false;
        }

        if (live is null)
        {
            var add = _longRun("claude", new[] { "plugin", "marketplace", "add", mkt.Source }, e.ProjectPath);
            if (add.Code != 0)
            {
                warnings.Add($"could not re-add the marketplace `{mkt.Name}` for {e.Id} ({Where(e)}): " +
                             $"claude {DescribeExit(add.Code)}. To restore it yourself: claude plugin " +
                             $"marketplace add {mkt.Source} then {manual}.");
                return false;
            }
        }

        var reinstall = _run("claude", new[] { "plugin", "install", e.Id, "--scope", e.Scope }, e.ProjectPath);
        if (reinstall.Code != 0)
        {
            // ⚠ RULE 3: a PARTIAL restore reports as a FAILURE with the remaining commands, never as a
            // success. We may have just re-added a marketplace and left the plugin missing.
            warnings.Add((live is null ? $"re-added the marketplace `{mkt.Name}` but " : "") +
                         $"could NOT reinstall {e.Id} ({Where(e)}): claude {DescribeExit(reinstall.Code)}. " +
                         $"To finish: {manual}.");
            return false;
        }

        // ⚠ RULE 2: say WHICH steps ran. "Restored" when only an enable was needed and "reinstalled from
        // <marketplace>" when the copy was rebuilt are different facts, and an operator debugging a lost
        // plugin needs to know which happened.
        warnings.Add($"{e.Id} ({Where(e)}) was no longer installed, so it was reinstalled from " +
                     $"`{mkt.Name}` ({mkt.Kind}: {mkt.Source})" +
                     (live is null ? ", after re-adding that marketplace." : "."));
        return true;
    }

    /// <summary>The exact commands a human must run to undo our disable themselves.
    ///
    /// Entries whose project directory is gone are OMITTED, not listed with an impossible "run it from
    /// &lt;deleted path&gt;" — the same bad-recourse defect the in-loop guard fixes. An empty result says so
    /// rather than printing an empty list. Shared by the FutureVersion path and the
    /// inventory-unreadable path so the two recourses can never drift.</summary>
    private string ManualEnableRecourse(IReadOnlyList<DisabledEntry> entries)
    {
        // ⚠ TWO DIFFERENT EMPTIES, and conflating them makes the tool blame the USER for its own limit.
        // An empty INPUT means we could not read any entries at all — a newer marker whose entry shape
        // this build does not understand, which ParseEntries drops one by one. An empty RECOVERABLE set
        // means we DID read entries and every one of their projects is gone. Only the second is the
        // user's disk. Saying "the recorded projects no longer exist" for the first is a confident lie
        // about a directory we never even had a path for.
        //
        // Reachable ONLY from the FutureVersion branch: the Present path returns early when
        // recorded.Count == 0, so no other caller can pass an empty list here.
        if (entries.Count == 0)
            return "We could not read any entries from that record, so there is nothing to list here — " +
                   "run `claude plugin list` and re-enable whatever is still disabled.";

        var recoverable = entries.Where(e => e.ProjectPath is null || _dirExists(e.ProjectPath)).ToList();
        // ⚠ "NEEDED", not "possible". Reaching here means every recorded entry is a non-user scope whose
        // project directory is gone — and this class's own guards state why that is not a loss: a plugin
        // whose project is deleted cannot load, so the collision is MOOT (see the Restore loop's guard,
        // and Apply's symmetric one). "No manual action is possible" implies helplessness about something
        // that still matters; it does not matter, and saying so is the honest and calmer answer.
        if (recoverable.Count == 0)
            return "No manual action is needed for those: the recorded projects no longer exist, so " +
                   "those copies cannot load anyway.";
        return "To re-enable manually: " + string.Join("; ", recoverable.Select(e =>
            $"claude plugin enable {e.Id} --scope {e.Scope}" +
            (e.ProjectPath is null ? "" : $" (run from {e.ProjectPath})"))) + ".";
    }

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
