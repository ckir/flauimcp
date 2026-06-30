using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Tests.Interaction;

/// <summary>Scriptable IPlatformEnvironment for headless guard + fake-reverify tests.</summary>
public sealed class FakePlatformEnvironment : IPlatformEnvironment
{
    public nint ForegroundRoot { get; set; }
    public PointTarget PointResult { get; set; }
    public bool CanDeliver { get; set; } = true;

    public nint GetForegroundRoot() => ForegroundRoot;
    public PointTarget HitTestRoot(int x, int y) => PointResult;
    public SessionInputState SessionState() => new(CanDeliver);
}
