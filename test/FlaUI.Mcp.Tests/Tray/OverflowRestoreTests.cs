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

    [Theory]
    [InlineData(false, true, true, false)]  // opened by the tool and still open after the close attempt: NOT restored
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void Report_compares_the_real_end_state_with_the_start_state(bool wasOpen, bool openedByTool, bool isOpenAfter, bool expected)
        => Assert.Equal(expected, OverflowRestore.Report(wasOpen, openedByTool, isOpenAfter));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public void Report_is_null_when_the_tool_never_touched_it(bool wasOpen, bool openedByTool, bool isOpenAfter)
        => Assert.Null(OverflowRestore.Report(wasOpen, openedByTool, isOpenAfter));

    [Fact]
    public void Never_touched_reports_nothing()
    {
        var p = OverflowRestore.Decide(wasOpenAtStart: false, openedByTool: false, isOpenNow: false);
        Assert.False(p.CloseNow);
        Assert.Null(p.Restored);
    }
}
