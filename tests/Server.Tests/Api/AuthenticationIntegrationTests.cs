using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteSupport.Server.Application.DTOs;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;
using RemoteSupport.Server.Infrastructure.Persistence;

namespace RemoteSupport.Server.Tests.Api;

public class AuthenticationIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly IPasswordHasher _passwordHasher = new PasswordHasher();

    public AuthenticationIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            
            builder.UseSetting("RelayServer:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (descriptor != null) services.Remove(descriptor);

                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseInMemoryDatabase("TestDb_Auth"));
            });
        });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task SeedTestDataAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (!await context.Users.AnyAsync(u => u.Username == "testagent"))
        {
            context.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Username = "testagent",
                Email = "agent@test.com",
                PasswordHash = _passwordHasher.HashPassword("AgentPass123!"),
                Role = UserRole.SupportAgent,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Login_ReturnsToken_WhenValidCredentials()
    {
        await SeedTestDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("testagent", "AgentPass123!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result!.AccessToken.Token));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken.Token));
        Assert.Equal("testagent", result.User.Username);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_WhenInvalidCredentials()
    {
        await SeedTestDataAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("testagent", "WrongPassword"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_WhenUserNotFound()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("nonexistent", "pass"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCurrentUser_ReturnsUserInfo_WhenAuthenticated()
    {
        await SeedTestDataAsync();

        var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("testagent", "AgentPass123!"));
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.AccessToken.Token);

        var response = await _client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCurrentUser_ReturnsUnauthorized_WhenNoToken()
    {
        var response = await _client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_ReturnsCreated_WhenAdmin()
    {
        await SeedTestDataAsync();

        // First login as admin (we need an admin user)
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "testadmin",
            Email = "admin@test.com",
            PasswordHash = _passwordHasher.HashPassword("AdminPass123!"),
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("testadmin", "AdminPass123!"));
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.AccessToken.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("newagent", "new@test.com", "NewPass123!"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Register_ReturnsForbidden_WhenNotAdmin()
    {
        await SeedTestDataAsync();

        var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("testagent", "AgentPass123!"));
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.AccessToken.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("newagent2", "new2@test.com", "NewPass123!"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_ReturnsNewTokens()
    {
        await SeedTestDataAsync();

        var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("testagent", "AgentPass123!"));
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        var refreshResponse = await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshTokenRequest(loginResult!.RefreshToken.Token));

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshResult = await refreshResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(refreshResult);
        Assert.NotEqual(loginResult.RefreshToken.Token, refreshResult!.RefreshToken.Token);
    }

    [Fact]
    public async Task Logout_ReturnsNoContent_WhenAuthenticated()
    {
        await SeedTestDataAsync();

        var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("testagent", "AgentPass123!"));
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.AccessToken.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/auth/logout",
            new LogoutRequest(loginResult.RefreshToken.Token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
