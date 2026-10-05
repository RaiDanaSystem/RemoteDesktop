using CommunityToolkit.Mvvm.ComponentModel;

namespace CustomerAgent.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    protected void SetError(string message)
    {
        HasError = true;
        StatusMessage = message;
    }

    protected void ClearError()
    {
        HasError = false;
        StatusMessage = string.Empty;
    }
}
