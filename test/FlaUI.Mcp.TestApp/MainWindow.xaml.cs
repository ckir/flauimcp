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
}