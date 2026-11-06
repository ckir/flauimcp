// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class MenuLabelTests
{
    [Theory]
    [InlineData("&Alpha", "Alpha")]
    [InlineData("E&xit", "Exit")]
    [InlineData("Save && Exit", "Save & Exit")]
    [InlineData("Beta\tCtrl+B", "Beta")]
    [InlineData("&Open\tCtrl+O", "Open")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", "")]
    [InlineData("&&", "&")]
    [InlineData("trailing&", "trailing")]
    public void Clean_strips_accelerators_shortcut_text_and_whitespace(string raw, string expected)
        => Assert.Equal(expected, MenuLabel.Clean(raw));

    [Fact]
    public void Clean_of_null_is_empty() => Assert.Equal("", MenuLabel.Clean(null));
}
