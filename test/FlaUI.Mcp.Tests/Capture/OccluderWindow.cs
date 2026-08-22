using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace FlaUI.Mcp.Tests.Capture;

/// <summary>An opaque top-most window used to cover a target window on a real desktop, so a capture can
/// be proven to see through it.
///
/// ⚠⚠ RAW WIN32, NOT WINDOWS FORMS, AND THAT IS NOT A STYLE CHOICE. No project in this repository sets
/// `&lt;UseWindowsForms&gt;`, so a `System.Windows.Forms.Form` would mean adding an entire UI framework to the
/// build for one test helper. The structure below — STA thread, RegisterClassEx, CreateWindowEx, message
/// pump, teardown on the owning thread via WM_QUIT — is modelled on
/// `src/FlaUI.Mcp.Server/Overlay/GdiActionOverlay.cs`, which solves the same problem in production.
///
/// ⚠⚠ THE THREADING IS COPIED FROM THAT FILE; THE EXTENDED STYLES DELIBERATELY ARE NOT. GdiActionOverlay
/// is a CLICK-THROUGH HUD: it uses WS_EX_LAYERED | WS_EX_TRANSPARENT and a black colour key so its
/// background disappears. An occluder needs the opposite — it must PAINT and COVER. Copying the layered
/// setup would produce a window that does not occlude, and the test would then be measuring nothing.
/// Only WS_EX_TOPMOST and WS_EX_TOOLWINDOW are kept.</summary>
internal sealed class OccluderWindow : IDisposable
{
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    private const uint WM_QUIT = 0x0012, WM_PAINT = 0x000F;
    private const int MAGENTA_BGR = 0x00FF00FF;   // COLORREF is BGR: R=FF, G=00, B=FF

    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _brush;
    private WndProcDelegate? _wndProc;
    private readonly Rectangle _rect;
    private readonly string _className = "flaui-mcp-test-occluder-" + Guid.NewGuid().ToString("N");

    private OccluderWindow(Rectangle rect) => _rect = rect;

    public static OccluderWindow Show(Rectangle rect)
    {
        var w = new OccluderWindow(rect);
        w._thread = new Thread(w.PumpThread) { IsBackground = true, Name = "flaui-mcp-test-occluder" };
        w._thread.SetApartmentState(ApartmentState.STA);
        w._thread.Start();
        w._ready.Wait(5000);
        if (w._hwnd == IntPtr.Zero)
            throw new InvalidOperationException("the occluder window could not be created");
        return w;
    }

    private void PumpThread()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _wndProc = WndProc;
            _brush = CreateSolidBrush(MAGENTA_BGR);
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                hbrBackground = _brush,
                lpszClassName = _className,
            };
            RegisterClassEx(ref wc);

            // WS_VISIBLE so it is mapped without a separate ShowWindow, and no layered/transparent styles:
            // this window exists to be SEEN by a screen scrape.
            _hwnd = CreateWindowEx(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                _className, "", WS_POPUP | WS_VISIBLE,
                _rect.X, _rect.Y, _rect.Width, _rect.Height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, _rect.X, _rect.Y,
                             _rect.Width, _rect.Height, 0x0040 /* SWP_SHOWWINDOW */);
                UpdateWindow(_hwnd);
            }
        }
        catch { _hwnd = IntPtr.Zero; }
        finally { try { _ready.Set(); } catch { } }

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch { /* a pump fault must not take the test process down */ }

        // Free the window and the brush on the thread that owns them, as GdiActionOverlay does.
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_brush != IntPtr.Zero) { DeleteObject(_brush); _brush = IntPtr.Zero; }
        UnregisterClass(_className, GetModuleHandle(null));
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_PAINT)
        {
            var hdc = BeginPaint(hwnd, out var ps);
            var r = new RECT { Left = 0, Top = 0, Right = _rect.Width, Bottom = _rect.Height };
            FillRect(hdc, ref r, _brush);
            EndPaint(hwnd, ref ps);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    public void Dispose()
    {
        var t = _thread;
        _thread = null;
        if (t is not null && _threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            t.Join(3000);
        }
        _ready.Dispose();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
}
