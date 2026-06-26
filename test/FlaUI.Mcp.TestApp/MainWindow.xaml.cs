using System.Windows;

namespace FlaUI.Mcp.TestApp;

public partial class MainWindow : Window
{
    // A known secret typed into the PasswordBox. The redaction test asserts this literal NEVER
    // appears in a snapshot and that the field renders as [REDACTED].
    public const string SecretValue = "hunter2-NEVER-LEAK";

    public MainWindow()
    {
        InitializeComponent();
        Secret.Password = SecretValue;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
        => Status.Text = $"clicked: {Input.Text}";

    // Clear and re-create the items as NEW ListBoxItem objects (same AutomationIds). This destroys
    // the old elements, so a held ref's cached UIA element goes invalid and its RuntimeId no longer
    // matches — forcing the option-C descriptor RE-WALK (the cache fast-path can't short-circuit).
    private void RebuildItemsButton_Click(object sender, RoutedEventArgs e)
    {
        ItemList.Items.Clear();
        foreach (var (aid, content) in new[] { ("ItemA", "A"), ("ItemB", "B"), ("ItemC", "C") })
        {
            var item = new System.Windows.Controls.ListBoxItem { Content = content };
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