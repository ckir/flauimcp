// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayManagerResolveTests
{
    private static TrayIconInfo Icon(string tooltip, bool redacted = false, string loc = "visible")
        => new(tooltip, redacted, loc, "1.2.3", 10, 10, false, 0x1);

    [Fact]
    public void Blank_icon_query_is_InvalidArguments()
    {
        var ex = Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("ShareX") }, "  "));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void No_match_is_SelectorNoMatch()
        => Assert.Equal(ToolErrorCode.SelectorNoMatch, Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("ShareX") }, "vpn")).Code);

    [Fact]
    public void Two_matches_is_AmbiguousMatch_and_the_message_lists_both_names()
    {
        var ex = Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("Docker Desktop"), Icon("Docker helper") }, "docker"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Contains("Docker Desktop", ex.Message);
        Assert.Contains("Docker helper", ex.Message);
    }

    [Fact]
    public void A_redacted_icon_is_never_matched()
        => Assert.Equal(ToolErrorCode.SelectorNoMatch, Assert.Throws<ToolException>(() => TrayManager.ResolveIcon(new[] { Icon("Bitwarden", redacted: true) }, "bitwarden")).Code);

    [Fact]
    public void Unique_match_returns_the_icon()
        => Assert.Equal("hidden", TrayManager.ResolveIcon(new[] { Icon("ShareX"), Icon("ZoomIt", loc: "hidden") }, "zoom").Location);
}
