using System.Windows;
using System.Windows.Input;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed partial class ChangelogWindow : Window
{
    public ChangelogWindow(string version, string body)
    {
        InitializeComponent();

        TitleText.Text = $"What's New in v{version}";
        ChangelogViewer.Document = MarkdownRenderer.Render(body);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
