using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// CONSOLE-MACHINE-ONLY / DESTRUCTIVE: launches a REAL, brand-new Windows Terminal window (via `-w -1`,
// forcing a new window rather than attaching to any window the human already has open) and drives it —
// selecting tabs, reading buffers, restoring focus. Tab titles are seeded with a random per-run marker so
// discovery can NEVER match an ambient/pre-existing WT window (spec §7.11: local acceptance for the
// highest-risk Destructive tool). Skips cleanly (does not fail) if `wt.exe` is missing or the new window
// cannot be found within the poll bound — a flaky/unconfigured Desktop-category environment must not turn
// into a red local run for a developer who just doesn't have Windows Terminal installed.
[Trait("Category", "Desktop")]
public class TerminalTabE2ETests
{
    [SkippableFact]
    public async Task Read_a_background_tab_sequential_calls_and_out_of_range_all_behave()
    {
        var marker = "FlaUiE2E" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var titleA = marker + "A";
        var titleB = marker + "B";

        Process? proc = null;
        try
        {
            // -w -1 forces a BRAND NEW window (never attaches to an existing WT session). First command is
            // the implicit tab 0 (titleA); `new-tab` opens+activates tab 1 (titleB) on top of it.
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
        var tools = new ContentTools(perception, windows, new ServerOptions(ReadOnly: false, AllowElevation: false));

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
            // The originally-active tab is titleB (WT activates the most-recently-created tab, and the WT
            // window title reflects the ACTIVE tab). Capture that up front so we can independently confirm
            // the restore actually reverted the window — not just trust the tool's self-report.
            var originallyActiveTitle = titleB;

            // (a) Reading the NON-active tab (index 0 = titleA) returns its buffer tail and restores the
            // originally-active tab (index 1 = titleB) with high confidence (titles are unique here).
            var jsonA = await tools.DesktopReadTerminalTab(win.Id, tabIndex: 0, restoreFocus: true, fromEnd: true, maxLength: 10000, timeoutMs: 8000);
            Assert.DoesNotContain("\"error\"", jsonA);
            Assert.Contains(titleA, jsonA);
            Assert.Contains("\"restored\":true", jsonA);
            Assert.Contains($"\"tabTitle\":\"{titleA}\"", jsonA);

            // INDEPENDENT restore verification (do NOT trust only the self-reported restored:true): the WT
            // window title reflects the active tab, so a fresh ListWindowsAsync must show the ORIGINALLY-
            // active tab's title (titleB) back — proving the restore Select actually took effect. Poll
            // because WT repaints its caption ASYNCHRONOUSLY after the switch. Runs BEFORE step (b)
            // re-selects any tab. Bound matches the 15s discovery poll above: a caption settle after a
            // tab-select is strictly easier than the initial-launch settle, so 15s is a comfortable margin
            // that keeps this hard Assert from flaking on the async-repaint tail under load (Desktop/CI).
            bool reverted = false;
            var restoreDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < restoreDeadline && !reverted)
            {
                var post = await windows.ListWindowsAsync(includeBounds: false, includeHandles: true);
                var self = post.FirstOrDefault(w => w.Handle == win.Id);
                if (self?.Title is { } t && t.Contains(originallyActiveTitle, StringComparison.Ordinal)
                    && !t.Contains(titleA, StringComparison.Ordinal))
                    reverted = true;
                else await Task.Delay(250);
            }
            Assert.True(reverted, $"restore did not revert the window to the originally-active tab '{originallyActiveTitle}'");

            // (b) A second, sequential call off the same window (index 1 = titleB, already active after the
            // restore above) also succeeds and reads titleB's own buffer.
            var jsonB = await tools.DesktopReadTerminalTab(win.Id, tabIndex: 1, restoreFocus: true, fromEnd: true, maxLength: 10000, timeoutMs: 8000);
            Assert.DoesNotContain("\"error\"", jsonB);
            Assert.Contains(titleB, jsonB);
            Assert.Contains($"\"tabTitle\":\"{titleB}\"", jsonB);

            // (c) An out-of-range tabIndex errors WITHOUT switching: the tool surfaces InvalidArguments, and
            // titleB (the tab left active by (b)) is still readable as the active tab afterward.
            var jsonBad = await tools.DesktopReadTerminalTab(win.Id, tabIndex: 99, restoreFocus: true, fromEnd: true, maxLength: 10000, timeoutMs: 8000);
            Assert.Contains("\"error\":\"InvalidArguments\"", jsonBad);

            var jsonStillB = await tools.DesktopReadTerminalTab(win.Id, tabIndex: 1, restoreFocus: true, fromEnd: true, maxLength: 10000, timeoutMs: 8000);
            Assert.DoesNotContain("\"error\"", jsonStillB);
            Assert.Contains(titleB, jsonStillB);
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
