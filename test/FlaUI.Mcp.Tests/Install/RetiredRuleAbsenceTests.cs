using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>A rule curate RETIRED as measurably wrong must not come back — not in the skill, and not in the
/// human-facing docs either. Both routes have now happened:
///
/// 1. GROWTH region (2026-09-09): a regeneration script skipped a retired bullet but appended its continuation
///    lines to the last GRADUATED entry; that entry was later reinstated wholesale. Three operations carried it
///    with every gate green, because Every_twinned_skill_has_byte_identical_copies proves the two copies MATCH,
///    never what they SAY — two identically-corrupted copies pass it.
/// 2. Docs (same day): the wake-hydrates claim was retired from the skill and sat untouched in agent-contract
///    and architecture-and-safety for six commits. Retiring a rule from the SKILL does not retire it from DOCS.
///
/// Matching is WHITESPACE-NORMALISED on both sides, which is not cosmetic: these files wrap at ~113 columns, so
/// a resurrected phrase is likely to straddle a newline. A plain Contains() misses that — and did: a hand grep
/// for one retired phrase reported "absent" while the phrase was present, split across two lines.
///
/// SCOPE is deliberately narrow. Only files that describe CURRENT behaviour are scanned. History and
/// defect-records legitimately quote superseded claims — docs/superpowers/** (plans and specs, ~95 files),
/// CHANGELOG.md, the capstone ledger, and docs/fix-the-tool-backlog/** (whose entries QUOTE the wrong rule to
/// explain what they supersede; the grid-cell entry provably does).
///
/// HEADLESS — reads tracked files only, so it runs in the default gate.</summary>
public class RetiredRuleAbsenceTests
{
    private const string LedgerRel = ".claude/flaui-mcp/retired-phrases.md";
    private const string Start = "<!-- AUTOTRAIN:GROWTH:START -->";
    private const string End = "<!-- AUTOTRAIN:GROWTH:END -->";

    private static readonly string[] SkillTwins =
    {
        ".claude/skills/driving-flaui-mcp/SKILL.md",
        "plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md",
    };

    /// <summary>Docs that state CURRENT behaviour. Deliberately excludes history and defect-records.</summary>
    private static readonly string[] LiveDocs =
    {
        "README.md",
        "CONTRIBUTING.md",
        "docs/agent-contract.md",
        "docs/architecture-and-safety.md",
        "docs/operator-manual.md",
        "docs/building.md",
    };

    /// <summary>A line carrying this marker is exempt — for the rare case a live doc must name a superseded
    /// claim to warn about it. Say WHY in the same line.</summary>
    private const string ExemptMarker = "retired-ok:";

    private static string Read(string rel) => File.ReadAllText(RepoPaths.At(rel.Split('/')));

    /// <summary>Collapse every whitespace run to one space, so a phrase split across wrapped lines still
    /// matches. Without this the check is defeated by ordinary re-wrapping.</summary>
    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static List<string> RetiredFragments()
    {
        var fragments = new List<string>();
        foreach (var line in Read(LedgerRel).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- \"", StringComparison.Ordinal)) continue;
            var open = trimmed.IndexOf('"');
            var close = trimmed.IndexOf('"', open + 1);
            if (close > open + 1) fragments.Add(Normalize(trimmed.Substring(open + 1, close - open - 1)));
        }
        return fragments;
    }

    /// <summary>Drops any line bearing the exemption marker, then normalises.</summary>
    private static string Scannable(string text) =>
        Normalize(string.Join("\n", text.Split('\n')
            .Where(l => !l.Contains(ExemptMarker, StringComparison.Ordinal))));

    private static string GrowthRegion(string rel)
    {
        var text = Read(rel);
        var i = text.IndexOf(Start, StringComparison.Ordinal);
        var j = text.IndexOf(End, StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i,
            $"{rel}: the AUTOTRAIN:GROWTH markers are missing or inverted — without them this test would " +
            "scan an empty string and pass vacuously.");
        return text.Substring(i + Start.Length, j - i - Start.Length);
    }

    /// <summary>Every haystack this suite guards: the two GROWTH regions, plus each live doc whole.</summary>
    private static IEnumerable<(string Label, string Text)> Haystacks()
    {
        foreach (var twin in SkillTwins)
            yield return ($"{twin} (GROWTH region)", Scannable(GrowthRegion(twin)));
        foreach (var doc in LiveDocs)
            yield return (doc, Scannable(Read(doc)));
    }

    /// <summary>Guards every way this suite could pass while proving nothing: an empty ledger, a missing
    /// marker pair, an empty region, or a doc path that no longer resolves.</summary>
    [Fact]
    public void The_ledger_and_every_scanned_file_are_present_and_non_empty()
    {
        Assert.NotEmpty(RetiredFragments());

        foreach (var doc in LiveDocs)
            Assert.True(File.Exists(RepoPaths.At(doc.Split('/'))),
                $"{doc} is scanned by this test but does not exist — fix the path or drop it from LiveDocs, " +
                "otherwise the scan silently covers less than it claims.");

        foreach (var (label, text) in Haystacks())
            Assert.False(string.IsNullOrWhiteSpace(text), $"{label} scanned as empty");
    }

    [Fact]
    public void No_retired_rule_has_reappeared_in_the_skill_or_the_live_docs()
    {
        var fragments = RetiredFragments();
        var resurrected = new List<string>();

        foreach (var (label, text) in Haystacks())
            resurrected.AddRange(
                fragments.Where(f => text.Contains(f, StringComparison.Ordinal))
                         .Select(f => $"{label}: \"{f}\""));

        Assert.True(resurrected.Count == 0,
            "a RETIRED rule is back — it was removed because measurement showed it WRONG, so it now contradicts "
            + "whatever replaced it. Remove it; if it has been re-validated, delete its line from " + LedgerRel
            + " in the same commit and say why; if a live doc must name it to warn about it, put \""
            + ExemptMarker + " <why>\" on that line. Found: " + string.Join(" | ", resurrected));
    }
}
