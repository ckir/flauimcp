// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class OverflowRestoreTests
{
    [Fact]
    public void Tool_opened_it_and_it_is_still_open_closes_and_reports_restored()
    {
        var p = OverflowRestore.Decide(wasOpenAtStart: false, openedByTool: true, isOpenNow: true);
        Assert.True(p.CloseNow);
        Assert.True(p.Restored);
    }

    [Fact]
    public void Tool_opened_it_and_the_app_already_closed_it_reports_restored_without_closing()
    {
        var p = OverflowRestore.Decide(false, true, isOpenNow: false);
        Assert.False(p.CloseNow);
        Assert.True(p.Restored);
    }

    [Fact]
    public void Found_open_and_still_open_leaves_it_and_reports_restored()
    {
        var p = OverflowRestore.Decide(wasOpenAtStart: true, openedByTool: false, isOpenNow: true);
        Assert.False(p.CloseNow);
        Assert.True(p.Restored);
    }

    [Fact] // F4: never re-open over the app the action just raised
    public void Found_open_and_the_action_closed_it_is_not_reopened_and_reports_not_restored()
    {
        var p = OverflowRestore.Decide(true, false, isOpenNow: false);
        Assert.False(p.CloseNow);
        Assert.False(p.Restored);
    }

    [Fact]
    public void Never_touched_reports_nothing()
    {
        var p = OverflowRestore.Decide(wasOpenAtStart: false, openedByTool: false, isOpenNow: false);
        Assert.False(p.CloseNow);
        Assert.Null(p.Restored);
    }
}
