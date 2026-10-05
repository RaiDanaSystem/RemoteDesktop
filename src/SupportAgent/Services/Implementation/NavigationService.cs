using SupportAgent.Services.Interfaces;

namespace SupportAgent.Services.Implementation;

public class NavigationService : INavigationService
{
    private Type _currentViewType = typeof(ViewModels.LoginViewModel);

    public Type CurrentViewType => _currentViewType;
    public event Action<Type>? NavigationChanged;

    public void NavigateTo<T>() where T : class
    {
        NavigateTo(typeof(T));
    }

    public void NavigateTo(Type viewModelType)
    {
        _currentViewType = viewModelType;
        NavigationChanged?.Invoke(viewModelType);
    }
}
