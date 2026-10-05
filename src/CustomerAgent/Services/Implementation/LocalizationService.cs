using System.Globalization;
using System.Windows;
using CustomerAgent.Services.Interfaces;

namespace CustomerAgent.Services.Implementation;

public class LocalizationService : ILocalizationService
{
    private string _currentLanguage = "en";

    public string CurrentLanguage => _currentLanguage;
    public bool IsRtl => _currentLanguage == "fa";
    public event Action? LanguageChanged;

    public LocalizationService()
    {
    }

    public void SetLanguage(string language)
    {
        _currentLanguage = language;

        var culture = language switch
        {
            "fa" => new CultureInfo("fa"),
            _ => new CultureInfo("en")
        };

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var resourceUri = language switch
        {
            "fa" => new Uri("pack://application:,,,/Resources/Languages/Strings.fa.xaml", UriKind.Absolute),
            _ => new Uri("pack://application:,,,/Resources/Languages/Strings.en.xaml", UriKind.Absolute)
        };

        try
        {
            var dict = new ResourceDictionary { Source = resourceUri };
            var merged = Application.Current.Resources.MergedDictionaries;
            var previous = merged
                .Where(d => d.Source is not null &&
                            (d.Source.OriginalString.Contains("Strings.en.xaml", StringComparison.OrdinalIgnoreCase)
                             || d.Source.OriginalString.Contains("Strings.fa.xaml", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var item in previous)
                merged.Remove(item);
            merged.Add(dict);
        }
        catch { }

        LanguageChanged?.Invoke();
    }

    public string GetString(string key)
    {
        try
        {
            if (Application.Current.Resources.Contains(key))
                return Application.Current.Resources[key]?.ToString() ?? key;
        }
        catch { }
        return key;
    }
}
