using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
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

    // DELETED here, deliberately: a headless fact that serialized a LOCALLY RE-DECLARED copy of the tool's
    // anonymous projection and asserted activeTabIndex:-1 survived on the wire. The AGY-CAPSTONE Mechanism
    // Gamer seat called it vacuous and was right — it would have passed with desktop_list_terminal_tabs
    // deleted outright, because it never touched the tool. Its stated purpose does not survive scrutiny
    // either: ToolResponse's serializer is `new() { WriteIndented = false }` (ToolResponse.cs:12) with NO
    // DefaultIgnoreCondition, and the only setting that omits anything, WhenWritingDefault, drops
    // default(int) == 0 — never -1. So it guarded a behaviour no realistic configuration change could break.
    // An honest disclaimer had been added to its docstring, which was the wrong fix: a test whose NAME
    // claims it pins a projection is a trap even when its comment admits otherwise, because a reader sees
    // the name and a maintainer sees a passing count.
    // The wire shape IS pinned, through the real tool, by TerminalTabListDesktopTests below.
}

// CONSOLE-MACHINE-ONLY: launches a REAL, brand-new Windows Terminal window (via `-w -1`, forcing a new
// window rather than attaching to any window the human already has open) with two tabs, and drives the
// PRODUCTION path end to end — ContentTools.DesktopListTerminalTabs (the actual desktop_list_terminal_tabs
// MCP tool), which goes through PerceptionManager.ListTerminalTabsAsync (the query STA) into
// TerminalTabReader.List. This pins BOTH the structural behaviour (every tab reported ascending, the
// originally-active index, no selection touched) AND the tool's WIRE shape — the JSON that
// DesktopListTerminalTabs' anonymous projection actually emits. The wire-shape half could NOT be pinned
// headlessly: TerminalTabListTests' headless fact (above) re-declares the same anonymous shape locally to
// cheaply pin the -1-is-emitted serializer behaviour, but a re-declared shape cannot catch a field rename
// in ContentTools.cs (e.g. active -> isActive) — only calling the real tool method and parsing its real
// JSON, as this test does, can. Fixture mirrors TerminalTabE2ETests: unique per-run title marker so
// discovery can never match an ambient/pre-existing WT window, and a bounded poll for both window and
// result.
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
        // Collect any window a PREVIOUS run leaked (interrupted suite, killed test host, timeout) so
        // leftovers cannot accumulate across runs — the accumulation the maintainer actually reported.
        // Only ever touches windows carrying this suite's own marker prefix.
        await WtTestWindow.SweepStaleAsync(windows);
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
        // Retargeted at the production path (review finding on Task 3, commit d23b8a2): the shipped tool is
        // ContentTools.DesktopListTerminalTabs (desktop_list_terminal_tabs) itself, not the
        // PerceptionManager.ListTerminalTabsAsync hop it wraps — calling only the manager hop pinned the
        // structural record but not the tool's anonymous WIRE projection, so a field rename in
        // ContentTools.cs (e.g. active -> isActive) could have passed unnoticed. Matches
        // TerminalTabE2ETests.cs:48's ServerOptions(ReadOnly: false, AllowElevation: false) construction —
        // the sibling Desktop test that also constructs ContentTools to drive a terminal-tab tool.
        var tools = new ContentTools(perception, windows, new ServerOptions(ReadOnly: false, AllowElevation: false));
        try
        {
            string json = await tools.DesktopListTerminalTabs(window: win.Id);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tabsEl = root.GetProperty("tabs");
            Assert.Equal(JsonValueKind.Array, tabsEl.ValueKind);
            Assert.Equal(2, tabsEl.GetArrayLength());

            // Every element carries index/title/active by those EXACT lowercase names, and index ascends
            // from 0 with no duplicates — the wire-contract half a re-declared-shape headless test cannot
            // catch (see the headless fact's docstring above).
            for (int i = 0; i < tabsEl.GetArrayLength(); i++)
            {
                var t = tabsEl[i];
                Assert.True(t.TryGetProperty("index", out var idxEl), $"tabs[{i}] is missing \"index\"");
                Assert.Equal(i, idxEl.GetInt32());
                Assert.True(t.TryGetProperty("title", out _), $"tabs[{i}] is missing \"title\"");
                Assert.True(t.TryGetProperty("active", out _), $"tabs[{i}] is missing \"active\"");
            }

            Assert.True(root.TryGetProperty("activeTabIndex", out var activeEl), "response is missing \"activeTabIndex\"");
            int activeIndex = activeEl.GetInt32();

            // NECESSARY-BUT-NOT-SUFFICIENT: this would still pass if List selected every tab and restored
            // the original, which is precisely what Run does. It only proves the REPORTED active index and
            // titles are right, not that List never touched selection. The real no-Select guarantee is
            // structural (List's docstring in TerminalTabReader.cs) plus review — a test cannot observe
            // "no event fired" at runtime, since UIA event callbacks arrive on COM RPC threads.
            Assert.Equal(1, activeIndex);
            Assert.True(tabsEl[1].GetProperty("active").GetBoolean());
            Assert.False(tabsEl[0].GetProperty("active").GetBoolean());
            // CONTAINS, not Equals — and that is the contract, not a weakened assertion. A WT tab title is
            // set by the guest program, so it carries whatever the shell put there: the live run showed
            // "FlaUiList<guid>B - echo  FlaUiList<guid>B", i.e. the launcher title PLUS the running command.
            // This is exactly the rule the tool description and the driving skill both state — "a tab title
            // names the launcher, not the program running in it, so treat every title as a HINT". An
            // Assert.Equal here would be asserting a guarantee the contract explicitly disclaims, and it
            // failed on the first real run for precisely that reason. What matters is that each tab's title
            // carries ITS OWN marker and not the other tab's, which is what pins the index<->tab mapping.
            string title0 = tabsEl[0].GetProperty("title").GetString() ?? "";
            string title1 = tabsEl[1].GetProperty("title").GetString() ?? "";
            Assert.Contains(titleA, title0, StringComparison.Ordinal);
            Assert.DoesNotContain(titleB, title0, StringComparison.Ordinal);
            Assert.Contains(titleB, title1, StringComparison.Ordinal);
            Assert.DoesNotContain(titleA, title1, StringComparison.Ordinal);

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
            await WtTestWindow.CloseAsync(windows, win, marker); // best-effort ONLY — never throws from a finally
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { /* the wt.exe stub is usually already gone */ }
        }

        // OUTSIDE the try/finally, deliberately. Throwing from a `finally` REPLACES any exception already in
        // flight, so asserting there would mask the real assertion failure the test was reporting (and would
        // skip the proc.Kill above). Reached only when the body succeeded — which is exactly when a leaked
        // window is the interesting news. (AGY-CAPSTONE r7 Cascade Analyst.)
        await WtTestWindow.AssertNoLeakAsync(windows, marker);
    }
}
