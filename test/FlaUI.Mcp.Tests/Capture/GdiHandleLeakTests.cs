using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

[Trait("Category", "Desktop")]
public class GdiHandleLeakTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public GdiHandleLeakTests(TestAppFixture app) => _app = app;

    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

    private static (uint Gdi, uint User) Counts()
    {
        var h = Process.GetCurrentProcess().Handle;
        return (GetGuiResources(h, GR_GDIOBJECTS), GetGuiResources(h, GR_USEROBJECTS));
    }

    [Fact]
    public async Task Repeated_successful_captures_leave_handle_counts_flat()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();
        // Warm up first: the first call allocates caches that are not a leak.
        for (int i = 0; i < 3; i++) src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000)?.Dispose();

        var before = Counts();
        for (int i = 0; i < 30; i++) src.Acquire(hwnd, new Size(rect.Width, rect.Height), 5000)?.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = Counts();

        Assert.True(after.Gdi <= before.Gdi + 2, $"GDI objects grew {before.Gdi} -> {after.Gdi}");
        Assert.True(after.User <= before.User + 2, $"USER objects grew {before.User} -> {after.User}");
    }

    // THE EMPTY-CROP PATH exits AFTER step 6b allocated the bitmap, so it is in scope for this gate.
    [Fact]
    public async Task An_empty_crop_refusal_leaves_handle_counts_flat()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();
        // An element rect far outside the window forces WindowCropGeometry to return null.
        var geo = new CaptureGeometry(new Rectangle(rect.X + 99999, rect.Y + 99999, 10, 10),
            Array.Empty<Rectangle>(), false, false, null, Array.Empty<MaskEscalationEntry>(),
            rect, hwnd, false);

        // ⚠ WARM UP FIRST, exactly as the success test above does. Without this the baseline is
        // taken before GDI+ has ever been touched in this process, so the FIRST call's one-time
        // allocation is counted as growth. MEASURED: this test failed "GDI objects grew 0 -> 3"
        // with no warm-up -- and 3 is a ONE-TIME cost, not a leak: a per-iteration leak across 20
        // iterations would be ~60, and the success test is flat across 30.
        for (int i = 0; i < 3; i++)
        {
            try
            {
                ScreenCapture.CaptureWindow(geo, 0, CaptureScope.Element,
                    Array.Empty<CaptureWarning>(), src, 5000);
            }
            catch (FlaUI.Mcp.Core.Errors.ToolException) { /* expected */ }
        }

        var before = Counts();
        for (int i = 0; i < 20; i++)
        {
            try
            {
                ScreenCapture.CaptureWindow(geo, 0, CaptureScope.Element,
                    Array.Empty<CaptureWarning>(), src, 5000);
            }
            catch (FlaUI.Mcp.Core.Errors.ToolException) { /* expected */ }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = Counts();

        Assert.True(after.Gdi <= before.Gdi + 2, $"GDI objects grew {before.Gdi} -> {after.Gdi}");
    }
}
