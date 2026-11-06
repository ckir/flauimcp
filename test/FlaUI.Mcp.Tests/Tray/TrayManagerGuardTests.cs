// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Tray;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

/// <summary>Headless pins for the three guards the AGY-TEST-AUDIT found unpinned: the deny-list refusal,
/// the read-only block and the process-wide lock timeout.</summary>
public class TrayManagerGuardTests
{
    [Theory]
    [InlineData("keepassxc")]
    [InlineData("Bitwarden")]
    [InlineData(null)] // an unidentifiable process is denied too
    public void A_menu_of_a_denied_or_unidentifiable_process_is_refused(string? proc)
        => Assert.Equal(ToolErrorCode.TargetDenied, Assert.Throws<ToolException>(() => TrayManager.RefuseIfDenied(proc)).Code);

    [Theory]
    [InlineData("explorer")]
    [InlineData("keepassxc-helper-not-listed")] // distractor: near-miss of a denied name is NOT denied
    public void A_menu_of_an_ordinary_process_is_not_refused(string proc)
        => TrayManager.RefuseIfDenied(proc);

    [Theory]
    [InlineData("left_click")]
    [InlineData("right_click")]
    public async Task Tray_action_is_blocked_in_read_only_mode_before_any_dependency_is_touched(string action)
    {
        var tools = new TrayTools(tray: null!, new ServerOptions(ReadOnly: true, AllowElevation: false));
        var json = await tools.DesktopTrayAction("ShareX", action);
        Assert.Contains("WriteBlockedReadOnly", json);
    }

    [Fact]
    public async Task A_second_tray_call_times_out_while_the_first_holds_the_lock_and_succeeds_after_release()
    {
        var mgr = new TrayManager(windows: null!, dispatcher: null!, guard: null!, env: null!, classifier: null!);
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var first = mgr.WithLockAsync(5000, async () => { entered.SetResult(); await release.Task; return 1; });
        await entered.Task;

        var ex = await Assert.ThrowsAsync<ToolException>(() => mgr.WithLockAsync(50, () => Task.FromResult(2)));
        Assert.Equal(ToolErrorCode.Timeout, ex.Code);
        Assert.Contains("another tray operation is in progress", ex.Message);

        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(3, await mgr.WithLockAsync(50, () => Task.FromResult(3))); // lock was released
    }
}
