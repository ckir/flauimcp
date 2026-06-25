namespace FlaUI.Mcp.Core.Windows;

/// <summary>An opaque per-server window handle id like "w1". Namespaced, monotonic.</summary>
public readonly record struct WindowHandle(string Id)
{
    public override string ToString() => Id;
}
