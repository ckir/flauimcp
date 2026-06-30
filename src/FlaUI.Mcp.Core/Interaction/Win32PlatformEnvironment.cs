using System;
using System.Diagnostics;
using System.Text;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The real Win32 probe seam (4b). Foreground root, coordinate hit-test resolver (DPI-correct
/// WindowFromPhysicalPoint -> GA_ROOT -> owning process+class), and the fail-closed session oracle
/// (OpenInputDesktop succeeds AND a foreground window exists). Validated by the 2026-06-30 spike.</summary>
public sealed class Win32PlatformEnvironment : IPlatformEnvironment
{
    public nint GetForegroundRoot()
    {
        var fg = Win32Interop.GetForegroundWindow();
        return fg == 0 ? 0 : Win32Interop.GetAncestor(fg, Win32Interop.GA_ROOT);
    }

    public PointTarget HitTestRoot(int physX, int physY)
    {
        var hit = Win32Interop.WindowFromPhysicalPoint(new POINT { X = physX, Y = physY });
        if (hit == 0) return new PointTarget(0, null, null);
        var root = Win32Interop.GetAncestor(hit, Win32Interop.GA_ROOT);
        if (root == 0) return new PointTarget(0, null, null);
        return ResolveRoot(root);
    }

    public PointTarget ResolveRoot(nint root)
    {
        if (root == 0) return new PointTarget(0, null, null);
        Win32Interop.GetWindowThreadProcessId(root, out uint pid);
        string? proc = null;
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
        var sb = new StringBuilder(256);
        string? cls = Win32Interop.GetClassName(root, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        return new PointTarget(root, proc, cls);
    }

    public SessionInputState SessionState()
    {
        bool foreground = Win32Interop.GetForegroundWindow() != 0;
        nint desk = Win32Interop.OpenInputDesktop(0, false, Win32Interop.MAXIMUM_ALLOWED);
        bool deskOk = desk != 0;
        if (deskOk) Win32Interop.CloseDesktop(desk);
        return new SessionInputState(deskOk && foreground); // fail-closed: both must hold
    }
}
