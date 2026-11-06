// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Tray;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Tests.Tray;

/// <summary>The tray overflow window does not auto-close here, so a failed or mutated earlier run can leave it
/// open. Tray tests assume a closed overflow; normalise it first instead of depending on what a previous run left.</summary>
internal static class TrayTestHygiene
{
    public static void EnsureOverflowClosed()
    {
        if (!TrayLocator.IsOverflowOpen()) return;

        var dispatcher = new AutomationDispatcher();
        try
        {
            var windows = new WindowManager(dispatcher);
            try
            {
                new TrayLocator(windows, dispatcher).CloseOverflowAsync(TrayLocator.RequireTaskbar().Hwnd).GetAwaiter().GetResult();
            }
            finally { windows.Dispose(); }
        }
        finally { dispatcher.Dispose(); }

        if (TrayLocator.IsOverflowOpen())
            throw new InvalidOperationException("tray test hygiene: the overflow could not be closed");
    }
}
