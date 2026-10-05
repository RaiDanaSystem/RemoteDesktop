using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public class SessionEstablishmentTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly IPasswordHasher _passwordHasher = new PasswordHasher();
    private readonly string _deviceId = "test-device-001";

    private static readonly JsonSerializerOptions Opt = new() { PropertyNameCaseInsensitive = true };

    public SessionEstablishmentTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            
            builder.UseSetting("RelayServer:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (d != null) services.Remove(d);
                services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase("SessTest"));
            });
        });
        _client = _factory.CreateClient();
    }

    public void Dispose() { _client.Dispose(); _factory.Dispose(); }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Clear previous test data
        db.AuditLogs.RemoveRange(db.AuditLogs);
        db.SessionEvents.RemoveRange(db.SessionEvents);
        db.FileTransfers.RemoveRange(db.FileTransfers);
        db.Sessions.RemoveRange(db.Sessions);
        db.SupportCodes.RemoveRange(db.SupportCodes);
        db.RefreshTokens.RemoveRange(db.RefreshTokens);
        db.Users.RemoveRange(db.Users);
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "agent", Email = "a@t.com",
            PasswordHash = _passwordHasher.HashPassword("Pass1!"),
            Role = UserRole.SupportAgent, IsActive = true, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task AuthAsync()
    {
        await SeedAsync();
        var r = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("agent", "Pass1!"));
        var lr = (await r.Content.ReadFromJsonAsync<LoginResponse>())!;
        _client.DefaultRequestHeaders.Authorization =
            new("Bearer", lr.AccessToken.Token);
    }

    private async Task<string> NewCodeAsync()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/customeragent/support-code", new { DeviceIdentifier = _deviceId });
        var c = (await r.Content.ReadFromJsonAsync<CodeResp>())!;
        return c.Code;
    }

    private async Task<Guid> ConnectAsync(string? code = null, bool ensureAuth = true)
    {
        if (ensureAuth) await AuthAsync();
        code ??= await NewCodeAsync();
        var r = await _client.PostAsJsonAsync("/api/v1/supportagent/connect", new { SupportCode = code });
        var j = await r.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(j);
        return doc.RootElement.GetProperty("sessionId").GetGuid();
    }

    private async Task AcceptAsync(Guid sid)
    {
        var previous = _client.DefaultRequestHeaders.Authorization;
        var token = await GetDeviceTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        (await _client.PostAsJsonAsync("/api/v1/customeragent/accept",
            new { SessionId = sid })).EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.Authorization = previous;
    }

    private async Task<string> GetDeviceTokenAsync()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/customeragent/device-token",
            new { DeviceIdentifier = _deviceId });
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<string> StatusAsync(Guid sid, bool agent = true)
    {
        if (agent)
            return await (await _client.GetAsync($"/api/v1/supportagent/session/{sid}/status")).Content.ReadAsStringAsync();

        var previous = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await GetDeviceTokenAsync());
        var json = await (await _client.GetAsync($"/api/v1/customeragent/session/{sid}/status")).Content.ReadAsStringAsync();
        _client.DefaultRequestHeaders.Authorization = previous;
        return json;
    }

    private record CodeResp(string Code, DateTime ExpiresAtUtc);

    // --- Tests ---

    [Fact]
    public async Task Connect_ValidCode_ReturnsPending()
    {
        await AuthAsync();
        var code = await NewCodeAsync();
        var r = await _client.PostAsJsonAsync("/api/v1/supportagent/connect", new { SupportCode = code });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Pending", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Connect_InvalidCode_ReturnsBadRequest()
    {
        await AuthAsync();
        var r = await _client.PostAsJsonAsync("/api/v1/supportagent/connect", new { SupportCode = "NOPE" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Connect_ExpiredCode_ReturnsBadRequest()
    {
        await AuthAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SupportCodes.Add(new SupportCode
        {
            Id = Guid.NewGuid(), Code = "EXP1", DeviceIdentifier = _deviceId,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5), IsUsed = false, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var r = await _client.PostAsJsonAsync("/api/v1/supportagent/connect", new { SupportCode = "EXP1" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutAuth_ReturnsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var r = await _client.PostAsJsonAsync("/api/v1/supportagent/connect", new { SupportCode = "X" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Poll_NoPending_ReturnsFalse()
    {
        var r = await _client.GetAsync("/api/v1/customeragent/connection-request?code=NOPE");
        Assert.Contains("false", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accept_ValidSession_Activates()
    {
        var sid = await ConnectAsync();
        await AcceptAsync(sid);
        Assert.Contains("Active", await StatusAsync(sid, false));
    }

    [Fact]
    public async Task Reject_EndsSession()
    {
        var sid = await ConnectAsync();
        var previous = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await GetDeviceTokenAsync());
        (await _client.PostAsJsonAsync("/api/v1/customeragent/reject",
            new { SessionId = sid })).EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.Authorization = previous;
        Assert.Contains("Ended", await StatusAsync(sid, false));
    }

    [Fact]
    public async Task Accept_WithoutDeviceToken_ReturnsUnauthorized()
    {
        var sid = await ConnectAsync();
        _client.DefaultRequestHeaders.Authorization = null;
        var r = await _client.PostAsJsonAsync("/api/v1/customeragent/accept",
            new { SessionId = sid });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task AgentTerminate_EndsSession()
    {
        var sid = await ConnectAsync();
        await AcceptAsync(sid);
        (await _client.PostAsJsonAsync($"/api/v1/supportagent/session/{sid}/terminate",
            new { Reason = "done" })).EnsureSuccessStatusCode();
        Assert.Contains("Ended", await StatusAsync(sid));
    }

    [Fact]
    public async Task CustomerTerminate_EndsSession()
    {
        var sid = await ConnectAsync();
        await AcceptAsync(sid);
        var previous = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await GetDeviceTokenAsync());
        (await _client.PostAsJsonAsync(
            $"/api/v1/customeragent/session/{sid}/terminate",
            new { Reason = "bye" })).EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.Authorization = previous;
        Assert.Contains("Ended", await StatusAsync(sid, false));
    }

    [Fact]
    public async Task DuplicateActiveSession_ReturnsBadRequest()
    {
        await ConnectAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SupportCodes.Add(new SupportCode
        {
            Id = Guid.NewGuid(), Code = "C2", DeviceIdentifier = _deviceId,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10), IsUsed = false, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var r = await _client.PostAsJsonAsync("/api/v1/supportagent/connect", new { SupportCode = "C2" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task FullFlow_Connect_Accept_Terminate()
    {
        await AuthAsync();
        var code = await NewCodeAsync();
        var sid = await ConnectAsync(code, ensureAuth: false);
        Assert.Contains("hasPendingRequest", await (await _client.GetAsync(
            $"/api/v1/customeragent/connection-request?code={code}")).Content.ReadAsStringAsync());
        await AcceptAsync(sid);
        Assert.Contains("Active", await StatusAsync(sid));
        (await _client.PostAsJsonAsync($"/api/v1/supportagent/session/{sid}/terminate",
            new { Reason = "done" })).EnsureSuccessStatusCode();
        Assert.Contains("Ended", await StatusAsync(sid));
    }
}
