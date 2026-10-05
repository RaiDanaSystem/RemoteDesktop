using System.Windows;
using System.Windows.Input;
using SupportAgent.ViewModels;

namespace SupportAgent.Views;

public partial class ShellWindow : Window
{
    public ShellWindow(ShellViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
    }

    public UIElement? ScreenCaptureElement =>
        DataContext is ShellViewModel vm && vm.ModeContent is SupportWorkspace workspace
            ? workspace.ScreenCaptureElement
            : null;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (MaximizeGlyph is not null)
            MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        if (RestoreGlyph is not null)
            RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Closing -= OnClosing;
        if (DataContext is ShellViewModel vm)
            await vm.LeaveModeAsync();
        Application.Current.Shutdown();
    }
}
