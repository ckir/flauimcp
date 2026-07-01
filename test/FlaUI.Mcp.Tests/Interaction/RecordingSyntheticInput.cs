using System.Collections.Generic;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Tests.Interaction;

/// <summary>The 4a test double: runs the SAME atomic pre-send re-verify the real 4b leaf will (via the
/// injected IPlatformEnvironment + InputReverify) and, if it passes, records the call instead of firing.
/// Never reaches the session guard in prod, so it is the DI-only "bypass" of the auto-lockout paradox —
/// never a shipped runtime flag.</summary>
public sealed class RecordingSyntheticInput : ISyntheticInput
{
    private readonly IPlatformEnvironment _env;
    public RecordingSyntheticInput(IPlatformEnvironment env) => _env = env;
    public List<string> Calls { get; } = new();
    public List<int> TypeDelays { get; } = new();

    public void KeyType(string text, nint root, int interKeyDelayMs = 0)
    { InputReverify.AssertSameRoot(root, _env.GetForegroundRoot()); Calls.Add($"KeyType:{text}"); TypeDelays.Add(interKeyDelayMs); }

    public void KeyChord(string[] mods, string key, nint root)
    { InputReverify.AssertSameRoot(root, _env.GetForegroundRoot()); Calls.Add($"KeyChord:{string.Join("+", mods)}+{key}"); }

    public void MouseClick(int x, int y, string button, int count, string[] mods, nint root)
    { InputReverify.AssertSameRoot(root, _env.HitTestRoot(x, y).Root); Calls.Add($"MouseClick:{button}:{x},{y}:{count}"); }

    public void MouseDrag(int sx, int sy, int ex, int ey, string button, nint startRoot, nint endRoot)
    { InputReverify.AssertSameRoot(startRoot, _env.HitTestRoot(sx, sy).Root);   // re-verify START before mouse-down (merge-gate blocker)
      InputReverify.AssertSameRoot(endRoot, _env.HitTestRoot(ex, ey).Root); Calls.Add($"MouseDrag:{button}:{sx},{sy}->{ex},{ey}"); }
}
