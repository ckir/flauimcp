using System.Windows;

namespace FlaUI.Mcp.TestApp;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OkButton_Click(object sender, RoutedEventArgs e)
        => Status.Text = $"clicked: {Input.Text}";
}