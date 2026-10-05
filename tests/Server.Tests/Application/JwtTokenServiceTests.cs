using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Tests.Application;

public class JwtTokenServiceTests
{
    private readonly JwtTokenService _service;

    public JwtTokenServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "TestSecretKeyForDevelopment_12345678901234567890!",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience",
                ["Jwt:AccessTokenExpirationMinutes"] = "30"
            })
            .Build();

        _service = new JwtTokenService(config);
    }

    [Fact]
    public void GenerateAccessToken_ReturnsValidJwt()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Role = UserRole.SupportAgent
        };

        var token = _service.GenerateAccessToken(user);

        Assert.False(string.IsNullOrEmpty(token));

        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(token);

        Assert.Equal("TestIssuer", jwtToken.Issuer);
        Assert.Equal("TestAudience", jwtToken.Audiences.First());
        Assert.Contains(jwtToken.Claims, c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier && c.Value == user.Id.ToString());
        Assert.Contains(jwtToken.Claims, c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "SupportAgent");
    }

    [Fact]
    public void GenerateCustomerAccessToken_ReturnsValidJwt()
    {
        var sessionId = Guid.NewGuid();
        var deviceIdentifier = "device-001";

        var token = _service.GenerateCustomerAccessToken(sessionId, deviceIdentifier);

        Assert.False(string.IsNullOrEmpty(token));

        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(token);

        Assert.Contains(jwtToken.Claims, c => c.Type == "session_id" && c.Value == sessionId.ToString());
        Assert.Contains(jwtToken.Claims, c => c.Type == "device_identifier" && c.Value == deviceIdentifier);
        Assert.Contains(jwtToken.Claims, c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "Customer");
    }

    [Fact]
    public void GenerateRefreshToken_ReturnsBase64String()
    {
        var token = _service.GenerateRefreshToken();

        Assert.False(string.IsNullOrEmpty(token));
        Assert.True(Convert.FromBase64String(token).Length > 0);
    }

    [Fact]
    public void GenerateRefreshToken_GeneratesDifferentTokens()
    {
        var token1 = _service.GenerateRefreshToken();
        var token2 = _service.GenerateRefreshToken();

        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void HashToken_ReturnsConsistentHash()
    {
        var token = "test-token-value";
        var hash1 = _service.HashToken(token);
        var hash2 = _service.HashToken(token);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashToken_ReturnsDifferentHashesForDifferentTokens()
    {
        var hash1 = _service.HashToken("token-1");
        var hash2 = _service.HashToken("token-2");

        Assert.NotEqual(hash1, hash2);
    }
}
