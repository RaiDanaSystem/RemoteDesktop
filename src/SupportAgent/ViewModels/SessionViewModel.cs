using CommunityToolkit.Mvvm.ComponentModel;

namespace SupportAgent.ViewModels;

public partial class SessionViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _sessionInfo = "No active sessions";

    [ObservableProperty]
    private string _sessionDuration = "00:00:00";
}
