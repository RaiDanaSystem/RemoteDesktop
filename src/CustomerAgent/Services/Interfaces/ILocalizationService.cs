namespace CustomerAgent.Services.Interfaces;

public interface ILocalizationService
{
    string CurrentLanguage { get; }
    bool IsRtl { get; }
    event Action? LanguageChanged;
    void SetLanguage(string language);
    string GetString(string key);
}
