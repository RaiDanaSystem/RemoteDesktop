using System.Windows;
using SupportAgent.ViewModels;

namespace SupportAgent.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
    }

    public UIElement? ScreenCaptureElement => LiveSessionView.ScreenCaptureElement;

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Closing -= OnWindowClosing;
        if (DataContext is MainViewModel vm)
            await vm.TerminateActiveSessionAsync();
        Application.Current.Shutdown();
    }
}
