using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Server.Overlay;

/// <summary>Real intent overlay (spec §5.2 mechanism A1). A single layered/click-through/topmost/no-activate
/// window on a dedicated BACKGROUND STA thread + message pump draws a hollow red rect at a screen rect for
/// OverlayMs, then hides. Created lazily on first preview, reused thereafter. IDisposable so the DI container
/// tears down the STA thread + GDI handles (SEAT-F: prevents orphaned threads/leaked handles when multiple
/// Server instances are created+disposed in one process, e.g. the in-process xUnit runner).</summary>
public sealed class GdiActionOverlay : IActionOverlay, IDisposable
{
    private readonly int _ms;
    private readonly OverlayTokenGate _gate = new();
    private readonly object _startLock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _pen;                 // cached — created once, deleted in Dispose (no per-paint alloc, SEAT-J)
    private volatile bool _disposed;
    private RECT _pending;               // last requested rect; read by the pump on WM_APP_SHOW
    private readonly object _rectLock = new();
    private WndProcDelegate? _wndProc;   // pinned for the window's lifetime (GC must not collect it)

    public GdiActionOverlay(int overlayMs) => _ms = overlayMs;
    public bool Enabled => true;

    public async Task PreviewAsync(OverlayRect rect)
    {
        if (_disposed || rect.IsDegenerate) return;
        try
        {
            EnsureStarted();
            long token = _gate.Next();
            Show(rect);
            await Task.Delay(_ms).ConfigureAwait(false);
            if (_gate.OwnsCurrent(token)) Hide();
        }
        catch { /* INV-OV-4: the overlay must NEVER break an act. */ }
    }

    private void EnsureStarted()
    {
        if (_thread is not null) return;
        lock (_startLock)
        {
            if (_thread is not null) return;
            var t = new Thread(PumpThread) { IsBackground = true, Name = "flaui-mcp-overlay" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _thread = t;
            _ready.Wait(2000); // window created (or thread died) — bounded so a failed create can't hang forever
        }
    }

    // ── STA thread: register class, create the layered window, run the pump ──
    private void PumpThread()
    {
        _threadId = GetCurrentThreadId();
        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = OverlaySentinel.ClassName, // the sentinel the perception layer filters
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            OverlaySentinel.ClassName, "", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            // Color-key: black (the background) becomes fully transparent, so only the red border shows.
            SetLayeredWindowAttributes(_hwnd, 0x000000 /* RGB black */, 0, LWA_COLORKEY);
            _pen = CreatePen(PS_SOLID, BorderPx, 0x0000FF /* RGB(255,0,0) is 0x0000FF in COLORREF BGR */);
        }
        _ready.Set(); // unblock EnsureStarted whether or not the window created

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Pump exited (WM_QUIT from Dispose): free GDI + window on the SAME thread that owns them.
        if (_pen != IntPtr.Zero) { DeleteObject(_pen); _pen = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        UnregisterClass(OverlaySentinel.ClassName, GetModuleHandle(null));
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_SHOW:
            {
                RECT r; lock (_rectLock) r = _pending;
                SetWindowPos(hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                InvalidateRect(hwnd, IntPtr.Zero, true);
                return IntPtr.Zero;
            }
            case WM_APP_HIDE:
                ShowWindow(hwnd, SW_HIDE);
                return IntPtr.Zero;
            case WM_PAINT:
            {
                var hdc = BeginPaint(hwnd, out var ps);
                try
                {
                    GetClientRect(hwnd, out var cr);
                    // Background is the class black brush (keyed transparent). Draw a hollow red border:
                    var oldPen = SelectObject(hdc, _pen);
                    var oldBrush = SelectObject(hdc, GetStockObject(NULL_BRUSH)); // hollow interior stays black->transparent
                    Rectangle(hdc, cr.Left, cr.Top, cr.Right, cr.Bottom);
                    SelectObject(hdc, oldPen);
                    SelectObject(hdc, oldBrush);
                }
                finally { EndPaint(hwnd, ref ps); }
                return IntPtr.Zero;
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Show(OverlayRect rect)
    {
        if (_hwnd == IntPtr.Zero) return;
        lock (_rectLock) _pending = new RECT { Left = rect.L, Top = rect.T, Right = rect.L + rect.W, Bottom = rect.T + rect.H };
        PostMessage(_hwnd, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
    }

    private void Hide()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_HIDE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var t = _thread;
        if (t is not null && _threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            t.Join(2000);
        }
        _ready.Dispose();
    }

    // ── constants ──
    private const int BorderPx = 4;
    private const uint WM_PAINT = 0x000F, WM_QUIT = 0x0012;
    private const uint WM_APP_SHOW = 0x8001, WM_APP_HIDE = 0x8002; // WM_APP + n
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOPMOST = 0x00000008,
                       WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LWA_COLORKEY = 0x00000001;
    private const int PS_SOLID = 0, BLACK_BRUSH = 4, NULL_BRUSH = 5;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    // ── P/Invoke ──
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, IntPtr hInst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
}
