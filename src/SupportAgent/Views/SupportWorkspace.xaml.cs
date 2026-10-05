using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SupportAgent.Models;
using SupportAgent.ViewModels;

namespace SupportAgent.Views;

public partial class SupportWorkspace : UserControl
{
    private readonly IServiceProvider _services;
    private MainViewModel? _viewModel;

    public SupportWorkspace(IServiceProvider services, UserSession session)
    {
        _services = services;
        InitializeComponent();
        _viewModel = services.GetRequiredService<MainViewModel>();
        _viewModel.SetSession(session);
        DataContext = _viewModel;
    }

    public UIElement ScreenCaptureElement => LiveSessionView.ScreenCaptureElement;

    public bool HasLiveSession => _viewModel?.HasLiveSession == true;

    public async Task StopAsync()
    {
        if (_viewModel is not null)
            await _viewModel.TerminateActiveSessionAsync();
    }
}
