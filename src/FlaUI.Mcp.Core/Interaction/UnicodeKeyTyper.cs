using System;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Drives a paced (inter-key-delayed) unicode type. Spreading keystrokes over time re-opens the
/// focus-steal window that the shipped single-batch SendInput closed, so this re-verifies the foreground
/// BEFORE each character group and aborts mid-type (leaving whatever already landed) if the reverify
/// throws. Pure over its callbacks -> headless-testable; the Win32 leaf wires real reverify/send/sleep.
/// The pause is BETWEEN keys (n-1 pauses): none before the first char, none after the last.</summary>
public static class UnicodeKeyTyper
{
    public static void Drive(string text, int interKeyDelayMs, Action reverify, Action<INPUT[]> send, Action<int> sleep)
    {
        bool first = true;
        foreach (var group in UnicodeKeyInput.Groups(text ?? string.Empty))
        {
            reverify();
            if (!first && interKeyDelayMs > 0) sleep(interKeyDelayMs);
            send(group);
            first = false;
        }
    }
}
