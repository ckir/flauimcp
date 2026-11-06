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
public class TrayDesktopTests
{
    /// <summary>xunit builds one instance per test: normalise a leftover-open overflow first.</summary>
    public TrayDesktopTests() => TrayTestHygiene.EnsureOverflowClosed();

    /// <summary>Everything a tray test needs, built the way Program.cs builds it (real Win32 env, real sink,
    /// file lease provider — the read tools and left_click never consult the lease).</summary>
    private sealed class Rig : IDisposable
    {
        public AutomationDispatcher Dispatcher { get; } = new();
        public WindowManager Windows { get; }
        public TrayManager Tray { get; }
        public TrayTools Tools { get; }
        public Rig()
        {
            Windows = new WindowManager(Dispatcher);
            var env = new Win32PlatformEnvironment();
            var guard = new InputGuard(new Win32SyntheticInput(env), env, new FileLeaseProvider(), new ActionBudget(),
                new InputAudit(TextWriter.Null), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, false, false);
            Tray = new TrayManager(Windows, Dispatcher, guard, env, SensitivityClassifier.OsOnly);
            Tools = new TrayTools(Tray, new ServerOptions(ReadOnly: false, AllowElevation: false));
        }
        public void Dispose() { Windows.Dispose(); Dispatcher.Dispose(); }
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    /// <summary>F10: a hidden-path test must PROVE it is on the hidden path. Fails naming Q5 otherwise.</summary>
    private static void RequireHidden(JsonElement list, string tooltip)
    {
        var icon = list.GetProperty("icons").EnumerateArray().Single(i => i.GetProperty("name").GetString() == tooltip);
        Assert.True(icon.GetProperty("location").GetString() == "hidden",
            $"the fixture icon is '{icon.GetProperty("location")}', not 'hidden' — see SPEC §8 Q5; this test would not exercise the hidden path");
    }

    [Fact]
    public void Fixture_starts_logs_and_exits_gracefully()
    {
        string log;
        using (var fx = new TrayFixtureHost("uia")) { log = fx.LogPath; Assert.Contains(fx.Events(), e => e.StartsWith("started kind=uia")); }
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task List_without_hidden_shows_system_icons_and_never_the_overflow()
    {
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopListTrayIcons());
        Assert.Contains(j.GetProperty("icons").EnumerateArray(), i => i.GetProperty("location").GetString() == "system");
        Assert.False(j.TryGetProperty("overflowRestored", out _));
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [Fact]
    public async Task List_with_hidden_contains_the_fixture_icon_with_its_exact_tooltip_and_restores_a_closed_overflow()
    {
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        Assert.False(TrayLocator.IsOverflowOpen());
        var j = Json(await rig.Tools.DesktopListTrayIcons(includeHidden: true));
        RequireHidden(j, fx.Tooltip);
        Assert.True(j.GetProperty("overflowRestored").GetBoolean());
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [Fact]
    public async Task List_with_hidden_leaves_an_already_open_overflow_open()
    {
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        var opened = Json(await rig.Tools.DesktopOpenTray(includeHidden: true));
        Assert.True(opened.GetProperty("overflowLeftOpen").GetBoolean());
        try
        {
            var j = Json(await rig.Tools.DesktopListTrayIcons(includeHidden: true));
            RequireHidden(j, fx.Tooltip);
            Assert.True(j.GetProperty("overflowRestored").GetBoolean());
            Assert.True(TrayLocator.IsOverflowOpen());
        }
        finally { await rig.Tray.ListIconsAsync(false); await new TrayLocator(rig.Windows, rig.Dispatcher).CloseOverflowAsync(TrayLocator.RequireTaskbar().Hwnd); }
    }

    [Fact]
    public async Task Open_tray_handles_work_with_find_and_the_overflow_handle_goes_stale_once_closed()
    {
        using var rig = new Rig();
        var perception = new PerceptionManager(rig.Windows, new RefRegistry(), new SnapshotCache());
        var j = Json(await rig.Tools.DesktopOpenTray(includeHidden: true));
        var tray = new WindowHandle(j.GetProperty("handle").GetString()!);
        var found = await perception.FindAsync(tray, new FindQuery(TrayIconReader.SystemItemAid, null, "eq", null, false), 50, null);
        Assert.NotEmpty(found.Matches);
        Assert.True(j.TryGetProperty("overflowHandle", out var oh), "the fixture or other apps keep at least one icon in the overflow on this machine; if none are hidden the test cannot exercise the overflow handle");
        var overflow = new WindowHandle(oh.GetString()!);
        Assert.NotEmpty((await perception.FindAsync(overflow, new FindQuery(TrayIconReader.NotifyItemAid, null, "eq", null, false), 50, null)).Matches);
        await new TrayLocator(rig.Windows, rig.Dispatcher).CloseOverflowAsync(TrayLocator.RequireTaskbar().Hwnd);
        rig.Windows.PruneClosedWindows();
        var ex = await Assert.ThrowsAsync<FlaUI.Mcp.Core.Errors.ToolException>(() => perception.FindAsync(overflow, new FindQuery(TrayIconReader.NotifyItemAid, null, "eq", null, false), 50, null));
        Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.WindowHandleStale, ex.Code);
    }

    [Fact]
    public async Task Tray_handle_refuses_close_and_transform()
    {
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopOpenTray());
        var handle = j.GetProperty("handle").GetString()!;
        var windowTools = new WindowTools(rig.Windows, new ServerOptions(false, false), new Win32PlatformEnvironment());
        Assert.Contains("ElementNotActionable", await windowTools.DesktopCloseWindow(handle));
        var interaction = new InteractionTools(new PerceptionManager(rig.Windows, new RefRegistry(), new SnapshotCache()), rig.Windows, new ServerOptions(false, false));
        Assert.Contains("ElementNotActionable", await interaction.DesktopWindowTransform(handle, "minimize"));
        Assert.True(TrayWin32.IsWindow(TrayLocator.RequireTaskbar().Hwnd)); // still there
    }

    [Fact]
    public async Task Left_click_reaches_the_fixture_and_restores_the_overflow()
    {
        using var fx = new TrayFixtureHost("uia");
        using var rig = new Rig();
        var j = Json(await rig.Tools.DesktopTrayAction(fx.Tooltip, "left_click"));
        Assert.Equal(fx.Tooltip, j.GetProperty("matchedIcon").GetString());
        Assert.True(fx.WaitForEvent("left-click"), $"fixture log: {string.Join(" / ", fx.Events())}");
        Assert.True(j.GetProperty("overflowRestored").GetBoolean());
        Assert.False(TrayLocator.IsOverflowOpen());
    }

    [Fact]
    public async Task Ambiguous_icon_lists_both_fixture_names()
    {
        using var a = new TrayFixtureHost("uia", "FlaUI Tray Twin");
        using var b = new TrayFixtureHost("uia", "FlaUI Tray Twin");
        using var rig = new Rig();
        var json = await rig.Tools.DesktopTrayAction("FlaUI Tray Twin", "left_click");
        Assert.Contains("AmbiguousMatch", json);
        Assert.Contains(a.Tooltip, json);
        Assert.Contains(b.Tooltip, json);
    }

    [Fact]
    public async Task Unknown_icon_is_SelectorNoMatch()
    {
        using var rig = new Rig();
        Assert.Contains("SelectorNoMatch", await rig.Tools.DesktopTrayAction("no such tray icon 9f3a", "left_click"));
    }
}
