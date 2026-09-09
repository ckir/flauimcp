using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>The GROWTH region's CONTENT was untested until 2026-09-09, and that gap shipped a defect: a rule
/// curate had RETIRED as wrong came back into both twins and sat there contradicting its own replacement.
/// A regeneration script skipped the retired bullet but kept appending its continuation lines to the last
/// GRADUATED entry; the corrupted entry was exported to graduation-candidates.md and reinstated wholesale when
/// the cap was raised. Three operations carried it with every gate green, because
/// SkillLoadLineTests.Every_twinned_skill_has_byte_identical_copies proves the two copies MATCH, never what
/// they SAY — two identically-corrupted copies pass it.
///
/// So this asserts ABSENCE, which is the only assertion that catches a resurrection: every fragment listed in
/// .claude/flaui-mcp/retired-phrases.md must be gone from BOTH twins' GROWTH regions.
///
/// HEADLESS by construction — it reads tracked files only, so it runs in the default gate.</summary>
public class RetiredRuleAbsenceTests
{
    private const string LedgerRel = ".claude/flaui-mcp/retired-phrases.md";
    private const string Start = "<!-- AUTOTRAIN:GROWTH:START -->";
    private const string End = "<!-- AUTOTRAIN:GROWTH:END -->";

    private static readonly string[] Twins =
    {
        ".claude/skills/driving-flaui-mcp/SKILL.md",
        "plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md",
    };

    private static string Read(string rel) => File.ReadAllText(RepoPaths.At(rel.Split('/')));

    /// <summary>The fragment is the text between the FIRST pair of double quotes on a "- " line, so the
    /// human-readable date and rationale after it are ignored.</summary>
    private static List<string> RetiredFragments()
    {
        var fragments = new List<string>();
        foreach (var line in Read(LedgerRel).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- \"", StringComparison.Ordinal)) continue;
            var open = trimmed.IndexOf('"');
            var close = trimmed.IndexOf('"', open + 1);
            if (close > open + 1) fragments.Add(trimmed.Substring(open + 1, close - open - 1));
        }
        return fragments;
    }

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

    /// <summary>Guards the two ways this suite could pass while proving nothing: an empty ledger (no fragments
    /// to look for) or an empty region (nowhere for them to hide).</summary>
    [Fact]
    public void The_ledger_and_both_growth_regions_are_non_empty()
    {
        Assert.NotEmpty(RetiredFragments());
        foreach (var twin in Twins)
            Assert.NotEmpty(GrowthRegion(twin).Trim());
    }

    [Fact]
    public void No_retired_rule_has_reappeared_in_either_growth_region()
    {
        var fragments = RetiredFragments();
        var resurrected = new List<string>();

        foreach (var twin in Twins)
        {
            var region = GrowthRegion(twin);
            resurrected.AddRange(
                fragments.Where(f => region.Contains(f, StringComparison.Ordinal))
                         .Select(f => $"{twin}: \"{f}\""));
        }

        Assert.True(resurrected.Count == 0,
            "a RETIRED rule is back in the GROWTH region — it was removed because measurement showed it WRONG, "
            + "so it is now contradicting whatever replaced it. Remove it, or if it has been re-validated, "
            + "delete its line from " + LedgerRel + " in the same commit and say why. Found: "
            + string.Join(" | ", resurrected));
    }
}
