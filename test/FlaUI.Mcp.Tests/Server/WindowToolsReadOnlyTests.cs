// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class WindowToolsReadOnlyTests
{
    // --read-only-mode must block the state-changing window tools (README promises launch/focus/close
    // short-circuit to WriteBlockedReadOnly). GuardWrite fires before touching the (null) WindowManager.
    private static WindowTools ReadOnly() => new(null!, new ServerOptions(ReadOnly: true, AllowElevation: false), null!);

    [Fact]
    public async Task Launch_is_blocked_in_read_only_mode()
        => Assert.Contains("WriteBlockedReadOnly", await ReadOnly().DesktopLaunchApp("C:\\notepad.exe"));

    [Fact]
    public async Task Focus_is_blocked_in_read_only_mode()
        => Assert.Contains("WriteBlockedReadOnly", await ReadOnly().DesktopFocusWindow("w1"));

    [Fact]
    public async Task Close_is_blocked_in_read_only_mode()
        => Assert.Contains("WriteBlockedReadOnly", await ReadOnly().DesktopCloseWindow("w1"));

    [Fact]
    public async Task OpenWindow_with_invalid_pid_returns_InvalidArguments()
        => Assert.Contains("InvalidArguments", await ReadOnly().DesktopOpenWindow("pid", "not-a-number"));
}
