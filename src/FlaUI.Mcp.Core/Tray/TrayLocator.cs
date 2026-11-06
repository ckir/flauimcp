// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.UIA3;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>Finds the taskbar, the overflow window and the chevron; opens/closes the overflow and CONFIRMS
/// the effect by window presence, never by the chevron's (localized, state-bearing) Name (SPEC "Finding
/// the chevron", M14).</summary>
public sealed class TrayLocator
{
    private const int OverflowSettleMs = 1500;
    private const int CloseConfirmMs = 150;
    private const int ReopenWatchMs = 500;
    private const int CloseAttempts = 2;
    private readonly WindowManager _windows;
    private readonly AutomationDispatcher _dispatcher;

    public TrayLocator(WindowManager windows, AutomationDispatcher dispatcher) { _windows = windows; _dispatcher = dispatcher; }

    public static (nint Hwnd, int Pid) RequireTaskbar()
    {
        var hwnd = TrayWin32.FindWindowW(TrayMenuClassifier.TaskbarClass, null);
        if (hwnd == 0)
            throw new ToolException(ToolErrorCode.WindowNotFound, "taskbar not available; explorer may be restarting.", "retry in a few seconds");
        return (hwnd, TrayWin32.PidOf(hwnd));
    }

    public static nint OverflowHwnd() => TrayWin32.FindVisibleByClass(TrayMenuClassifier.OverflowClass);
    public static bool IsOverflowOpen() => OverflowHwnd() != 0;

    public Task<string?> ChevronRuntimeIdAsync(nint tray)
        => _windows.RunOnHwndAsync(tray, TrayIconReader.FindChevronRuntimeId);

    /// <summary>Returns the overflow HWND. Opens it if closed; 0 when there is no chevron (nothing hidden).
    /// Throws ElementNotActionable when invoking the chevron did not make the overflow appear.</summary>
    public async Task<nint> OpenOverflowAsync(nint tray)
    {
        var open = OverflowHwnd();
        if (open != 0) return open;
        var rid = await ChevronRuntimeIdAsync(tray);
        if (rid is null) return 0;
        await InvokeChevronAsync(tray, rid);
        var deadline = DateTime.UtcNow.AddMilliseconds(OverflowSettleMs);
        while (DateTime.UtcNow < deadline)
        {
            open = OverflowHwnd();
            if (open != 0) return open;
            await Task.Delay(50);
        }
        throw new ToolException(ToolErrorCode.ElementNotActionable,
            "could not open the hidden-icons overflow: invoking the chevron did not show it within 1500 ms.",
            "retry; if it persists the taskbar layout may have changed");
    }

    /// <summary>Best effort: close the overflow if it is open. The chevron is a TOGGLE and the overflow both
    /// animates in (~250-290 ms) and auto-closes when the foreground moves, so a visible overflow is re-checked
    /// before the chevron is invoked and a closed one is watched for a late reopen. Never throws (cleanup never
    /// replaces a primary result, F3). Returns true when the overflow is closed afterwards.</summary>
    public async Task<bool> CloseOverflowAsync(nint tray)
    {
        try
        {
            for (var attempt = 1; attempt <= CloseAttempts; attempt++)
            {
                if (!IsOverflowOpen())
                {
                    if (await StaysClosedAsync()) return true;
                }
                else
                {
                    await Task.Delay(CloseConfirmMs);
                    // It closed by itself: do NOT invoke the chevron (that would open it again).
                    if (!IsOverflowOpen() && await StaysClosedAsync()) return true;
                }
                if (!IsOverflowOpen()) continue;
                var rid = await ChevronRuntimeIdAsync(tray);
                if (rid is null) return !IsOverflowOpen();
                await InvokeChevronAsync(tray, rid);
                var deadline = DateTime.UtcNow.AddMilliseconds(OverflowSettleMs);
                while (DateTime.UtcNow < deadline && IsOverflowOpen()) await Task.Delay(50);
            }
            return !IsOverflowOpen();
        }
        catch (ToolException) { return !IsOverflowOpen(); }
    }

    /// <summary>True when the overflow stays closed for ReopenWatchMs (a late animate-in or reopen shows up here).</summary>
    private static async Task<bool> StaysClosedAsync()
    {
        var end = DateTime.UtcNow.AddMilliseconds(ReopenWatchMs);
        while (DateTime.UtcNow < end)
        {
            if (IsOverflowOpen()) return false;
            await Task.Delay(50);
        }
        return !IsOverflowOpen();
    }

    private Task InvokeChevronAsync(nint tray, string rid)
        => _dispatcher.RunActionAsync(() =>
        {
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(tray);
            var el = TrayIconReader.FindByRuntimeId(root, rid)
                     ?? throw new ToolException(ToolErrorCode.ElementNotActionable, "the hidden-icons chevron disappeared.", "retry");
            Interactor.Invoke(el);
        }, 4000);
}
