using RemoteSupport.Server.Application.Services;

namespace RemoteSupport.Server.Tests.Application;

public class SupportCodeServiceTests : IDisposable
{
    private readonly RemoteSupport.Server.Infrastructure.Persistence.ApplicationDbContext _context;
    private readonly SupportCodeService _service;

    public SupportCodeServiceTests()
    {
        _context = TestHelpers.CreateInMemoryContext();
        _service = new SupportCodeService(_context);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task GenerateCode_CreatesUniqueCode()
    {
        var code = await _service.GenerateCodeAsync("device-001");

        Assert.NotEqual(Guid.Empty, code.Id);
        Assert.False(string.IsNullOrEmpty(code.Code));
        Assert.Equal(8, code.Code.Length);
        Assert.True(code.Code.All(char.IsDigit));
        Assert.Equal("device-001", code.DeviceIdentifier);
        Assert.False(code.IsUsed);
    }

    [Fact]
    public async Task GenerateCode_GeneratesDifferentCodes()
    {
        var code1 = await _service.GenerateCodeAsync("device-001");
        var code2 = await _service.GenerateCodeAsync("device-001");

        Assert.NotEqual(code1.Code, code2.Code);
    }

    [Fact]
    public async Task ValidateCode_ReturnsCode_WhenValid()
    {
        var generated = await _service.GenerateCodeAsync("device-001");

        var validated = await _service.ValidateCodeAsync(generated.Code);

        Assert.NotNull(validated);
        Assert.Equal(generated.Id, validated!.Id);
    }

    [Fact]
    public async Task ValidateCode_AcceptsDigitsWithSpaces()
    {
        var generated = await _service.GenerateCodeAsync("device-001");
        var spaced = generated.Code.Insert(4, " ");

        var validated = await _service.ValidateCodeAsync(spaced);

        Assert.NotNull(validated);
        Assert.Equal(generated.Id, validated!.Id);
    }

    [Fact]
    public async Task ValidateCode_ReturnsNull_WhenCodeNotFound()
    {
        var result = await _service.ValidateCodeAsync("NONEXISTENT");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateCode_ReturnsNull_WhenCodeExpired()
    {
        var generated = await _service.GenerateCodeAsync("device-001");

        // Manually expire the code
        generated.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await _context.SaveChangesAsync();

        var result = await _service.ValidateCodeAsync(generated.Code);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveCodes_ReturnsOnlyUnused()
    {
        var c1 = await _service.GenerateCodeAsync("d1");
        var c2 = await _service.GenerateCodeAsync("d2");

        // Mark c2 as used
        c2.IsUsed = true;
        await _context.SaveChangesAsync();

        var active = await _service.GetActiveCodesAsync();

        Assert.Single(active);
        Assert.Equal(c1.Code, active[0].Code);
    }

    [Fact]
    public async Task ValidateCode_AllowsReuse_WhenPreviousSessionEnded()
    {
        var generated = await _service.GenerateCodeAsync("device-001");
        generated.IsUsed = true;
        generated.UsedBySessionId = Guid.NewGuid();
        await _context.SaveChangesAsync();

        var validated = await _service.ValidateCodeAsync(generated.Code);

        Assert.NotNull(validated);
        Assert.False(validated!.IsUsed);
        Assert.Null(validated.UsedBySessionId);
    }

    [Fact]
    public async Task ValidateCode_AllowsReconnect_WhenCodeStillBoundToActiveSession()
    {
        var generated = await _service.GenerateCodeAsync("device-001");
        var sessionId = Guid.NewGuid();
        _context.Sessions.Add(new RemoteSupport.Server.Domain.Entities.Session
        {
            Id = sessionId,
            AgentUserId = Guid.NewGuid(),
            CustomerDeviceIdentifier = "device-001",
            Status = RemoteSupport.Server.Domain.Enums.SessionStatus.Active
        });
        generated.IsUsed = true;
        generated.UsedBySessionId = sessionId;
        await _context.SaveChangesAsync();

        var validated = await _service.ValidateCodeAsync(generated.Code);

        Assert.NotNull(validated);
        Assert.Equal(generated.Id, validated!.Id);
    }
}
