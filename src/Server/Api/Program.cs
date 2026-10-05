using System.Text;
using Serilog;
using RemoteSupport.Server.Api;
using RemoteSupport.Server.Api.Hubs;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Infrastructure;
using RemoteSupport.Server.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithMachineName()
    .WriteTo.Console()
    .WriteTo.File("logs/server-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    builder.Host.UseSerilog();
    LanHosting.ListenOnLan(builder);

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddApiServices(builder.Configuration);

    var jwtKey = ResolveJwtSigningKey(builder.Configuration, builder.Environment);
    builder.Configuration["Jwt:SecretKey"] = jwtKey;

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && (path.StartsWithSegments("/hubs/session") || path.StartsWithSegments("/hubs/webrtc")))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("SupportAgentOnly", policy =>
            policy.RequireRole("SupportAgent"));
        options.AddPolicy("AdminOnly", policy =>
            policy.RequireRole("Admin"));
        options.AddPolicy("SupportAgentOrAdmin", policy =>
            policy.RequireRole("SupportAgent", "Admin"));
        options.AddPolicy("CustomerOnly", policy =>
            policy.RequireRole("Customer"));
    });

    builder.Services.AddSignalR(options =>
    {
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
        options.MaximumReceiveMessageSize = 256 * 1024;
    });

    builder.Services.AddSingleton<ISessionHubContext, SessionHubContext>();
    builder.Services.AddSingleton<RemoteSupport.Server.Api.Transport.WebRtc.WebRtcPeerManager>();
    builder.Services.AddScoped<RemoteSupport.Server.Api.Transport.WebRtc.IWebRtcTransportService,
        RemoteSupport.Server.Api.Transport.WebRtc.WebRtcTransportService>();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Remote Support API v1");
        });
    }

    if (app.Configuration.GetValue("HttpsRedirection:Enabled", false))
        app.UseHttpsRedirection();

    app.UseCors("AllowConfiguredOrigins");
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<SessionHub>("/hubs/session");
    app.MapHub<RemoteSupport.Server.Api.Hubs.WebRtc.WebRtcSignalingHub>("/hubs/webrtc");

    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            if (!app.Environment.IsDevelopment())
            {
                await context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });
                return;
            }

            var result = new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    duration = e.Value.Duration.ToString(),
                    description = e.Value.Description
                }),
                duration = report.TotalDuration.ToString()
            };
            await context.Response.WriteAsJsonAsync(result);
        }
    });

    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (dbContext.Database.IsRelational())
        {
            await dbContext.Database.MigrateAsync();
        }

        await SeedAdminUserAsync(scope.ServiceProvider, app.Environment, app.Configuration);
    }

    var listenPort = LanHosting.GetPort(app.Configuration);
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        LanHosting.LogReachableAddresses(listenPort);
        LanHosting.TryOpenWindowsFirewall(listenPort);
    });

    Log.Information("Remote Support Server starting on {Urls}", string.Join(", ", app.Urls));
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Server terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string ResolveJwtSigningKey(IConfiguration configuration, IHostEnvironment environment)
{
    const string knownWeak = "DevOnly-SuperSecretKey-DoNotUseInProduction-ChangeThis!";
    var jwtKey = configuration["Jwt:SecretKey"];

    if (environment.IsProduction())
    {
        if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey == knownWeak || jwtKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SecretKey must be set via environment (JWT__SecretKey) or user-secrets and be at least 32 characters.");
        }

        return jwtKey;
    }

    if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey == knownWeak || jwtKey.Length < 32)
    {
        jwtKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        Log.Warning("Using an ephemeral JWT signing key for {Environment}. Set Jwt:SecretKey for stable tokens across restarts.", environment.EnvironmentName);
    }

    return jwtKey;
}

static async Task SeedAdminUserAsync(IServiceProvider serviceProvider, IHostEnvironment environment, IConfiguration configuration)
{
    var allowBootstrap = environment.IsDevelopment()
        || string.Equals(configuration["Admin:AllowBootstrap"], "true", StringComparison.OrdinalIgnoreCase);
    if (!allowBootstrap)
        return;

    var password = configuration["Admin:BootstrapPassword"];
    if (string.IsNullOrWhiteSpace(password))
    {
        if (!environment.IsDevelopment())
            return;
        password = "Admin@12345";
    }

    using var scope = serviceProvider.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

    if (!await context.Users.AnyAsync(u => u.Username == "admin"))
    {
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var admin = new RemoteSupport.Server.Domain.Entities.User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            Email = "admin@remotesupport.local",
            PasswordHash = passwordHasher.HashPassword(password),
            Role = RemoteSupport.Server.Domain.Enums.UserRole.Admin,
            DisplayName = "System Administrator",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        context.Users.Add(admin);
        await context.SaveChangesAsync();
        Log.Warning("Bootstrap admin user created. Change the password immediately.");
    }
}

public partial class Program { }
