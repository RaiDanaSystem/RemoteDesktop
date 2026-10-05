using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
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

public class RelayTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly IPasswordHasher _passwordHasher = new PasswordHasher();
    private readonly string _deviceId = "relay-test-device";
    private Guid _agentUserId;
    private string _agentToken = string.Empty;

    public RelayTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (d != null) services.Remove(d);
                services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase("RelayTest"));
            });
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AuditLogs.RemoveRange(db.AuditLogs);
        db.TransportTokens.RemoveRange(db.TransportTokens);
        db.SessionEvents.RemoveRange(db.SessionEvents);
        db.Sessions.RemoveRange(db.Sessions);
        db.SupportCodes.RemoveRange(db.SupportCodes);
        db.RefreshTokens.RemoveRange(db.RefreshTokens);
        db.Users.RemoveRange(db.Users);
        await db.SaveChangesAsync();

        var agent = new User
        {
            Id = Guid.NewGuid(), Username = "agent", Email = "a@t.com",
            PasswordHash = _passwordHasher.HashPassword("Pass1!"),
            Role = UserRole.SupportAgent, IsActive = true, CreatedAtUtc = DateTime.UtcNow
        };
        db.Users.Add(agent);
        await db.SaveChangesAsync();
        _agentUserId = agent.Id;
    }

    private async Task LoginAsync()
    {
        await SeedAsync();
        var r = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("agent", "Pass1!"));
        var lr = (await r.Content.ReadFromJsonAsync<LoginResponse>())!;
        _agentToken = lr.AccessToken.Token;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _agentToken);
    }

    private async Task<Guid> CreateActiveSessionAsync()
    {
        await LoginAsync();
        var codeResponse = await _client.PostAsJsonAsync("/api/v1/customeragent/support-code",
            new { DeviceIdentifier = _deviceId });
        var codeContent = await codeResponse.Content.ReadFromJsonAsync<SupportCodeResponse>();
        var code = codeContent!.Code;

        var connectResponse = await _client.PostAsJsonAsync("/api/v1/supportagent/connect",
            new { SupportCode = code });
        var connectJson = await connectResponse.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(connectJson);
        var sessionId = doc.RootElement.GetProperty("sessionId").GetGuid();

        var agentAuth = _client.DefaultRequestHeaders.Authorization;
        var tokenResponse = await _client.PostAsJsonAsync("/api/v1/customeragent/device-token",
            new { DeviceIdentifier = _deviceId });
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDoc = System.Text.Json.JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = new("Bearer", tokenDoc.RootElement.GetProperty("token").GetString());
        (await _client.PostAsJsonAsync("/api/v1/customeragent/accept",
            new { SessionId = sessionId })).EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.Authorization = agentAuth;

        return sessionId;
    }

    private async Task<string> GetTransportTokenAsync(Guid sessionId)
    {
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _agentToken);
        var r = await _client.PostAsJsonAsync("/api/v1/transport/token",
            new { SessionId = sessionId });
        var content = await r.Content.ReadFromJsonAsync<TransportTokenResponse>();
        return content!.Token;
    }

    private async Task<string> GetCustomerTransportTokenAsync(Guid sessionId)
    {
        var r = await _client.PostAsJsonAsync("/api/v1/transport/customer-token",
            new { SessionId = sessionId, DeviceIdentifier = _deviceId });
        var content = await r.Content.ReadFromJsonAsync<TransportTokenResponse>();
        return content!.Token;
    }

    private record SupportCodeResponse(string Code, DateTime ExpiresAtUtc);
    private record TransportTokenResponse(string Token, DateTime ExpiresAtUtc, string? SessionKey);

    // --- Transport Token Tests ---

    [Fact]
    public async Task TransportToken_IssueAgentToken_ReturnsToken()
    {
        var sessionId = await CreateActiveSessionAsync();
        var token = await GetTransportTokenAsync(sessionId);

        Assert.False(string.IsNullOrEmpty(token));
        Assert.Contains(".", token);
    }

    [Fact]
    public async Task TransportToken_IssueCustomerToken_ReturnsToken()
    {
        var sessionId = await CreateActiveSessionAsync();
        var token = await GetCustomerTransportTokenAsync(sessionId);

        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task TransportToken_EndedSession_ReturnsBadRequest()
    {
        var sessionId = await CreateActiveSessionAsync();

        // Terminate session
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _agentToken);
        await _client.PostAsJsonAsync($"/api/v1/supportagent/session/{sessionId}/terminate",
            new { Reason = "test" });

        var r = await _client.PostAsJsonAsync("/api/v1/transport/token",
            new { SessionId = sessionId });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

}
