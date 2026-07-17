using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>One plugin entry that WE disabled, and everything needed to put it back.</summary>
/// <param name="ProjectPath">Null for user scope. For any other scope this is the directory the
/// enable must RUN from — `claude plugin enable` cannot target another project by flag.</param>
public sealed record DisabledEntry(string Id, string Scope, string? ProjectPath);

/// <summary>How <see cref="CollisionMarker.ReadState"/> classified the marker file.
/// Absent = no file. Corrupt = present but structurally unreadable (fail-safe: collapse to empty).
/// FutureVersion = written by a newer build (version &gt; 1) — leave it untouched. Present = valid v1.</summary>
internal enum MarkerState { Absent, Corrupt, FutureVersion, Present }

/// <summary>
/// The durable record of which plugin entries THIS product disabled, so uninstall can put back
/// exactly those and nothing else. Without it we cannot tell "we disabled it" from "the user
/// disabled it", and re-enabling the latter would override a deliberate user choice.
///
/// WHERE IT LIVES, and why not the obvious places:
///   - not `{app}`: Inno deletes it on BOTH uninstall branches (installer/flaui-mcp.iss:25).
///   - not `<data-dir>`: `--purge-data` deletes it (CliRouter.cs:110-114), and `--purge-data` is NOT
///     agent-scoped (CliRouter.cs:18) — so `uninstall --agent agy --purge-data` would destroy the
///     CLAUDE marker while Claude is still installed, stranding the user's plugin disabled forever.
///   - so: `<state-dir>`, outside both blast radii.
///
/// WRITE-ONCE, PER ENTRY (spec R1): only the run that performs a given entry's enabled->disabled
/// transition records THAT entry; later runs leave existing entries untouched. Per-entry rather than
/// per-file, because a later run may legitimately disable a NEW entry (e.g. the user installed the
/// marketplace copy in a second project) which must also be restored.
///
/// Never throws.
/// </summary>
public static class CollisionMarker
{
    public const string FileName = "disabled-plugins.json";

    public static string PathIn(string stateDir) => Path.Combine(stateDir, FileName);

    /// <summary>Two entries are the same entry when they name the same plugin in the same place.
    /// Paths and scopes are compared case-INSENSITIVELY: ProjectPath is a Windows path, so `C:\Proj`
    /// and `c:\proj` are one directory, and default record equality (ordinal, case-sensitive) would
    /// treat them as two. Getting this wrong duplicates entries in the marker and — worse — makes the
    /// "did we already record this?" check miss, so a re-install would wrongly conclude the USER
    /// disabled it. Restore's presence check uses the same comparison, deliberately.</summary>
    public static bool SameEntry(DisabledEntry a, DisabledEntry b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Scope, b.Scope, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ProjectPath ?? "", b.ProjectPath ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Size above which a corrupt marker is treated as non-marker garbage and discarded
    /// (overwritten) rather than backed up. A real marker is a few small JSON objects.</summary>
    public const long BackupSizeCap = 1024 * 1024;   // 1 MB

    /// <summary>Merge these entries in, preserving any already recorded, and write atomically.
    /// Returns null on success (INCLUDING recovery from a Corrupt file — it DID record, so the caller's
    /// re-enable promise must still fire). Returns a non-null reason only when the record did NOT
    /// happen: a genuine write failure, or a refusal to touch a FutureVersion marker.</summary>
    public static string? Record(string stateDir, IReadOnlyList<DisabledEntry> entries)
    {
        if (entries is null || entries.Count == 0) return null;   // never write an empty marker (a no-op restore); null-safe to keep the class's "Never throws" contract

        var (state, existing) = ReadState(stateDir);
        if (state == MarkerState.FutureVersion)
            return $"the restore record at {PathIn(stateDir)} was written by a newer flaui-mcp and was " +
                   "left unchanged; this install's disable was NOT recorded. If you did not expect this, " +
                   $"remove {PathIn(stateDir)}.";

        try
        {
            IReadOnlyList<DisabledEntry> baseline;
            if (state == MarkerState.Corrupt)
            {
                BackUpCorrupt(stateDir);                       // best-effort: preserve forensic bytes
                baseline = System.Array.Empty<DisabledEntry>();
            }
            else
            {
                baseline = state == MarkerState.Present
                    ? DedupBySameEntry(existing)
                    : System.Array.Empty<DisabledEntry>();     // Absent
            }

            var merged = baseline.ToList();
            foreach (var e in entries)
                if (!merged.Any(m => SameEntry(m, e))) merged.Add(e);

            WriteAtomically(stateDir, BuildJson(merged));
            return null;                                        // recorded (incl. Corrupt recovery)
        }
        catch (Exception e)
        {
            // NOT swallowed. We have already disabled the user's plugin; if the record does not survive,
            // uninstall can never put it back. The caller turns this into a Warning the user can see.
            return $"disabled a conflicting plugin but could NOT record it at {PathIn(stateDir)} ({e.Message}) — " +
                   "uninstalling flaui-mcp will not re-enable it automatically.";
        }
    }

    private static JsonObject BuildJson(IReadOnlyList<DisabledEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
            arr.Add(new JsonObject { ["id"] = e.Id, ["scope"] = e.Scope, ["projectPath"] = e.ProjectPath });
        return new JsonObject { ["version"] = 1, ["disabled"] = arr };
    }

    // Follows JsoncFile.Save's tmp -> File.Move pattern (the repo's atomic-write convention), but with
    // NO happy-path backup: the merged marker is a superset of the old, so an atomic replace loses
    // nothing and leaves no .bak litter in the state dir.
    private static void WriteAtomically(string stateDir, JsonObject root)
    {
        Directory.CreateDirectory(stateDir);
        var path = PathIn(stateDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);   // atomic directory-entry replace on the same volume
    }

    private static IReadOnlyList<DisabledEntry> DedupBySameEntry(IReadOnlyList<DisabledEntry> entries)
    {
        var result = new List<DisabledEntry>();
        foreach (var e in entries)
            if (!result.Any(m => SameEntry(m, e))) result.Add(e);   // SameEntry, NOT record .Distinct()
        return result;
    }

    // Move a corrupt marker aside to a collision-free .bak so its bytes survive for forensics, unless it
    // is absurdly large (a sign of non-marker garbage — discarded by the coming overwrite instead).
    // Best-effort: a backup hiccup must not fail an otherwise-recoverable record.
    private static void BackUpCorrupt(string stateDir)
    {
        try
        {
            var path = PathIn(stateDir);
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > BackupSizeCap) return;   // oversized garbage: leave it, overwrite discards it
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var dest = $"{path}.bak-{stamp}";
            for (var n = 0; File.Exists(dest); n++) dest = $"{path}.bak-{stamp}-{n}";   // same-ms collision-free
            File.Move(path, dest);
        }
        catch { /* best-effort: a failed backup must not fail a recoverable record */ }
    }

    /// <summary>Classify the marker and project its entries. The single source of truth Read, Record,
    /// Restore, and status all branch on. NEVER throws — every failure collapses to Corrupt.</summary>
    internal static (MarkerState State, IReadOnlyList<DisabledEntry> Entries) ReadState(string stateDir)
    {
        var empty = (IReadOnlyList<DisabledEntry>)System.Array.Empty<DisabledEntry>();
        try
        {
            var path = PathIn(stateDir);
            if (!File.Exists(path)) return (MarkerState.Absent, empty);
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return (MarkerState.Corrupt, empty);

            // version must be a NUMBER >= 1. Read as double so a large integer or a fractional future
            // version (2.1) neither overflows nor throws into Corrupt.
            if (o["version"] is not JsonValue vNode || vNode.GetValueKind() != JsonValueKind.Number)
                return (MarkerState.Corrupt, empty);
            var version = vNode.GetValue<double>();
            if (version < 1) return (MarkerState.Corrupt, empty);
            if (version > 1) return (MarkerState.FutureVersion, empty);   // honored on version alone (goal 4)

            if (o["disabled"] is not JsonArray arr) return (MarkerState.Corrupt, empty);

            var list = new List<DisabledEntry>();
            foreach (var node in arr)
            {
                if (node is not JsonObject e) continue;
                // A bare (string?)e["id"] cast THROWS on a numeric/boolean node (it does NOT return
                // null), so every field is gated on GetValueKind == String; a wrong-typed field drops
                // THIS entry only, never the whole file.
                var id = AsString(e["id"]);
                var scope = AsString(e["scope"]);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(scope)) continue;

                // projectPath may be legitimately absent or JSON null (user scope). A present-but-
                // wrong-typed projectPath drops the entry.
                var ppNode = e["projectPath"];
                string? projectPath;
                if (ppNode is null) projectPath = null;                     // absent OR JSON null
                else { projectPath = AsString(ppNode); if (projectPath is null) continue; }

                list.Add(new DisabledEntry(id!, scope!, projectPath));
            }
            return (MarkerState.Present, list);
        }
        catch { return (MarkerState.Corrupt, empty); }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Entries we recorded. Empty for every non-Present state (Absent/Corrupt/FutureVersion) —
    /// the fail-safe direction: an unreadable or future marker must mean "restore nothing", never
    /// "enable things we have no record of". Never throws.</summary>
    public static IReadOnlyList<DisabledEntry> Read(string stateDir)
    {
        var (state, entries) = ReadState(stateDir);
        return state == MarkerState.Present ? entries : System.Array.Empty<DisabledEntry>();
    }

    /// <summary>Consume the marker (spec R7). Returns null on success, else the reason.</summary>
    public static string? Delete(string stateDir)
    {
        try
        {
            var path = PathIn(stateDir);
            if (File.Exists(path)) File.Delete(path);
            return null;
        }
        catch (Exception e)
        {
            return $"could not remove the plugin-restore marker at {PathIn(stateDir)}: {e.Message}";
        }
    }
}
