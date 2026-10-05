namespace SupportAgent.Services.Interfaces;

public interface INavigationService
{
    event Action<Type>? NavigationChanged;
    Type CurrentViewType { get; }
    void NavigateTo<T>() where T : class;
    void NavigateTo(Type viewModelType);
}
