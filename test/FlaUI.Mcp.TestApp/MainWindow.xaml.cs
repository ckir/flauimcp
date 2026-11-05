// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

﻿using System.Windows;

namespace FlaUI.Mcp.TestApp;

public partial class MainWindow : Window
{
    // A known secret typed into the PasswordBox. The redaction test asserts this literal NEVER
    // appears in a snapshot and that the field renders as [REDACTED].
    public const string SecretValue = "hunter2-NEVER-LEAK";

    public MainWindow()
    {
        InitializeComponent();
        // Min(), never bind, and never a bare literal. An unclamped literal is not hermetic
        // because a work area's size IN DIPS shrinks as display scaling rises. Binding the width to
        // the work area is worse: on a very wide primary monitor it could grow the window past the
        // Canvas.Left="5000" sentinel and invert OffscreenCullTests. Min can only ever shrink.
        //
        // NOTE: SystemParameters.WorkArea wraps SPI_GETWORKAREA and reports the PRIMARY monitor,
        // which on a multi-monitor setup may not be the display hosting this window. That is
        // tolerable only because the design constants below already fit the declared floor machine
        // unaided -- the clamp is belt-and-braces, not the mechanism this depends on.
        Width = System.Math.Min(880, SystemParameters.WorkArea.Width);
        Height = System.Math.Min(420, SystemParameters.WorkArea.Height);
        Secret.Password = SecretValue;
        Grid.ItemsSource = new[]
        {
            new { Name = "r0c0", Value = "r0c1" },
            new { Name = "r1c0", Value = "r1c1" },
            new { Name = "r2c0", Value = "r2c1" },
        };
        var ticker = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(120) };
        int tick = 0;
        ticker.Tick += (_, _) => Ticker.Text = (++tick).ToString();
        ticker.Start();
        SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)
                .AddHook(OpenContextMenuHook);
        Activated += (_, _) => Log("window Activated");
        Deactivated += (_, _) => Log("window Deactivated");
        MenuTarget.ContextMenu.Opened += (_, _) => Log("ContextMenu Opened");
        MenuTarget.ContextMenu.Closed += (_, _) => Log("ContextMenu Closed");
        Log("started");
    }

    // ARRANGE EVENT LOG (test diagnostics). One line per event that decides whether an arrange step can work, so a
    // failing Desktop test can QUOTE what the app saw instead of the reader guessing. Path is derived from the pid,
    // which the tests already know: %TEMP%laui-testapp-<pid>.log (ContextMenuArrange.LogPath). Best-effort: a
    // logging fault must never change the app's behaviour under test.
    public static string LogPath(int pid) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"flaui-testapp-{pid}.log");
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private void Log(string what)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.IO.File.AppendAllText(LogPath(System.Environment.ProcessId),
                $"+{Clock.ElapsedMilliseconds,6}ms {what} | IsActive={IsActive} foreground={(hwnd != System.IntPtr.Zero && GetForegroundWindow() == hwnd)} " +
                $"menuOpen={MenuTarget.ContextMenu.IsOpen} staysOpen={MenuTarget.ContextMenu.StaysOpen}{System.Environment.NewLine}");
        }
        catch (System.Exception) { /* diagnostics only */ }
    }

    // TEST-ONLY ARRANGE HOOK (ROADMAP item 16). The Desktop tests need MenuTarget's context menu OPEN (and then
    // CLOSED again, so no test inherits another's popup), and a real right-click cannot deliver that reliably: it
    // lands on whatever window is on top at that screen point, and while a human holds the foreground Windows
    // silently refuses to raise this window (MEASURED: a SetWindowPos(HWND_TOPMOST) that returned TRUE left
    // WS_EX_TOPMOST unset). A posted WM_CONTEXTMENU does not work either: WPF ignores it (MEASURED). So the tests
    // post THESE registered messages and the window opens/closes the SAME ContextMenu object itself. No XAML
    // change, so the layout guards are untouched. The strings are duplicated in
    // test/FlaUI.Mcp.Tests/Perception/ContextMenuArrange.cs; keep them equal.
    public const string OpenContextMenuMessage = "FlaUI.Mcp.TestApp.OpenMenuTargetContextMenu";
    public const string CloseContextMenuMessage = "FlaUI.Mcp.TestApp.CloseMenuTargetContextMenu";
    private static readonly uint OpenContextMenuMsg = RegisterWindowMessage(OpenContextMenuMessage);
    private static readonly uint CloseContextMenuMsg = RegisterWindowMessage(CloseContextMenuMessage);

    private System.IntPtr OpenContextMenuHook(System.IntPtr hwnd, int msg, System.IntPtr w, System.IntPtr l, ref bool handled)
    {
        if (OpenContextMenuMsg != 0 && (uint)msg == OpenContextMenuMsg)
        {
            Log("open-menu message received");
            MenuTarget.ContextMenu.PlacementTarget = MenuTarget;
            MenuTarget.ContextMenu.IsOpen = true;
            Log("IsOpen=true set");
            handled = true;
        }
        else if (CloseContextMenuMsg != 0 && (uint)msg == CloseContextMenuMsg)
        {
            Log("close-menu message received");
            MenuTarget.ContextMenu.IsOpen = false;
            handled = true;
        }
        return System.IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern System.IntPtr GetForegroundWindow();

    private void OkButton_Click(object sender, RoutedEventArgs e)
        => Status.Text = $"clicked: {Input.Text}";

    // Clear and re-create the items as NEW ListBoxItem objects (same AutomationIds). This destroys
    // the old elements, so a held ref's cached UIA element goes invalid and its RuntimeId no longer
    // matches — forcing the option-C descriptor RE-WALK (the cache fast-path can't short-circuit).
    //
    // All SIX, matching the XAML. Recreating only ItemA/B/C would permanently drop ItemOne/ItemTwo
    // and make any later `contains "Item"` query in the same app instance return 0. That cannot
    // bite today — every FindTests [Fact] builds its own TestAppFixture, so the rebuilding test
    // cannot contaminate the querying ones — but that is a fixture-LIFETIME argument, and the
    // classes that share one app (WaitForTests, AmbiguousResolutionTests, both
    // IClassFixture<TestAppFixture>) would not be protected by it.
    private void RebuildItemsButton_Click(object sender, RoutedEventArgs e)
    {
        ItemList.Items.Clear();
        foreach (var (aid, content) in new[]
                 {
                     ("ItemA", "A"), ("ItemB", "B"), ("ItemC", "C"),
                     ("", "NamedOnly"), ("ItemD", "ItemOne"), ("ItemE", "ItemTwo")
                 })
        {
            var item = new System.Windows.Controls.ListBoxItem { Content = content };
            if (!string.IsNullOrEmpty(aid))
                System.Windows.Automation.AutomationProperties.SetAutomationId(item, aid);
            ItemList.Items.Add(item);
        }
    }

    private void ClearItemsButton_Click(object sender, RoutedEventArgs e) => ItemList.Items.Clear();

    private void FocusReveal_GotFocus(object sender, System.Windows.RoutedEventArgs e)
        => RevealedLabel.Text = "revealed";

    private void ModalButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new System.Windows.Window
        {
            Title = "Modal",
            Width = 200, Height = 120,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this
        };
        var ok = new System.Windows.Controls.Button { Content = "OK" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "ModalOk");
        ok.Click += (_, _) => dlg.Close();
        dlg.Content = ok;
        dlg.ShowDialog(); // BLOCKS the UI thread until closed — the deadlock-recovery target
    }

    // Genuinely freezes the UI thread (no message pump) for a fixed window. Unlike ShowDialog,
    // this stops the thread from servicing COM RPC, so a UIA action arriving DURING the freeze
    // physically blocks the caller's action STA — the faithful repro for the Task-C deadlock test.
    public const int FreezeMs = 2000;
    private void FreezeButton_Click(object sender, RoutedEventArgs e)
        => System.Threading.Thread.Sleep(FreezeMs);

    // Adds a NEW labeled control ~600ms after click — exercises wait_for(until:exists).
    private void DelayRevealButton_Click(object sender, RoutedEventArgs e)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var tb = new System.Windows.Controls.TextBlock { Text = "delayed" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(tb, "DelayedLabel");
            RootPanel.Children.Add(tb);
        };
        timer.Start();
    }
}