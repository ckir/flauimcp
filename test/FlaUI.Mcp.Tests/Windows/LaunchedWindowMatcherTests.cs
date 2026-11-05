// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

public class LaunchedWindowMatcherTests
{
    [Theory]
    [InlineData("notepad", "Notepad")]                      // launched "notepad.exe" → process "Notepad" (case-insensitive)
    [InlineData("FlaUI.Mcp.TestApp", "FlaUI.Mcp.TestApp")]  // exact match
    public void Matches_window_owned_by_the_launched_app(string expected, string windowProcessName)
        => Assert.True(LaunchedWindowMatcher.IsExpectedApp(expected, windowProcessName));

    [Theory]
    [InlineData("notepad", "explorer")]  // unrelated window that popped during launch — the race we close
    [InlineData("notepad", "")]          // candidate has no resolvable process name
    [InlineData("", "Notepad")]          // no expected name → never match
    public void Does_not_match_other_windows(string expected, string windowProcessName)
        => Assert.False(LaunchedWindowMatcher.IsExpectedApp(expected, windowProcessName));
}
