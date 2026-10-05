using CustomerAgent.Services.Implementation;
using CustomerAgent.Services.Input;
using CustomerAgent.Services.Interfaces;
using CustomerAgent.Services.Session;
using CustomerAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared;
using RemoteSupport.Shared.Clipboard;
using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.Transport;
using RemoteSupport.Shared.Transport.WebRtc;

namespace CustomerAgent;

public static class CustomerComposition
{
    public static void ConfigureServices(IServiceCollection services, AgentOptions options)
    {
        services.AddSingleton(options);
        services.AddHttpClient<ICustomerApiClient, CustomerApiClient>(client =>
        {
            client.BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ISignalRClient, CustomerSignalRClient>();
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton<SignalingClient>();
        services.AddSingleton<WebRtcConfiguration>(WebRtcConfiguration.Default);
        services.AddSingleton<WebRtcSessionManager>();

        services.AddSingleton<InputConsentManager>();
        services.AddSingleton<ClipboardManager>();
        services.AddSingleton<WindowsInputInjection>();
        services.AddTransient<RemoteDesktopSession>();

        services.AddTransient<CustomerViewModel>();
    }
}
