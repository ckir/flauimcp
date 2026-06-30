using System.Diagnostics;
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Resolves the ref-path ActionTarget from a TOP-LEVEL window UIA element. A top-level window's
/// NativeWindowHandle IS its GA_ROOT, so it compares equal to the leaf's GetForegroundRoot()
/// (GA_ROOT(GetForegroundWindow())). Identity for the deny-list/audit comes from UIA (ProcessId +
/// ClassName) plus a process-name lookup — no Win32 needed here.</summary>
public static class InputTargeting
{
    public static ActionTarget ResolveRefTarget(AutomationElement window)
    {
        nint root = window.Properties.NativeWindowHandle.ValueOrDefault;
        int pid = -1;
        try { pid = window.Properties.ProcessId.ValueOrDefault; } catch { }
        string? proc = null;
        if (pid >= 0) { try { using var p = Process.GetProcessById(pid); proc = p.ProcessName; } catch { } }
        string? cls = null;
        try { cls = window.Properties.ClassName.ValueOrDefault; } catch { }
        return new ActionTarget(root, pid < 0 ? 0 : pid, proc, cls);
    }

    /// <summary>Resolve the deny-list/interlock identity from the ELEMENT being acted on (not its host window),
    /// so an embedded cross-process interlocked element is classified by ITS owner. Root stays the host window's
    /// handle (the element often has no HWND) for audit; process/class come from the element.</summary>
    public static ActionTarget ResolveElementTarget(AutomationElement win, AutomationElement el)
    {
        int pid = -1;
        try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { } // F5: a thrown UIA read must fail closed (->Denied), not crash
        string? proc = null;
        if (pid >= 0) { try { using var p = Process.GetProcessById(pid); proc = p.ProcessName; } catch { } }
        string? cls = null;
        try { cls = el.Properties.ClassName.ValueOrDefault; } catch { } // null/empty if the element has no class
        return new ActionTarget(win.Properties.NativeWindowHandle.ValueOrDefault, pid < 0 ? 0 : pid, proc, cls);
    }
}
