// src/FlaUI.Mcp.Core/Watch/WatchEventKind.cs
namespace FlaUI.Mcp.Core.Watch;

/// <summary>The v1 event kinds (§5). Wire tokens are stable; the enum is internal ordering only.</summary>
public enum WatchEventKind { WindowOpened, WindowClosed, FocusChanged, StructureChanged }

public static class WatchEventKinds
{
    public static string ToWire(WatchEventKind k) => k switch
    {
        WatchEventKind.WindowOpened => "window_opened",
        WatchEventKind.WindowClosed => "window_closed",
        WatchEventKind.FocusChanged => "focus_changed",
        WatchEventKind.StructureChanged => "structure_changed",
        _ => throw new System.ArgumentOutOfRangeException(nameof(k), k, "unknown WatchEventKind"),
    };

    public static bool TryParse(string token, out WatchEventKind kind)
    {
        switch (token)
        {
            case "window_opened": kind = WatchEventKind.WindowOpened; return true;
            case "window_closed": kind = WatchEventKind.WindowClosed; return true;
            case "focus_changed": kind = WatchEventKind.FocusChanged; return true;
            case "structure_changed": kind = WatchEventKind.StructureChanged; return true;
            default: kind = default; return false;
        }
    }
}
