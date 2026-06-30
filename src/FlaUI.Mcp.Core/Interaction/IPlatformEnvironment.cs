namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The READ half of the seam: every Win32 probe the guard/leaf needs, behind an interface so
/// each branch is deterministically assertable on a single-state CI runner. 4a ships only the interface
/// + the fake; the real Win32PlatformEnvironment is 4b (alongside Win32SyntheticInput).</summary>
public interface IPlatformEnvironment
{
    /// <summary>Top-level root (GA_ROOT) of the current foreground window, or 0.</summary>
    nint GetForegroundRoot();

    /// <summary>Top-level root (GA_ROOT) of the window under a physical screen point, with its owning
    /// process base-name (no ".exe") and window class — for the coordinate deny-list. Root 0 if none.</summary>
    PointTarget HitTestRoot(int physX, int physY);

    /// <summary>Whether synthetic input can actually reach the interactive user desktop right now
    /// (OpenInputDesktop succeeds AND a foreground window exists). Fail-closed when false.</summary>
    SessionInputState SessionState();
}

public readonly record struct PointTarget(nint Root, string? ProcessName, string? WindowClass);
public readonly record struct SessionInputState(bool CanDeliverInput);
