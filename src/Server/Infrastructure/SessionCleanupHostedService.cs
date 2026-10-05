using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Infrastructure;

public class SessionCleanupHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SessionCleanupHostedService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _pendingStaleThreshold = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _activeStaleThreshold = TimeSpan.FromMinutes(30);

    public SessionCleanupHostedService(IServiceProvider serviceProvider, ILogger<SessionCleanupHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Session cleanup host service starting");

        await RunCleanupCycleAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, cancellationToken);
                await RunCleanupCycleAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup cycle");
            }
        }

        _logger.LogInformation("Session cleanup host service stopping");
    }

    private async Task RunCleanupCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();

        var pendingCleaned = await sessionManager.CleanupPendingSessionsAsync(_pendingStaleThreshold, cancellationToken);
        var activeCleaned = await sessionManager.CleanupActiveSessionsAsync(_activeStaleThreshold, cancellationToken);
        var total = pendingCleaned + activeCleaned;

        if (total > 0)
            _logger.LogInformation("Session cleanup: ended {Total} stale sessions ({Pending} pending, {Active} active)",
                total, pendingCleaned, activeCleaned);
    }
}
