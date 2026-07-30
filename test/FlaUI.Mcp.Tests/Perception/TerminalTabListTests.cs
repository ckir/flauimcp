using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>desktop_list_terminal_tabs exists so an agent can learn a tabIndex WITHOUT selecting a tab.
/// The index space must match desktop_read_terminal_tab's, which it does by construction: both go through
/// TerminalTabReader.EnumerateTabs. A snapshot-derived ordinal could NOT be used, because five filters sit
/// between the raw child array and an emitted node and four of them can drop a TabItem
/// (SnapshotEngine.cs:62, :65, :66-70, :95-98).
///
/// HEADLESS here: this pins the projection shape only. The live WT read is Desktop-gated in Step 5.</summary>
public class TerminalTabListTests
{
    [Fact]
    public void The_listing_record_carries_index_title_and_reported_selection()
    {
        var t = new TerminalTabReader.TabListing(2, "pwsh", true);

        Assert.Equal(2, t.Index);
        Assert.Equal("pwsh", t.Title);
        Assert.True(t.Active);
    }

    /// <summary>Pins the WIRE shape of desktop_list_terminal_tabs' anonymous projection (ContentTools.cs,
    /// DesktopListTerminalTabs). MECHANISM NOTE: this cannot reach the tool's projection by invoking the
    /// tool method the way ToolProjectionShapeTests does, because the tool's live path
    /// (PerceptionManager.ListTerminalTabsAsync -> WindowManager.RunWithWindowAndDesktopAsync ->
    /// TerminalTabReader.List) needs a real window AutomationElement and so is Desktop-gated — that path is
    /// instead exercised by TerminalTabListDesktopTests below. So this follows ListWindowsProjectionShapeTests'
    /// mechanism: build the exact production TabListing values and run the SAME anonymous-object shape the
    /// tool emits (copied verbatim from ContentTools.cs) through the REAL ToolResponse.Ok serializer — that
    /// still exercises the real record type and the real serializer settings (naming, null-omission), which
    /// is the part that can silently drift.</summary>
    [Fact]
    public void The_tabs_projection_carries_index_title_active_and_emits_a_negative_one_activeTabIndex()
    {
        var tabs = new[]
        {
            new TerminalTabReader.TabListing(0, "pwsh", false),
            new TerminalTabReader.TabListing(1, "cmd", false),
        };
        int activeTabIndex = -1; // the documented "no tab reported itself selected" sentinel
                                  // (TerminalTabReader.cs:107) — a default-value-omitting serializer setting
                                  // would silently drop this, unlike a normal >=0 index, so it is the case
                                  // that matters to pin.

        string json = ToolResponse.Ok(new
        {
            tabs = tabs.Select(t => new { index = t.Index, title = t.Title, active = t.Active }),
            activeTabIndex,
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("activeTabIndex", out var activeEl));
        Assert.Equal(JsonValueKind.Number, activeEl.ValueKind);
        Assert.Equal(-1, activeEl.GetInt32());

        var tabsEl = root.GetProperty("tabs");
        Assert.Equal(JsonValueKind.Array, tabsEl.ValueKind);
        Assert.Equal(2, tabsEl.GetArrayLength());

        var t0 = tabsEl[0];
        Assert.Equal(0, t0.GetProperty("index").GetInt32());
        Assert.Equal("pwsh", t0.GetProperty("title").GetString());
        Assert.False(t0.GetProperty("active").GetBoolean());

        var t1 = tabsEl[1];
        Assert.Equal(1, t1.GetProperty("index").GetInt32());
        Assert.Equal("cmd", t1.GetProperty("title").GetString());
        Assert.False(t1.GetProperty("active").GetBoolean());
    }
}

// CONSOLE-MACHINE-ONLY: launches a REAL, brand-new Windows Terminal window (via `-w -1`, forcing a new
// window rather than attaching to any window the human already has open) with two tabs, and calls
// TerminalTabReader.List directly against it — bypassing ContentTools/PerceptionManager entirely (Task 3
// wires those; this pins the Core-level read only, which is all that exists at this point in the plan).
// Fixture mirrors TerminalTabE2ETests: unique per-run title marker so discovery can never match an
// ambient/pre-existing WT window, and a bounded poll for both window and result.
//
// Separate class (not extra facts in the headless class above): a class-level [Trait("Category","Desktop")]
// on the headless class would exclude its own headless fact from the headless gate, and a shared fixture
// would launch a real process under a headless filter.
[Trait("Category", "Desktop")]
public class TerminalTabListDesktopTests
{
    [SkippableFact]
    public async Task List_reports_every_tab_ascending_and_the_originally_active_index_without_selecting()
    {
        var marker = "FlaUiList" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var titleA = marker + "A";
        var titleB = marker + "B";

        Process? proc = null;
        try
        {
            // -w -1 forces a BRAND NEW window (never attaches to an existing WT session). First command is
            // the implicit tab 0 (titleA); `new-tab` opens+activates tab 1 (titleB) on top of it — so the
            // ACTIVE tab going into the call is index 1, not 0 (the case that would be indistinguishable
            // from "nothing reported active" if List got the guard wrong).
            var args = $"-w -1 --title {titleA} -d . cmd.exe /k \"echo {titleA}\" ; " +
                       $"new-tab --title {titleB} -d . cmd.exe /k \"echo {titleB}\"";
            proc = Process.Start(new ProcessStartInfo("wt.exe") { Arguments = args, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Skip.If(true, $"could not launch wt.exe: {ex.Message}");
        }

        using var dispatcher = new AutomationDispatcher();
        using var windows = new WindowManager(dispatcher);
        var perception = new PerceptionManager(windows, new RefRegistry(), new SnapshotCache());

        // Poll for our own new WindowsTerminal window by its unique active-tab title (titleB, since the
        // most-recently-created tab is the one WT activates and reflects in the window title).
        WindowHandle? handle = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && handle is null)
        {
            var list = await windows.ListWindowsAsync(includeBounds: false, includeHandles: true);
            var hit = list.FirstOrDefault(w =>
                string.Equals(w.ProcessName, "WindowsTerminal", StringComparison.Ordinal) &&
                w.Title.Contains(titleB, StringComparison.Ordinal));
            if (hit?.Handle is { } h) handle = new WindowHandle(h);
            else await Task.Delay(250);
        }
        Skip.IfNot(handle is not null, "no new WindowsTerminal window with our unique title appeared within 15s "
            + "(wt.exe not installed/registered, or the packaged app failed to activate)");

        var win = handle!.Value;
        try
        {
            // Retargeted at the production path (Task 3): the shipped tool goes through
            // PerceptionManager.ListTerminalTabsAsync (the QUERY STA), not RunOnWindowActionAsync (the
            // ACTION STA) — this exercises the same hop desktop_list_terminal_tabs actually uses.
            var (tabs, activeIndex) = await perception.ListTerminalTabsAsync(win);

            Assert.Equal(2, tabs.Count);
            for (int i = 0; i < tabs.Count; i++) Assert.Equal(i, tabs[i].Index);

            // NECESSARY-BUT-NOT-SUFFICIENT: this would still pass if List selected every tab and restored
            // the original, which is precisely what Run does. It only proves the REPORTED active index and
            // titles are right, not that List never touched selection. The real no-Select guarantee is
            // structural (List's docstring in TerminalTabReader.cs) plus review — a test cannot observe
            // "no event fired" at runtime, since UIA event callbacks arrive on COM RPC threads.
            Assert.Equal(1, activeIndex);
            Assert.True(tabs[1].Active);
            Assert.False(tabs[0].Active);
            Assert.Equal(titleA, tabs[0].Title);
            Assert.Equal(titleB, tabs[1].Title);

            // INDEPENDENT confirmation that nothing moved: the window's caption still reflects titleB (the
            // tab WT activated on launch) after the call. If List had switched tabs, WT's caption would show
            // titleA instead — this is the same caption-reflects-active-tab signal TerminalTabE2ETests uses
            // to independently verify Run's restore.
            var post = await windows.ListWindowsAsync(includeBounds: false, includeHandles: true);
            var self = post.FirstOrDefault(w => w.Handle == win.Id);
            Assert.True(self?.Title.Contains(titleB, StringComparison.Ordinal) == true
                && self?.Title.Contains(titleA, StringComparison.Ordinal) != true,
                "window caption changed after List() — a tab may have been selected");
        }
        finally
        {
            // Close only the window we minted (never the shared WT host process — closing IT would risk
            // taking down unrelated windows under WT's single-process-multi-window model).
            try { await windows.CloseAsync(win); } catch { /* best-effort */ }
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { /* the wt.exe stub is usually already gone */ }
        }
    }
}
