using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The real SendInput leaf (4b). Apartment-agnostic — the caller invokes it on a Task.Run so
/// the atomic pre-send re-verify and the SendInput call share one thread (spec §2). Re-verifies the
/// expected target with the LIVE foreground/point root immediately before firing; a mismatch aborts and
/// fires nothing (InputReverify throws). Validated by the 2026-06-30 active-RDP spike.</summary>
public sealed class Win32SyntheticInput : ISyntheticInput
{
    private readonly IPlatformEnvironment _env;
    public Win32SyntheticInput(IPlatformEnvironment env) => _env = env;

    public void KeyType(string text, nint expectedForegroundRoot)
    {
        Reverify(expectedForegroundRoot, _env.GetForegroundRoot());
        Send(UnicodeKeyInput.Build(text ?? string.Empty));
    }

    public void KeyChord(string[] modifiers, string key, nint expectedForegroundRoot)
    {
        Reverify(expectedForegroundRoot, _env.GetForegroundRoot());
        var chord = KeyChordParser.Parse((modifiers is { Length: > 0 } ? string.Join("+", modifiers) + "+" : "") + key);
        var inputs = new List<INPUT>();
        foreach (var m in chord.ModifierVks) inputs.Add(Vk(m, 0));
        inputs.Add(Vk(chord.KeyVk, 0));
        inputs.Add(Vk(chord.KeyVk, Win32Interop.KEYEVENTF_KEYUP));
        for (int i = chord.ModifierVks.Length - 1; i >= 0; i--) inputs.Add(Vk(chord.ModifierVks[i], Win32Interop.KEYEVENTF_KEYUP));
        Send(inputs.ToArray());
    }

    public void MouseClick(int physX, int physY, string button, int count, string[] modifiers, nint expectedRootAtPoint)
    {
        Reverify(expectedRootAtPoint, _env.HitTestRoot(physX, physY).Root);
        var (down, up) = ButtonFlags(button);
        var (ax, ay) = AbsolutePoint(physX, physY);
        var inputs = new List<INPUT> { Move(ax, ay) };
        for (int i = 0; i < Math.Clamp(count, 1, 2); i++) { inputs.Add(Mouse(ax, ay, down)); inputs.Add(Mouse(ax, ay, up)); }
        Send(inputs.ToArray());
    }

    public void MouseDrag(int startX, int startY, int endX, int endY, string button, nint expectedRootAtStart, nint expectedRootAtEnd)
    {
        var (down, up) = ButtonFlags(button);
        // re-verify the START root immediately before the mouse-DOWN (merge-gate BLOCKER, both reviewers):
        // an overlay/focus-steal at the start coord between authorize and fire must NOT receive an
        // unauthorized button-down (Win32 SetCapture on WM_*BUTTONDOWN would also hand it the up). This
        // throws BEFORE any button goes down, so there is nothing to release — no stuck-button dance needed.
        Reverify(expectedRootAtStart, _env.HitTestRoot(startX, startY).Root);
        var (sax, say) = AbsolutePoint(startX, startY);
        Send(new[] { Move(sax, say), Mouse(sax, say, down) });
        // re-verify the END root immediately before the drop (spec §3.2: an overlay can move in the gap).
        // CRITICAL (agy BLOCKER): once the button is DOWN it MUST be released on every path, or the OS
        // mouse is left globally stuck-down. If the re-verify aborts, release at the START point — this
        // cancels the drag harmlessly (drops back onto the origin) and NEVER drops into the suspect/denied
        // end window — then propagate the abort.
        try { Reverify(expectedRootAtEnd, _env.HitTestRoot(endX, endY).Root); }
        catch
        {
            Send(new[] { Move(sax, say), Mouse(sax, say, up) }); // release at origin, not at the suspect end
            throw;
        }
        var (eax, eay) = AbsolutePoint(endX, endY);
        Send(new[] { Move(eax, eay), Mouse(eax, eay, up) });
    }

    // agy R2: distinguish a focus-steal (root changed to ANOTHER window -> ElementDisappearedDuringAction,
    // re-focus + retry) from the desktop locking mid-send (foreground/hit-test root collapses to 0 ->
    // InputDesktopUnavailable, the agent must get the session unlocked, not retry the element).
    private void Reverify(nint expected, nint actual)
    {
        if (actual == 0 && !_env.SessionState().CanDeliverInput)
            throw new ToolException(ToolErrorCode.InputDesktopUnavailable,
                "The interactive input desktop became unavailable mid-send (locked / disconnected).",
                "connect and unlock the session, then retry");
        InputReverify.AssertSameRoot(expected, actual);
    }

    private static (int ax, int ay) AbsolutePoint(int physX, int physY)
    {
        int ox = Win32Interop.GetSystemMetrics(Win32Interop.SM_XVIRTUALSCREEN);
        int oy = Win32Interop.GetSystemMetrics(Win32Interop.SM_YVIRTUALSCREEN);
        int w = Win32Interop.GetSystemMetrics(Win32Interop.SM_CXVIRTUALSCREEN);
        int h = Win32Interop.GetSystemMetrics(Win32Interop.SM_CYVIRTUALSCREEN);
        return VirtualDesktopMap.ToAbsolute(physX, physY, ox, oy, w, h);
    }

    private static (uint down, uint up) ButtonFlags(string button) => button?.Trim().ToLowerInvariant() switch
    {
        "right" => (Win32Interop.MOUSEEVENTF_RIGHTDOWN, Win32Interop.MOUSEEVENTF_RIGHTUP),
        "middle" => (Win32Interop.MOUSEEVENTF_MIDDLEDOWN, Win32Interop.MOUSEEVENTF_MIDDLEUP),
        _ => (Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP),
    };

    private static INPUT Vk(ushort vk, uint flags) => new()
    { type = Win32Interop.INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = 0 } } };

    private static INPUT Move(int ax, int ay) => Mouse(ax, ay, Win32Interop.MOUSEEVENTF_MOVE);

    private static INPUT Mouse(int ax, int ay, uint action) => new()
    {
        type = Win32Interop.INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = ax, dy = ay, mouseData = 0,
            dwFlags = action | Win32Interop.MOUSEEVENTF_ABSOLUTE | Win32Interop.MOUSEEVENTF_VIRTUALDESK, time = 0, dwExtraInfo = 0 } }
    };

    private static void Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        Win32Interop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
