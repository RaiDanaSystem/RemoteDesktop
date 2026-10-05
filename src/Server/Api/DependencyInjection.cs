using Asp.Versioning;
using Microsoft.OpenApi.Models;
using RemoteSupport.Server.Api.Hubs;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Infrastructure.Persistence;

namespace RemoteSupport.Server.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers();
        services.AddEndpointsApiExplorer();

        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = ApiVersionReader.Combine(
                new UrlSegmentApiVersionReader(),
                new HeaderApiVersionReader("X-Api-Version"));
        })
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Remote Support API",
                Version = "v1",
                Description = "Enterprise Remote Support Platform - Backend API"
            });
        });

        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>(
                name: "database",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);

        services.AddCors(options =>
        {
            options.AddPolicy("AllowConfiguredOrigins", policy =>
            {
                var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
                policy.SetIsOriginAllowed(origin =>
                    {
                        if (LanHosting.IsPrivateLanOrigin(origin))
                            return true;
                        return origins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
                    })
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });

        services.AddSingleton<ISessionEventNotifier, SignalRSessionEventNotifier>();

        return services;
    }
}
