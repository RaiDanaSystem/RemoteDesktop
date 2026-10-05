using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using RemoteSupport.Shared;
using SupportAgent.Configuration;
using SupportAgent.Services.Implementation;
using SupportAgent.Services.Interfaces;
using SupportAgent.ViewModels;
using SupportAgent.Views;

namespace SupportAgent;

public partial class App : Application
{
    private static IServiceProvider? _serviceProvider;
    public static AgentOptions Options { get; private set; } = new();

    public static T Resolve<T>() where T : class
        => _serviceProvider!.GetRequiredService<T>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("SupportAgent.log", shared: true)
            .CreateLogger();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var endpointStore = new ServerEndpointStore();
        Options = new AgentOptions
        {
            ServerUrl = EndpointResolver.Resolve(configuration["ServerUrl"], endpointStore.LoadOverride()),
            SupportUsername = configuration["SupportUsername"] ?? "admin",
            SupportPassword = configuration["SupportPassword"] ?? "Admin@12345"
        };

        var loggerFactory = new SerilogLoggerFactory(Log.Logger);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(Options);
        services.AddSingleton(endpointStore);
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<ShellWindow>();
        _serviceProvider = services.BuildServiceProvider();

        var localization = _serviceProvider.GetRequiredService<ILocalizationService>();
        localization.SetLanguage("en");

        var window = _serviceProvider.GetRequiredService<ShellWindow>();
        MainWindow = window;
        window.Show();
    }
}
