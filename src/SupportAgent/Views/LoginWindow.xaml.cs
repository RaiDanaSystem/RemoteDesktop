using System.Windows;
using SupportAgent.ViewModels;

namespace SupportAgent.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginWindow(LoginViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.LoginSuccess += OnLoginSuccess;
        Loaded += OnLoaded;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                LoginButton_Click(this, new RoutedEventArgs());
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var stored = _viewModel.TryPeekUsername();
        if (!string.IsNullOrEmpty(stored))
            _viewModel.Username = stored;

        await _viewModel.CheckServerConnectionAsync();
        var session = await _viewModel.TryRestoreSessionAsync();
        if (session is not null)
            OnLoginSuccess(session);
    }

    private void OnLoginSuccess(Models.UserSession session)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[LOGIN] LoginSuccess fired, navigating to MainWindow...");
                var mainWindow = App.Resolve<MainWindow>();
                var mainVm = App.Resolve<MainViewModel>();
                mainVm.SetSession(session);
                mainWindow.DataContext = mainVm;
                Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
                Application.Current.MainWindow = mainWindow;
                mainWindow.Show();
                Close();
                System.Diagnostics.Debug.WriteLine("[LOGIN] MainWindow shown successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Navigation error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                System.Windows.MessageBox.Show($"Navigation error: {ex.Message}\n\n{ex.InnerException?.Message}", "Debug Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        });
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            vm.Password = PasswordBox.Password;
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.LoginCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\n{ex.InnerException?.Message}",
                "Login Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
