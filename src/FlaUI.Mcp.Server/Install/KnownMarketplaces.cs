using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>How a read of `known_marketplaces.json` turned out.
///
/// ⚠ FileAbsent and Unreadable are DIFFERENT ANSWERS WITH OPPOSITE ACTIONS, and collapsing them is a
/// defect. FileAbsent means there are no registered marketplaces at all, so a recorded alias is
/// DEFINITIVELY absent and re-adding it is both safe and necessary. Unreadable means the live source is
/// genuinely UNKNOWN, so re-adding blind could overwrite a marketplace the user has repointed.</summary>
public enum MarketplacesState { FileAbsent, Unreadable, Read }

/// <summary>What one read saw. <paramref name="UnsupportedKinds"/> exists so an unrecognised source kind
/// is VISIBLE rather than silently dropped — otherwise the next unsupported kind is invisible until a
/// restore fails weeks later.</summary>
public sealed record MarketplacesSnapshot(
    MarketplacesState State,
    IReadOnlyDictionary<string, MarketplaceSource> ByName,
    IReadOnlyList<string> UnsupportedKinds);

/// <summary>
/// Reads Claude Code's marketplace registry so a restore can compare what it RECORDED against what is
/// LIVE before touching anything.
///
/// MEASURED 2026-08-20 on the operator's machine — 10 marketplaces: 5 `directory`, 4 `github`, 1 `git`.
/// So `github` is the MINORITY kind and `directory` is what our own marketplace uses; a github-only
/// implementation would strand the majority case. `claude plugin marketplace add` takes "a URL, path, or
/// GitHub repo" (measured from its --help), so ONE recorded string serves all three.
///
/// ⚠⚠ NEVER THROWS — this runs inside `uninstall`, which the Windows uninstaller invokes with
/// `Flags: runhidden waituntilterminated` (installer/flaui-mcp.iss:44-47). A malformed, locked or
/// ACL-blocked file that threw would fail the uninstall and trap the user with software they cannot
/// remove. The codebase already states this discipline at CliRouter.cs:577 — "best-effort: the warning
/// channel itself must never throw and abort uninstall". A restore that cannot run is a warning; it is
/// never a failed uninstall.
///
/// ⚠ This is an UNDOCUMENTED internal format that upstream may change. That is accepted rather than
/// solved: the never-throws rule is precisely what makes a schema change degrade into "unreadable"
/// instead of crashing, and the smoke gate records `claude --version` so a green result stays evidence
/// about the CLI behaviour of a known day.
/// </summary>
public static class KnownMarketplaces
{
    public const string FileName = "known_marketplaces.json";

    public static string PathIn(string claudeConfigDir) =>
        Path.Combine(claudeConfigDir, "plugins", FileName);

    private static MarketplacesSnapshot Empty() => new(
        MarketplacesState.FileAbsent,
        new Dictionary<string, MarketplaceSource>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    private static MarketplacesSnapshot UnreadableSnapshot() => new(
        MarketplacesState.Unreadable,
        new Dictionary<string, MarketplaceSource>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    /// <summary>Never throws. A null or empty config dir reads as FileAbsent — there is no registry to
    /// consult, which is the same answer as "the file is not there".</summary>
    public static MarketplacesSnapshot Read(string? claudeConfigDir)
    {
        if (string.IsNullOrWhiteSpace(claudeConfigDir)) return Empty();

        var byName = new Dictionary<string, MarketplaceSource>(StringComparer.OrdinalIgnoreCase);
        var unsupported = new List<string>();
        try
        {
            var path = PathIn(claudeConfigDir);
            if (!File.Exists(path)) return Empty();
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return UnreadableSnapshot();

            foreach (var (name, node) in root)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (node is not JsonObject entry) continue;
                if (entry["source"] is not JsonObject src) continue;

                var kind = AsString(src["source"]);
                if (string.IsNullOrWhiteSpace(kind)) continue;

                var value = SourceValue(src, kind!);
                if (string.IsNullOrWhiteSpace(value))
                {
                    // Recorded as ABSENT, never guessed — but SURFACED, so the next unsupported kind is
                    // not invisible. A recognised kind with a missing value lands here too, which is
                    // correct: we still have nothing usable to re-add from.
                    unsupported.Add(kind!);
                    continue;
                }
                byName[name] = new MarketplaceSource(name, kind!, value!);
            }
            return new MarketplacesSnapshot(MarketplacesState.Read, byName, unsupported);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The file (or its directory) vanished AFTER the File.Exists check above — a concurrent
            // `claude` run rewriting its own registry, or the user's tooling. That is ABSENT, not
            // UNREADABLE, and the distinction is the whole point of having two states: absent means
            // there are no registered marketplaces, so a recorded alias is DEFINITIVELY absent and
            // re-adding it is safe and necessary; unreadable means the live source is UNKNOWN and we
            // must not touch it. Falling through to the bare catch below would abort a restore that
            // should have proceeded — the false-negative direction, which is the dangerous one.
            // CollisionMarker.ReadState guards the identical race on the marker file for the same reason.
            return Empty();
        }
        catch
        {
            // Locked, ACL-blocked, or a schema that made a cast throw. "I know nothing" is a usable
            // answer inside a hidden uninstaller; an exception is not.
            return UnreadableSnapshot();
        }
    }

    /// The kind-to-field mapping. MEASURED from the operator's own known_marketplaces.json.
    private static string? SourceValue(JsonObject src, string kind) => kind.ToLowerInvariant() switch
    {
        "github" => AsString(src["repo"]),
        "git" => AsString(src["url"]),
        "directory" => AsString(src["path"]),
        _ => null,
    };

    /// <summary>Do two records denote the same marketplace?
    ///
    /// ⚠ A `directory` source is a PATH, and naive string equality gets it WRONG: `C:\p` vs `c:\p`, `\`
    /// vs `/`, a trailing separator, or a redundant `.\` segment all denote one location while comparing
    /// unequal — and an unequal compare triggers the "source DIFFERS" branch, which ABORTS a restore that
    /// should have proceeded. That is the dangerous direction. `CollisionMarker.SameEntry` already faces
    /// this for projectPath and is the precedent followed here.
    ///
    /// `github` and `git` are IDENTIFIERS, not paths, and compare ordinally — running a URL through path
    /// normalisation would mangle its forward slashes.</summary>
    public static bool SameSource(MarketplaceSource a, MarketplaceSource b)
    {
        if (!string.Equals(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(a.Kind, "directory", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(NormalisePath(a.Source), NormalisePath(b.Source), StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical form of a directory source for comparison ONLY — never for launching anything.
    ///
    /// ⚠ A RELATIVE path is deliberately NOT resolved. `Path.GetFullPath` resolves against the CURRENT
    /// working directory, which during uninstall is Inno's, not the user's — so resolving one would
    /// invent a confident wrong answer instead of leaving an honest textual comparison. MEASURED: all
    /// five `directory` sources in the operator's registry are absolute, so this is not a live case.</summary>
    private static string NormalisePath(string p)
    {
        var t = p.Trim();
        if (t.Length == 0) return t;
        try
        {
            if (Path.IsPathRooted(t)) t = Path.GetFullPath(t);
        }
        catch { /* a hostile recorded value: fall through to the textual form */ }
        return t.Replace('/', '\\').TrimEnd('\\');
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
