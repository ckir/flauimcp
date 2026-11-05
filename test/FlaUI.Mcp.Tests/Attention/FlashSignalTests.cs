// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class FlashSignalTests
{
    private sealed class FakeHwndSource : IHwndSource
    {
        public bool TryGetHwnd(WindowHandle handle, out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            return false;
        }
    }

    [Fact]
    public void Enabled_true_and_signal_never_throws_on_unknown_handle()
    {
        var f = new FlashSignal(new FakeHwndSource());
        Assert.True(f.Enabled);
        f.Signal(new WindowHandle("w-nonexistent")); // no HWND → no-op, must not throw
    }
}
