// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class PopupGraftingTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PopupGraftingTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task A_context_menu_opened_by_right_click_is_grafted_under_Popups()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // Same coordinate right-click hazard as ROADMAP item 16; ContextMenuArrange owns the fix and waits
        // for the menu instead of sleeping a fixed 400ms.
        await ContextMenuArrange.OpenAsync(mgr, handle);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        Assert.Contains("[Active Overlays]", snap.Tree);
        Assert.Contains("aid=MenuAlpha", snap.Tree);
        // Dedup: the menu item must appear EXACTLY once. The WPF context menu is a window-child
        // (CT=Window cls=Popup), so without pruning it from the main walk it would appear both in
        // the main tree and under [Active Overlays] — two refs for one element.
        Assert.Equal(1, snap.Tree.Split('\n').Count(l => l.Contains("aid=MenuAlpha")));
    }
}
