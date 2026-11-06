// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class ClassicMenuReaderTests
{
    [Fact]
    public void FromRaw_keeps_raw_positions_and_drops_separators_from_the_returned_list()
    {
        var raw = new[]
        {
            new RawMenuEntry(0, "&Alpha", TrayWin32.MF_BYPOSITION),
            new RawMenuEntry(1, "Disabled Item", TrayWin32.MF_GRAYED),
            new RawMenuEntry(2, "More", TrayWin32.MF_POPUP),
            new RawMenuEntry(3, "", TrayWin32.MF_SEPARATOR),
            new RawMenuEntry(4, "Beta\tCtrl+B", 0),
        };
        var items = ClassicMenuReader.FromRaw(raw);
        Assert.Equal(new[] { "Alpha", "Disabled Item", "More", "Beta" }, items.Select(i => i.Text));
        Assert.Equal(new[] { 0, 1, 2, 4 }, items.Select(i => i.RawPosition));
        Assert.Equal(new[] { true, false, true, true }, items.Select(i => i.Enabled));
        Assert.Equal(new[] { false, false, true, false }, items.Select(i => i.HasSubmenu));
    }

    [Fact]
    public void FromRaw_treats_MF_DISABLED_as_disabled_too()
        => Assert.False(ClassicMenuReader.FromRaw(new[] { new RawMenuEntry(0, "x", TrayWin32.MF_DISABLED) }).Single().Enabled);
}
