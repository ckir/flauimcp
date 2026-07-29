using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

// CONSOLE-MACHINE-ONLY: fires real SendInput; needs an active unlocked session + a granted lease.
// Carries the SyntheticInput trait for the same reason InputToolsTests does.
[Trait("Category", "Desktop")]
[Trait("Category", "SyntheticInput")]
public class PresenceDesktopTests
{
    private static bool InputLocked()
    {
        var lease = new FileLeaseProvider().Read(out _);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        return lease is null || !lease.IsValidNow(System.DateTime.UtcNow, sid);
    }

    /// <summary>
    /// A zero-delta relative mouse move: the smallest event that advances the GetLastInputInfo
    /// clock without delivering anything meaningful to whatever window currently has focus.
    /// Deliberately NOT a keystroke -- SendInput goes to the focused window, which during a suite
    /// run may be the TestApp, a launched VS Code, or the developer's own editor.
    /// </summary>
    private static void NudgeIdleClock()
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = 0, // INPUT_MOUSE
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0, dy = 0, mouseData = 0,
                        dwFlags = Win32Interop.MOUSEEVENTF_MOVE,
                        time = 0, dwExtraInfo = 0
                    }
                }
            }
        };
        var sent = Win32Interop.SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        Assert.Equal(1u, sent); // 0 means the input was blocked (UIPI/lease) -- a real failure
    }

    [SkippableFact]
    public void Real_idle_source_reports_active_right_after_input()
    {
        Skip.If(InputLocked(), "no active input lease — grant one on a console with `flaui-mcp unlock`");

        NudgeIdleClock();

        var idle = new Win32IdleSource().IdleMs();
        Assert.True(idle >= 0);
        Assert.True(idle < 60_000, $"expected the synthesised input to move the idle clock, got {idle} ms");
        Assert.Equal(Activity.Active, IdleActivity.Bucket(idle, 60_000, 300_000));
    }
}
