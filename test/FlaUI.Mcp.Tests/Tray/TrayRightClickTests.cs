// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Text.Json;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Tray;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

[Trait("Category", "Desktop")]
[Trait("Category", "SyntheticInput")]
public class TrayRightClickTests
{
    private static bool InputLocked()
    {
        var lease = new FileLeaseProvider().Read(out _);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        return lease is null || !lease.IsValidNow(DateTime.UtcNow, sid);
    }
    private const string NoLease = "no active input lease — grant one on the console with `flaui-mcp unlock`";

    private sealed class Rig : IDisposable
    {
        public AutomationDispatcher Dispatcher { get; } = new();
        public WindowManager Windows { get; }
        public TrayTools Tools { get; }
        public Rig()
        {
            Windows = new WindowManager(Dispatcher);
            var env = new Win32PlatformEnvironment();
            var guard = new InputGuard(new Win32SyntheticInput(env), env, new FileLeaseProvider(), new ActionBudget(),
                new InputAudit(TextWriter.Null), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, false, false);
            Tools = new TrayTools(new TrayManager(Windows, Dispatcher, guard, env, SensitivityClassifier.OsOnly),
                new ServerOptions(ReadOnly: false, AllowElevation: false));
        }
        public void Dispose() { Windows.Dispose(); Dispatcher.Dispose(); }
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    private static string[] Texts(JsonElement j) => j.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()!).ToArray();
    private static readonly string[] ExpectedItems = { "Alpha", "Disabled Item", "More", "Beta", "Exit" }; // separator dropped, accelerators/shortcut cleaned

    [SkippableTheory]
    [InlineData("uia")]
    [InlineData("classic")]
    public async Task Right_click_lists_the_fixture_menu_and_dismisses_it(string kind)
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost(kind);
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click"));
        Assert.Equal(fx.Tooltip, j.GetProperty("matchedIcon").GetString());
        Assert.Equal("menu", j.GetProperty("menuKind").GetString());
        Assert.Equal(ExpectedItems, Texts(j));
        var items = j.GetProperty("items").EnumerateArray().ToArray();
        Assert.False(items[1].GetProperty("enabled").GetBoolean());
        Assert.True(items[2].GetProperty("hasSubmenu").GetBoolean());
        Assert.True(j.GetProperty("dismissed").GetBoolean());
        Assert.True(fx.WaitForEvent("menu closed"), string.Join(" / ", fx.Events()));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("command "));
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [SkippableTheory]
    [InlineData("uia")]
    [InlineData("classic")]
    public async Task Selecting_an_item_lands_in_the_fixture_log_and_the_menu_is_gone(string kind)
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost(kind);
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: "beta"));
        Assert.Equal("Beta", j.GetProperty("selected").GetString());
        Assert.True(j.GetProperty("dismissed").GetBoolean());
        Assert.True(fx.WaitForEvent("command Beta"), string.Join(" / ", fx.Events()));
        Assert.Equal(0, TrayWin32.FindVisibleByClass(TrayMenuClassifier.ClassicMenuClass));
    }

    [SkippableFact]
    public async Task Exact_item_text_wins_over_a_substring_hit()
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        // "Alpha" is exact; nothing else contains it, so this pins the equality-first rule end to end.
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: "ALPHA"));
        Assert.Equal("Alpha", j.GetProperty("selected").GetString());
        Assert.True(fx.WaitForEvent("command Alpha"));
    }

    [SkippableTheory]
    [InlineData("Disabled Item", "ElementNotActionable")]
    [InlineData("More", "ElementNotActionable")]
    [InlineData("Nope", "SelectorNoMatch")]
    public async Task Refused_items_dismiss_the_menu_and_send_nothing(string item, string code)
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost("classic");
        using var rig = new Rig();
        var json = await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: item);
        Assert.Contains(code, json);
        Assert.True(fx.WaitForEvent("menu closed"), string.Join(" / ", fx.Events()));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("command "));
        Assert.Equal(0, TrayWin32.FindVisibleByClass(TrayMenuClassifier.ClassicMenuClass));
    }

    [SkippableFact]
    public async Task K2_returns_a_window_handle_and_a_warning_when_menuItem_was_given()
    {
        Skip.If(InputLocked(), NoLease);
        using var fx = new TrayFixtureHost("window");
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click", menuItem: "Alpha"));
        Assert.Equal("window", j.GetProperty("menuKind").GetString());
        var handle = j.GetProperty("handle").GetString()!;
        Assert.Contains("nothing was selected", j.GetProperty("warning").GetString());
        Assert.False(j.TryGetProperty("dismissed", out _));
        Assert.True(fx.WaitForEvent("window opened"));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("command "));
        // the handle is usable: close the fixture window through it
        var windowTools = new WindowTools(rig.Windows, new ServerOptions(false, false), new Win32PlatformEnvironment());
        Assert.DoesNotContain("\"error\"", await windowTools.DesktopCloseWindow(handle));
        Assert.True(fx.WaitForEvent("window closed"));
    }

    [SkippableFact]
    public async Task Right_click_without_a_lease_is_InputNotLeased_and_touches_nothing()
    {
        // Runs ONLY when the lease is absent — the inverse gate of every other test here.
        Skip.If(!InputLocked(), "an input lease is active; this test needs the locked state");
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        Assert.Contains("InputNotLeased", await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click"));
        Assert.DoesNotContain(fx.Events(), e => e.StartsWith("menu "));
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [SkippableFact]
    public async Task Right_click_on_a_VISIBLE_icon_lists_the_menu()
    {
        Skip.If(InputLocked(), NoLease);
        const string KeyPath = @"Control Panel\NotifyIconSettings";
        Microsoft.Win32.RegistryKey? iconKey = null;
        string? keyName = null;
        object? original = null;
        TrayFixtureHost? fx = null;
        using var rig = new Rig();
        try
        {
            fx = new TrayFixtureHost("uia");
            var exe = fx.Process.MainModule?.FileName ?? "";
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath)
                ?? throw new Xunit.Sdk.XunitException("Q7: HKCU\\" + KeyPath + " does not exist, cannot promote the fixture icon");
            var candidates = new List<string>();
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("ExecutablePath") is string p && p.EndsWith("FlaUI.Mcp.TrayFixture.exe", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(sub);
            }
            foreach (var sub in candidates)
            {
                using var k = root.OpenSubKey(sub)!;
                if (string.Equals((string)k.GetValue("ExecutablePath")!, exe, StringComparison.OrdinalIgnoreCase)) { keyName = sub; break; }
            }
            keyName ??= candidates.Count == 1 ? candidates[0] : null;
            Assert.True(keyName is not null, $"Q7: no NotifyIconSettings key with ExecutablePath ending FlaUI.Mcp.TrayFixture.exe (candidates: {candidates.Count}, exe: {exe})");
            iconKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath + "\\" + keyName, writable: true)!;
            original = iconKey.GetValue("IsPromoted");
            iconKey.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);

            var visible = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !visible)
            {
                var list = Json(await rig.Tools.DesktopListTrayIcons(includeHidden: false));
                visible = list.GetProperty("icons").EnumerateArray().Any(i =>
                    i.GetProperty("name").GetString() == fx.Tooltip && i.GetProperty("location").GetString() == "visible");
                if (!visible) await Task.Delay(250);
            }
            Assert.True(visible, "Q7: IsPromoted=1 did not make the fixture icon location 'visible' within 5 s");

            var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "right_click"));
            Assert.Equal(fx.Tooltip, j.GetProperty("matchedIcon").GetString());
            Assert.Equal("menu", j.GetProperty("menuKind").GetString());
            Assert.Equal(ExpectedItems, Texts(j));
            var items = j.GetProperty("items").EnumerateArray().ToArray();
            Assert.False(items[1].GetProperty("enabled").GetBoolean());
            Assert.True(items[2].GetProperty("hasSubmenu").GetBoolean());
            Assert.True(j.GetProperty("dismissed").GetBoolean());
            Assert.True(fx.WaitForEvent("menu closed"), string.Join(" / ", fx.Events()));
            Assert.DoesNotContain(fx.Events(), e => e.StartsWith("command "));
            Assert.False(TrayLocator.IsOverflowOpen());
        }
        finally
        {
            // explorer re-creates IsPromoted=0 when the fixture's icon is dropped, so restore only AFTER exit.
            fx?.Dispose();
            if (iconKey is not null)
            {
                await Task.Delay(1500);
                if (original is null) iconKey.DeleteValue("IsPromoted", throwOnMissingValue: false);
                else iconKey.SetValue("IsPromoted", original, Microsoft.Win32.RegistryValueKind.DWord);
                iconKey.Dispose();
            }
        }
    }
}
