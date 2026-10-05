using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared;
using RemoteSupport.Shared.Clipboard;
using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.Transport;
using RemoteSupport.Shared.Transport.WebRtc;
using SupportAgent.Services.Implementation;
using SupportAgent.Services.Interfaces;
using SupportAgent.Services.Session;
using SupportAgent.ViewModels;
using SupportAgent.ViewModels.Session;

namespace SupportAgent;

internal static class SupportComposition
{
    public static void ConfigureServices(
        IServiceCollection services,
        AgentOptions options,
        ILocalizationService localization)
    {
        services.AddSingleton(options);
        services.AddHttpClient<IApiClient, ApiClient>(client =>
        {
            client.BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<ITokenStorage, TokenStorage>();
        services.AddSingleton<ILocalizationService>(localization);
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IInputConsentManager, InputConsentManager>();
        services.AddSingleton<ClipboardManager>();
        services.AddSingleton<IClipboardManager>(sp => sp.GetRequiredService<ClipboardManager>());
        services.AddSingleton<ISignalRClient, SupportSignalRClient>();
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton<SignalingClient>();
        services.AddSingleton<WebRtcConfiguration>(WebRtcConfiguration.Default);
        services.AddSingleton<WebRtcSessionManager>();
        services.AddTransient<RemoteDesktopSession>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SessionViewModel>();
    }
}
