// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Runtime.InteropServices;

namespace FlaUI.Mcp.TrayFixture;

/// <summary>A hidden form that owns one tray icon. kind=uia: WinForms ContextMenuStrip (K1).
/// kind=classic: TrackPopupMenu (K3). kind=window: a titled form (K2). Every command is appended to
/// %TEMP%\flaui-trayfixture-&lt;pid&gt;.log so a test asserts what the app RECEIVED, not only what the
/// tool replied. The icon is removed on a registered exit message, so a killed fixture never leaves a
/// ghost icon behind (SPEC §7, F11).</summary>
public sealed class TrayForm : Form
{
    public const string ExitMessageName = "FlaUI.Mcp.TrayFixture.Exit";
    private static readonly uint ExitMsg = RegisterWindowMessage(ExitMessageName);
    public static string LogPath(int pid) => Path.Combine(Path.GetTempPath(), $"flaui-trayfixture-{pid}.log");

    // Menu items are the same for every kind: one enabled, one disabled, one submenu, a separator, two more.
    private const int IdAlpha = 101, IdDisabled = 102, IdSub = 103, IdBeta = 104, IdExit = 105;

    private readonly NotifyIcon _icon;
    private readonly bool _allowVisible = false; // always false: the form is a message sink, never shown

    public TrayForm(string kind, string tooltip)
    {
        Text = $"FlaUI.Mcp.TrayFixture {Environment.ProcessId}";
        ShowInTaskbar = false;
        _ = Handle; // create the HWND now so the exit message has somewhere to go while the form stays hidden
        _icon = new NotifyIcon { Icon = SystemIcons.Application, Text = tooltip, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Log("left-click"); };
        if (kind == "uia")
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Alpha", null, (_, _) => Log("command Alpha"));
            menu.Items.Add(new ToolStripMenuItem("Disabled Item") { Enabled = false });
            var more = new ToolStripMenuItem("More");
            more.DropDownItems.Add("Sub Item", null, (_, _) => Log("command Sub Item"));
            menu.Items.Add(more);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Beta", null, (_, _) => Log("command Beta"));
            menu.Items.Add("Exit", null, (_, _) => ExitNow());
            menu.Opened += (_, _) => Log("menu opened");
            menu.Closed += (_, _) => Log("menu closed");
            _icon.ContextMenuStrip = menu;
        }
        else
        {
            _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) { if (kind == "classic") ShowClassicMenu(); else ShowWindow(); } };
        }
        Log($"started kind={kind} hwnd=0x{Handle.ToInt64():X} tooltip={tooltip}");
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(_allowVisible && value);

    private void ShowClassicMenu()
    {
        var h = CreatePopupMenu();
        var sub = CreatePopupMenu();
        AppendMenu(sub, MF_STRING, IdSub, "Sub Item");
        AppendMenu(h, MF_STRING, IdAlpha, "&Alpha");
        AppendMenu(h, MF_STRING | MF_GRAYED, IdDisabled, "Disabled Item");
        AppendMenu(h, MF_POPUP, sub, "More");
        AppendMenu(h, MF_SEPARATOR, 0, null);
        AppendMenu(h, MF_STRING, IdBeta, "Beta\tCtrl+B");
        AppendMenu(h, MF_STRING, IdExit, "Exit");
        GetCursorPos(out var p);
        SetForegroundWindow(Handle); // required by TrackPopupMenu for a correct dismissal
        Log("menu opened");
        int cmd = TrackPopupMenuEx(h, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, Handle, IntPtr.Zero);
        PostMessage(Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(h);
        Log("menu closed");
        switch (cmd)
        {
            case IdAlpha: Log("command Alpha"); break;
            case IdSub: Log("command Sub Item"); break;
            case IdBeta: Log("command Beta"); break;
            case IdExit: ExitNow(); break;
            case 0: break; // dismissed
            default: Log($"command id={cmd}"); break;
        }
    }

    private void ShowWindow()
    {
        var f = new Form { Text = "FlaUI Tray Fixture Window", Width = 320, Height = 200, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        var b = new Button { Text = "Alpha", Left = 20, Top = 20, Width = 120 };
        b.Click += (_, _) => { Log("command Alpha"); f.Close(); };
        f.Controls.Add(b);
        f.KeyPreview = true;
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.FormClosed += (_, _) => Log("window closed");
        Log("window opened");
        f.Show();
        f.Activate();
    }

    private void ExitNow()
    {
        Log("exit");
        _icon.Visible = false;
        _icon.Dispose();
        Application.Exit();
    }

    protected override void WndProc(ref Message m)
    {
        if (ExitMsg != 0 && (uint)m.Msg == ExitMsg) { ExitNow(); return; }
        base.WndProc(ref m);
    }

    private static void Log(string what)
    {
        try { File.AppendAllText(LogPath(Environment.ProcessId), $"{DateTime.UtcNow:HH:mm:ss.fff} {what}{Environment.NewLine}"); }
        catch { /* diagnostics only */ }
    }

    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    private const uint WM_NULL = 0;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hMenu, uint flags, nint idOrSubmenu, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
