namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The ACT half of the synthetic-input seam — the only boundary to the OS SendInput call.
/// Each verb carries the EXPECTED target so the real 4b impl (and the 4a recording fake) can run the
/// atomic pre-send re-verify (foreground-root / point hit-test) before the call. 4a ships only the
/// interface + the recording fake; the real Win32SyntheticInput is 4b.</summary>
public interface ISyntheticInput
{
    void KeyType(string text, nint expectedForegroundRoot, int interKeyDelayMs);
    void KeyChord(string[] modifiers, string key, nint expectedForegroundRoot);
    void MouseClick(int physX, int physY, string button, int count, string[] modifiers, nint expectedRootAtPoint);
    void MouseDrag(int startX, int startY, int endX, int endY, string button, nint expectedRootAtStart, nint expectedRootAtEnd);
}
