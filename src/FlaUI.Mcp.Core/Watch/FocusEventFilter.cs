// src/FlaUI.Mcp.Core/Watch/FocusEventFilter.cs
namespace FlaUI.Mcp.Core.Watch;

/// <summary>§7 delivery filter. focus_changed and the global window_opened/window_closed registrations are
/// desktop-wide; deliver ONLY when the source PID matches the subscription window's PID. A missing/zero PID
/// (elevated/inaccessible source, §16.4) is undeliverable -> drop. structure_changed is scope-registered
/// (not global) so it always passes this gate.</summary>
public static class FocusEventFilter
{
    public static bool ShouldDeliver(WatchEventKind kind, int subscriptionProcessId, int sourceProcessId)
    {
        if (kind == WatchEventKind.StructureChanged) return true;
        if (subscriptionProcessId <= 0 || sourceProcessId <= 0) return false;
        return subscriptionProcessId == sourceProcessId;
    }
}
