using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Infrastructure.Persistence;

namespace RemoteSupport.Server.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<IApplicationDbContext, ApplicationDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=remotesupport.db";
            options.UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
        });

        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<ISupportCodeService, SupportCodeService>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<ILoginAttemptTracker, LoginAttemptTracker>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<ICustomerAuthService, CustomerAuthService>();
        services.AddScoped<ISessionManager, SessionManager>();
        services.AddScoped<ITransportService, TransportService>();
        services.AddHostedService<SessionCleanupHostedService>();

        return services;
    }
}
