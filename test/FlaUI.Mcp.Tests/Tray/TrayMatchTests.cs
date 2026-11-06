// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayMatchTests
{
    private static readonly string[] Tooltips = { "Docker Desktop\nRunning", "OneDrive - Personal", "ShareX", "Docker Compose helper" };

    [Fact]
    public void Icon_substring_is_case_insensitive_and_unique()
    {
        var r = TrayMatch.IconBySubstring(Tooltips, "sharex");
        Assert.Equal(MatchKind.Unique, r.Kind);
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void Icon_substring_with_two_hits_is_ambiguous_and_lists_both()
    {
        var r = TrayMatch.IconBySubstring(Tooltips, "docker");
        Assert.Equal(MatchKind.Ambiguous, r.Kind);
        Assert.Equal(new[] { 0, 3 }, r.Candidates);
    }

    [Fact]
    public void Icon_substring_with_no_hit_is_none()
        => Assert.Equal(MatchKind.None, TrayMatch.IconBySubstring(Tooltips, "vpn").Kind);

    [Fact]
    public void Item_exact_match_wins_over_substring_matches()
    {
        var r = TrayMatch.ItemByText(new[] { "Quit", "Quit Docker Desktop", "Restart" }, "quit");
        Assert.Equal(MatchKind.Unique, r.Kind);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Item_falls_back_to_substring_when_nothing_is_exact()
    {
        var r = TrayMatch.ItemByText(new[] { "Open main window", "Exit" }, "main");
        Assert.Equal(MatchKind.Unique, r.Kind);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Item_two_substring_hits_and_no_exact_is_ambiguous()
    {
        var r = TrayMatch.ItemByText(new[] { "Pause sync", "Resume sync", "Exit" }, "sync");
        Assert.Equal(MatchKind.Ambiguous, r.Kind);
        Assert.Equal(new[] { 0, 1 }, r.Candidates);
    }

    [Fact]
    public void Item_two_identical_exact_texts_is_ambiguous()
        => Assert.Equal(MatchKind.Ambiguous, TrayMatch.ItemByText(new[] { "Open", "Open" }, "open").Kind);

    [Fact]
    public void Item_no_hit_is_none()
        => Assert.Equal(MatchKind.None, TrayMatch.ItemByText(new[] { "Exit" }, "quit").Kind);
}
