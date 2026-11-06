// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Tray;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayMenuClassifierTests
{
    private static CandidateFacts Facts(string cls = "WindowsForms10.Window.20808", bool hasTitle = false, int pid = 42,
        bool isForeground = false, bool hasHmenu = false, bool uiaRootIsMenu = false, bool actionableItems = false)
        => new(Hwnd: 0x1000, ClassName: cls, HasTitle: hasTitle, Pid: pid, IsForeground: isForeground,
               HasHmenu: hasHmenu, UiaRootIsMenu: uiaRootIsMenu, HasActionableMenuItems: actionableItems);

    [Fact] // M13 ZoomIt
    public void Classic_32768_with_hmenu_is_K3()
        => Assert.Equal(MenuKind.ClassicMenu, TrayMenuClassifier.Classify(Facts(cls: "#32768", hasHmenu: true), foregroundPid: 42));

    [Fact] // M6 ShareX
    public void Untitled_window_whose_uia_root_is_Menu_is_K1()
        => Assert.Equal(MenuKind.UiaMenu, TrayMenuClassifier.Classify(Facts(uiaRootIsMenu: true), 42));

    [Fact] // panel r2: WPF/Qt popup host rooted as Pane with invokable MenuItems
    public void Untitled_window_with_actionable_menu_items_is_K1()
        => Assert.Equal(MenuKind.UiaMenu, TrayMenuClassifier.Classify(Facts(actionableItems: true), 42));

    [Fact] // M7 OneDrive: titled, contains MenuItems (Value only) — must NOT be K1
    public void Titled_foreground_window_is_K2_even_with_menu_items()
        => Assert.Equal(MenuKind.AppWindow, TrayMenuClassifier.Classify(Facts(hasTitle: true, isForeground: true, actionableItems: true), 42));

    [Fact]
    public void Titled_window_that_is_not_foreground_is_not_a_candidate()
        => Assert.Equal(MenuKind.None, TrayMenuClassifier.Classify(Facts(hasTitle: true, isForeground: false), 42));

    [Fact] // F6
    public void Window_of_another_process_than_the_foreground_is_never_a_candidate()
        => Assert.Equal(MenuKind.None, TrayMenuClassifier.Classify(Facts(cls: "#32768", hasHmenu: true, pid: 7), foregroundPid: 42));

    [Fact]
    public void Untitled_window_with_no_menu_evidence_is_none()
        => Assert.Equal(MenuKind.None, TrayMenuClassifier.Classify(Facts(), 42));

    [Theory]
    [InlineData("Xaml_WindowedPopupClass")]
    [InlineData("SysShadow")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    public void Explorer_tooltip_shadow_and_overflow_windows_are_ignored(string cls)
        => Assert.True(TrayMenuClassifier.IsIgnoredClass(cls));

    [Fact]
    public void The_intent_overlay_sentinel_is_ignored()
        => Assert.True(TrayMenuClassifier.IsIgnoredClass(FlaUI.Mcp.Core.Interaction.OverlaySentinel.ClassName));

    [Fact]
    public void Strongest_prefers_K3_then_K1_then_K2()
    {
        var picked = TrayMenuClassifier.Strongest(new[]
        {
            new ClassifiedWindow(0x1, MenuKind.AppWindow, 0),
            new ClassifiedWindow(0x2, MenuKind.UiaMenu, 5),
            new ClassifiedWindow(0x3, MenuKind.ClassicMenu, 4),
        });
        Assert.Equal((nint)0x3, picked!.Value.Hwnd);
    }

    [Fact]
    public void Strongest_of_nothing_is_null()
        => Assert.Null(TrayMenuClassifier.Strongest(Array.Empty<ClassifiedWindow>()));

    [Fact] // F1: two consecutive polls, same non-empty set, same item counts
    public void Picture_is_stable_only_when_non_empty_and_identical_to_the_previous_poll()
    {
        var a = new[] { new ClassifiedWindow(0x2, MenuKind.UiaMenu, 5) };
        var b = new[] { new ClassifiedWindow(0x2, MenuKind.UiaMenu, 5) };
        var c = new[] { new ClassifiedWindow(0x2, MenuKind.UiaMenu, 6) };
        Assert.True(TrayMenuClassifier.IsStable(a, b));
        Assert.False(TrayMenuClassifier.IsStable(a, c));
        Assert.False(TrayMenuClassifier.IsStable(Array.Empty<ClassifiedWindow>(), Array.Empty<ClassifiedWindow>()));
        Assert.False(TrayMenuClassifier.IsStable(null, a));
    }
}
